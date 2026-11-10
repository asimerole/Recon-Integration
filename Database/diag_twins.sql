/* ============================================================================
   ДІАГНОСТИКА БЛИЗНЮКІВ — ТІЛЬКИ ЧИТАННЯ, нічого не змінює.
   Близнюки = той самий struct + номер файлу + сімейство (RECON/REXPR разом), ±1 доба.
   Запустити в SSMS (Ctrl+T — Results to Text), надіслати весь результат.
   ============================================================================ */
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;   -- не блокувати інтеграцію

IF OBJECT_ID('tempdb..#f') IS NOT NULL DROP TABLE #f;
IF OBJECT_ID('tempdb..#tw') IS NOT NULL DROP TABLE #tw;

SELECT id, struct_id, file_num, [date], [time], file_type,
       CASE WHEN data_file       IS NULL THEN 0 ELSE 1 END AS has_recon,
       CASE WHEN express_file    IS NULL THEN 0 ELSE 1 END AS has_rexpr,
       CASE WHEN other_type_file IS NULL THEN 0 ELSE 1 END AS has_other,
       CASE WHEN file_type IN ('RECON', 'REXPR') OR file_type IS NULL OR file_type = '' THEN 'RECON' ELSE file_type END AS family,
       DATEADD(MILLISECOND,
               DATEDIFF(MILLISECOND, CAST('00:00' AS TIME), ISNULL([time], CAST('00:00' AS TIME))),
               CAST([date] AS DATETIME2(3))) AS at
INTO #f
FROM data;

CREATE INDEX IX_f ON #f (struct_id, file_num, family, [date]);

-- Each twin couple once (a.id < b.id). kind = "recon rexpr other", e.g. 110 = RECON+REXPR
SELECT a.id AS a_id, b.id AS b_id, a.struct_id, a.file_num, a.family,
       CONCAT(a.has_recon, a.has_rexpr, a.has_other) AS a_kind,
       CONCAT(b.has_recon, b.has_rexpr, b.has_other) AS b_kind,
       a.at AS a_at, b.at AS b_at, a.file_type AS a_type, b.file_type AS b_type,
       ABS(DATEDIFF(MINUTE, a.at, b.at)) AS gap_min
INTO #tw
FROM #f a
JOIN #f b ON b.struct_id = a.struct_id AND b.file_num = a.file_num AND b.family = a.family
         AND b.id > a.id
         AND b.[date] BETWEEN DATEADD(DAY, -1, a.[date]) AND DATEADD(DAY, 1, a.[date]);

PRINT '=== 1. Склад близнюків (kind: RECON/REXPR/інший, 1 = файл є) ===';
SELECT family, a_kind, b_kind, COUNT(*) AS couples,
       SUM(CASE WHEN gap_min <= 5    THEN 1 ELSE 0 END) AS within_5min,
       SUM(CASE WHEN gap_min <= 60   THEN 1 ELSE 0 END) AS within_1h,
       SUM(CASE WHEN gap_min <= 1440 THEN 1 ELSE 0 END) AS within_24h,
       MAX(gap_min) AS max_gap_min
FROM #tw
GROUP BY family, a_kind, b_kind
ORDER BY couples DESC;

PRINT '=== 2. Групи з трьох і більше записів ===';
SELECT COUNT(*) AS rows_in_3plus_groups
FROM (SELECT a_id AS id FROM #tw UNION SELECT b_id FROM #tw) x
WHERE (SELECT COUNT(*) FROM #tw t WHERE t.a_id = x.id OR t.b_id = x.id) >= 2;

PRINT '=== 3. Приклади (по 5 кожного складу) ===';
WITH r AS (
    SELECT t.*, ROW_NUMBER() OVER (PARTITION BY a_kind, b_kind ORDER BY a_id) AS rn
    FROM #tw t
)
SELECT s.recon_id, s.[object], r.file_num, r.a_id, r.a_kind, r.a_type, r.a_at,
       r.b_id, r.b_kind, r.b_type, r.b_at, r.gap_min
FROM r JOIN struct s ON s.id = r.struct_id
WHERE r.rn <= 5
ORDER BY r.a_kind, r.b_kind, r.a_id;

PRINT '=== 4. Хто пише дати в майбутньому (годинник реєстратора) ===';
SELECT TOP 20 s.recon_id, s.[object], COUNT(*) AS rows, MIN(d.[date]) AS min_date, MAX(d.[date]) AS max_date
FROM data d JOIN struct s ON s.id = d.struct_id
WHERE d.[date] > DATEADD(DAY, 1, CAST(GETDATE() AS DATE))
GROUP BY s.recon_id, s.[object]
ORDER BY COUNT(*) DESC;

DROP TABLE #tw;
DROP TABLE #f;
