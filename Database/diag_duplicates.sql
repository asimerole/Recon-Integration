/* ============================================================================
   ДІАГНОСТИКА ДУБЛІВ — ТІЛЬКИ ЧИТАННЯ, нічого не змінює.
   Запустити в SSMS (Query → Results to Text, Ctrl+T),
   надіслати весь результат. Може виконуватись кілька хвилин на 500k рядків.
   ============================================================================ */
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;  

PRINT '=== 0. Середовище ===';
SELECT @@VERSION AS SqlVersion, DB_NAME() AS Db,
       (SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) AS CompatLevel;

PRINT '=== 0.1 Колонки ===';
SELECT t.name AS [table], c.name AS [column], ty.name AS [type], c.max_length, c.is_nullable
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name IN ('data', 'data_process', 'groups_data', 'struct', 'struct_units', 'units')
ORDER BY t.name, c.column_id;

PRINT '=== 0.2 Індекси ===';
SELECT t.name AS [table], i.name AS [index], i.type_desc, i.is_unique, i.is_primary_key,
       STUFF((SELECT ', ' + c.name
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS key_columns,
       STUFF((SELECT ', ' + c.name
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
              FOR XML PATH('')), 1, 2, '') AS included_columns
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE t.name IN ('data', 'data_process', 'groups_data', 'struct', 'struct_units') AND i.type > 0
ORDER BY t.name, i.name;

PRINT '=== 0.3 Зовнішні ключі та тригери ===';
SELECT fk.name AS fk, OBJECT_NAME(fk.parent_object_id) AS from_table, OBJECT_NAME(fk.referenced_object_id) AS to_table
FROM sys.foreign_keys fk
WHERE OBJECT_NAME(fk.referenced_object_id) IN ('data', 'struct');
SELECT name AS trigger_on_data FROM sys.triggers WHERE parent_id = OBJECT_ID('dbo.data');

PRINT '=== 0.4 Текст usp_GetAnalyticsDuplicates ===';
SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.usp_GetAnalyticsDuplicates')) AS usp_GetAnalyticsDuplicates;

PRINT '=== 1. Обсяг ===';
SELECT COUNT(*) AS rows_total,
       COUNT(data_file) AS with_recon,
       COUNT(express_file) AS with_rexpr,
       SUM(CASE WHEN data_file IS NOT NULL AND express_file IS NOT NULL THEN 1 ELSE 0 END) AS full_pairs,
       MIN([date]) AS min_date, MAX([date]) AS max_date
FROM data;

SELECT ISNULL(NULLIF(file_type, ''), '<порожньо>') AS file_type, COUNT(*) AS rows
FROM data GROUP BY ISNULL(NULLIF(file_type, ''), '<порожньо>') ORDER BY rows DESC;

PRINT '=== a. Один реєстратор кілька разів у struct (recon_id + object) ===';
SELECT COUNT(*) AS duplicated_registrators, ISNULL(SUM(cnt), 0) AS struct_rows_involved
FROM (SELECT COUNT(*) AS cnt FROM struct GROUP BY recon_id, [object] HAVING COUNT(*) > 1) x;

SELECT TOP 30 recon_id, [object], COUNT(*) AS structs, MIN(id) AS min_id, MAX(id) AS max_id
FROM struct GROUP BY recon_id, [object] HAVING COUNT(*) > 1 ORDER BY COUNT(*) DESC;

PRINT '=== a2. Одна папка кілька разів у struct ===';
SELECT TOP 30 files_path, COUNT(*) AS structs
FROM struct WHERE files_path IS NOT NULL GROUP BY files_path HAVING COUNT(*) > 1 ORDER BY COUNT(*) DESC;

PRINT '=== b. Та сама подія записана в різні struct одного реєстратора ===';
SELECT COUNT(*) AS events_in_several_structs, ISNULL(SUM(n), 0) AS rows_involved
FROM (SELECT COUNT(*) AS n
      FROM data d JOIN struct s ON s.id = d.struct_id
      GROUP BY s.recon_id, s.[object], d.file_num, d.[date], d.[time]
      HAVING COUNT(DISTINCT d.struct_id) > 1) x;

PRINT '=== c. Розщеплені пари: окремо RECON і окремо REXPR, той самий struct + номер, ±1 доба ===';
SELECT COUNT(*) AS split_pairs
FROM data a
JOIN data b ON b.struct_id = a.struct_id AND b.file_num = a.file_num AND b.id <> a.id
           AND b.[date] BETWEEN DATEADD(DAY, -1, a.[date]) AND DATEADD(DAY, 1, a.[date])
WHERE a.data_file IS NOT NULL AND a.express_file IS NULL
  AND b.express_file IS NOT NULL AND b.data_file IS NULL;

PRINT '=== c2. Будь-які близнюки: той самий struct + номер + сімейство, ±1 доба ===';
WITH f AS (
    SELECT id, struct_id, file_num, [date],
           CASE WHEN file_type IN ('RECON', 'REXPR') OR file_type IS NULL OR file_type = '' THEN 'RECON' ELSE file_type END AS family
    FROM data
)
SELECT COUNT(*) AS rows_having_a_twin
FROM f a
WHERE EXISTS (SELECT 1 FROM f b
              WHERE b.struct_id = a.struct_id AND b.file_num = a.file_num AND b.family = a.family
                AND b.id <> a.id AND b.[date] BETWEEN DATEADD(DAY, -1, a.[date]) AND DATEADD(DAY, 1, a.[date]));

PRINT '=== d. Сміттєві дати ===';
SELECT YEAR([date]) AS [year], COUNT(*) AS rows
FROM data
WHERE [date] < '2000-01-01' OR [date] > DATEADD(DAY, 1, CAST(GETDATE() AS DATE))
GROUP BY YEAR([date]) ORDER BY [year];

PRINT '=== e. Повні копії: struct + номер + дата + час ===';
SELECT COUNT(*) AS groups, ISNULL(SUM(n), 0) AS rows_involved
FROM (SELECT COUNT(*) AS n FROM data GROUP BY struct_id, file_num, [date], [time] HAVING COUNT(*) > 1) x;

PRINT '=== f. "Дублі за іменем" (як в аналітиці) і скільки з них — просто цикл номерів 000-999 ===';
WITH x AS (
    SELECT s.recon_id, d.file_type, d.file_num,
           DATEDIFF(DAY, LAG(d.[date]) OVER (PARTITION BY s.recon_id, d.file_type, d.file_num ORDER BY d.[date]), d.[date]) AS gap
    FROM data d JOIN struct s ON s.id = d.struct_id
)
SELECT COUNT(*) AS groups_by_name,
       ISNULL(SUM(n), 0) AS rows_by_name,
       ISNULL(SUM(CASE WHEN min_gap > 1 THEN 1 ELSE 0 END), 0) AS groups_natural_cycle
FROM (SELECT recon_id, file_type, file_num, COUNT(*) AS n, MIN(gap) AS min_gap
      FROM x GROUP BY recon_id, file_type, file_num HAVING COUNT(*) > 1) g;

PRINT '=== g. Скільки близнюків прив''язані до груп Viewer (groups_data) ===';
WITH f AS (
    SELECT id, struct_id, file_num, [date],
           CASE WHEN file_type IN ('RECON', 'REXPR') OR file_type IS NULL OR file_type = '' THEN 'RECON' ELSE file_type END AS family
    FROM data
)
SELECT COUNT(*) AS group_links_to_twins
FROM groups_data gd
JOIN f a ON a.id = gd.data_id
WHERE EXISTS (SELECT 1 FROM f b
              WHERE b.struct_id = a.struct_id AND b.file_num = a.file_num AND b.family = a.family
                AND b.id <> a.id AND b.[date] BETWEEN DATEADD(DAY, -1, a.[date]) AND DATEADD(DAY, 1, a.[date]));

PRINT '=== h. Рядки data без struct ===';
SELECT COUNT(*) AS rows_without_struct
FROM data d WHERE NOT EXISTS (SELECT 1 FROM struct s WHERE s.id = d.struct_id);
