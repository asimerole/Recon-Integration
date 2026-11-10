/* ============================================================================
   ЗЛИТТЯ ДУБЛІВ У data
   За замовчуванням @DryRun = 1: усе виконується в транзакції, показує що буде
   зроблено, і ВІДКОЧУЄТЬСЯ. Для застосування поставити @DryRun = 0.
   Перед @DryRun = 0 — зробити бекап бази.

   Ті самі правила, що в інтеграції (FileDataRepository.SqlUpsert):
     a) обидва записи мають REXPR       → одна подія лише при однаковому часі (±2 с)
     b) REXPR лише в одного (подія E), в іншого лише RECON (час файлу T):
        T від -65 хв до +49 год від E; якщо RECON в обох — однаковий розмір
     c) REXPR немає в жодного (RECON, DAILY, RNET_…) → ±65 хв і однакові розміри
   Реєстратори скидають нумерацію (новий рік, перезавантаження), тож однаковий номер
   поруч у часі — це НЕ завжди дубль. Такі записи не чіпаються.

   Залишається запис з REXPR (точний час події); при рівності — менший id.
   В нього зливаються відсутні файли, data_process і посилання groups_data.
   ============================================================================ */
DECLARE @DryRun BIT = 1;

SET NOCOUNT ON;
SET XACT_ABORT ON;   -- any error rolls everything back

IF OBJECT_ID('tempdb..#f') IS NOT NULL DROP TABLE #f;
IF OBJECT_ID('tempdb..#c') IS NOT NULL DROP TABLE #c;
IF OBJECT_ID('tempdb..#m') IS NOT NULL DROP TABLE #m;

BEGIN TRANSACTION;

SELECT id, struct_id, file_num, [date],
       CASE WHEN file_type IN ('RECON', 'REXPR') OR file_type IS NULL OR file_type = '' THEN 'RECON' ELSE file_type END AS family,
       CASE WHEN express_file IS NULL THEN 0 ELSE 1 END AS has_rexpr,
       DATALENGTH(data_file) AS recon_size,
       DATALENGTH(other_type_file) AS other_size,
       DATEADD(MILLISECOND,
               DATEDIFF(MILLISECOND, CAST('00:00' AS TIME), ISNULL([time], CAST('00:00' AS TIME))),
               CAST([date] AS DATETIME2(3))) AS at
INTO #f
FROM data WITH (TABLOCKX);   -- nobody writes while we merge

CREATE INDEX IX_f ON #f (struct_id, file_num, family, [date]);

-- Couples (a.id < b.id) that are the same event
SELECT a.id AS a_id, b.id AS b_id,
       a.has_rexpr AS a_rexpr, b.has_rexpr AS b_rexpr,
       ABS(DATEDIFF(SECOND, a.at, b.at)) AS gap_sec,
       CONCAT(CASE WHEN a.recon_size IS NULL THEN 0 ELSE 1 END, a.has_rexpr, CASE WHEN a.other_size IS NULL THEN 0 ELSE 1 END, '/',
              CASE WHEN b.recon_size IS NULL THEN 0 ELSE 1 END, b.has_rexpr, CASE WHEN b.other_size IS NULL THEN 0 ELSE 1 END) AS kind
INTO #c
FROM #f a
JOIN #f b ON b.struct_id = a.struct_id AND b.file_num = a.file_num AND b.family = a.family
         AND b.id > a.id
         AND b.[date] BETWEEN DATEADD(DAY, -3, a.[date]) AND DATEADD(DAY, 3, a.[date])
WHERE
      (a.has_rexpr = 1 AND b.has_rexpr = 1 AND ABS(DATEDIFF(MILLISECOND, a.at, b.at)) <= 2000)
   OR (a.has_rexpr = 1 AND b.has_rexpr = 0
       AND DATEDIFF(MINUTE, a.at, b.at) BETWEEN -65 AND 2940
       AND (a.recon_size IS NULL OR b.recon_size IS NULL OR a.recon_size = b.recon_size))
   OR (a.has_rexpr = 0 AND b.has_rexpr = 1
       AND DATEDIFF(MINUTE, b.at, a.at) BETWEEN -65 AND 2940
       AND (a.recon_size IS NULL OR b.recon_size IS NULL OR a.recon_size = b.recon_size))
   OR (a.has_rexpr = 0 AND b.has_rexpr = 0
       AND ABS(DATEDIFF(MINUTE, a.at, b.at)) <= 65
       AND ISNULL(a.recon_size, -1) = ISNULL(b.recon_size, -1)
       AND ISNULL(a.other_size, -1) = ISNULL(b.other_size, -1));

-- A record in more than one couple is ambiguous: leave it alone, report it
SELECT CASE WHEN c.a_rexpr >= c.b_rexpr THEN c.a_id ELSE c.b_id END AS keep_id,
       CASE WHEN c.a_rexpr >= c.b_rexpr THEN c.b_id ELSE c.a_id END AS lose_id,
       c.kind, c.gap_sec
INTO #m
FROM #c c
WHERE NOT EXISTS (SELECT 1 FROM #c o
                  WHERE (o.a_id <> c.a_id OR o.b_id <> c.b_id)
                    AND (o.a_id IN (c.a_id, c.b_id) OR o.b_id IN (c.a_id, c.b_id)));

PRINT '=== Буде злито (kind: RECON/REXPR/інший запису a / запису b) ===';
SELECT kind, COUNT(*) AS couples, MAX(gap_sec) / 60 AS max_gap_min FROM #m GROUP BY kind ORDER BY couples DESC;

PRINT '=== Неоднозначні (запис у кількох парах) — НЕ чіпаються ===';
SELECT COUNT(*) AS ambiguous_couples FROM #c c WHERE NOT EXISTS (SELECT 1 FROM #m m WHERE m.keep_id IN (c.a_id, c.b_id) AND m.lose_id IN (c.a_id, c.b_id));

PRINT '=== Приклади ===';
SELECT TOP 20 s.recon_id, s.[object], k.file_num, m.kind, m.gap_sec / 60 AS gap_min,
       k.id AS keep_id, k.file_type AS keep_type, k.[date] AS keep_date, k.[time] AS keep_time,
       l.id AS lose_id, l.file_type AS lose_type, l.[date] AS lose_date, l.[time] AS lose_time
FROM #m m
JOIN data k ON k.id = m.keep_id
JOIN data l ON l.id = m.lose_id
JOIN struct s ON s.id = k.struct_id
ORDER BY m.kind, k.id;

-- 1. Missing files go into the kept record
UPDATE k SET
    data_file       = COALESCE(k.data_file,       l.data_file),
    express_file    = COALESCE(k.express_file,    l.express_file),
    other_type_file = COALESCE(k.other_type_file, l.other_type_file),
    file_type       = CASE WHEN (k.file_type IN ('RECON', 'REXPR') OR k.file_type IS NULL OR k.file_type = '')
                                AND COALESCE(k.data_file, l.data_file) IS NOT NULL THEN 'RECON'
                           ELSE k.file_type END
FROM data k
JOIN #m m ON m.keep_id = k.id
JOIN data l ON l.id = m.lose_id;
DECLARE @completed INT = @@ROWCOUNT;

-- 2. REXPR fields of the removed record if the kept one has none
INSERT INTO data_process (id, damaged_line, [trigger], event_type)
SELECT m.keep_id, dp.damaged_line, dp.[trigger], dp.event_type
FROM #m m
JOIN data_process dp ON dp.id = m.lose_id
WHERE NOT EXISTS (SELECT 1 FROM data_process k WHERE k.id = m.keep_id);

-- 3. Viewer groups point to the kept record
INSERT INTO groups_data (group_id, data_id)
SELECT DISTINCT gd.group_id, m.keep_id
FROM groups_data gd
JOIN #m m ON m.lose_id = gd.data_id
WHERE NOT EXISTS (SELECT 1 FROM groups_data x WHERE x.group_id = gd.group_id AND x.data_id = m.keep_id);
DECLARE @relinked INT = @@ROWCOUNT;

DELETE gd FROM groups_data gd JOIN #m m ON m.lose_id = gd.data_id;
DELETE dp FROM data_process dp JOIN #m m ON m.lose_id = dp.id;
DELETE d  FROM data d          JOIN #m m ON m.lose_id = d.id;
DECLARE @deleted INT = @@ROWCOUNT;

PRINT '=== Підсумок ===';
SELECT @completed AS records_completed, @deleted AS duplicates_deleted, @relinked AS group_links_moved,
       CASE WHEN @DryRun = 1 THEN 'DRY RUN — усе відкочено' ELSE 'ЗАСТОСОВАНО' END AS mode;

IF @DryRun = 1 ROLLBACK TRANSACTION;
ELSE COMMIT TRANSACTION;

DROP TABLE #m;
DROP TABLE #c;
DROP TABLE #f;
