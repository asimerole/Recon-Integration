--   status: 0 = pending, 1 = sent, 2 = failed (gave up), 3 = skipped (stale / no recipients)

IF OBJECT_ID('dbo.mail_outbox', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.mail_outbox (
        id              BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_mail_outbox PRIMARY KEY,
        recon_id        INT            NOT NULL,
        object          NVARCHAR(255)  NOT NULL,
        substation      NVARCHAR(255)  NULL,
        event_time      DATETIME2(0)   NOT NULL,
        file_paths      NVARCHAR(MAX)  NOT NULL,   -- full paths in the storage, '|' separated
        created_at      DATETIME2(0)   NOT NULL CONSTRAINT DF_mail_outbox_created DEFAULT SYSDATETIME(),
        status          TINYINT        NOT NULL CONSTRAINT DF_mail_outbox_status  DEFAULT 0,
        attempts        INT            NOT NULL CONSTRAINT DF_mail_outbox_attempts DEFAULT 0,
        next_attempt_at DATETIME2(0)   NOT NULL CONSTRAINT DF_mail_outbox_next    DEFAULT SYSDATETIME(),
        sent_at         DATETIME2(0)   NULL,
        recipients      NVARCHAR(MAX)  NULL,
        last_error      NVARCHAR(1000) NULL
    );

    CREATE INDEX IX_mail_outbox_pending ON dbo.mail_outbox (status, next_attempt_at) INCLUDE (recon_id, object, created_at);
    CREATE INDEX IX_mail_outbox_object  ON dbo.mail_outbox (recon_id, object, created_at);
END
GO

-- (integration may process a file again). Returns 1 = queued, 0 = duplicate.
CREATE OR ALTER PROCEDURE sp_MailOutbox_Enqueue
    @ReconId    INT,
    @Object     NVARCHAR(255),
    @Substation NVARCHAR(255) = NULL,
    @EventTime  DATETIME2(0),
    @FilePaths  NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM dbo.mail_outbox
               WHERE recon_id = @ReconId AND object = @Object
                 AND created_at > DATEADD(DAY, -3, SYSDATETIME())
                 AND file_paths = @FilePaths)
    BEGIN
        SELECT CAST(0 AS INT);
        RETURN;
    END

    INSERT INTO dbo.mail_outbox (recon_id, object, substation, event_time, file_paths)
    VALUES (@ReconId, @Object, @Substation, @EventTime, @FilePaths);

    SELECT CAST(1 AS INT);
END
GO

CREATE OR ALTER PROCEDURE sp_MailOutbox_GetDue
    @SettleSeconds  INT = 30,
    @MaxWaitSeconds INT = 300,
    @Limit          INT = 500
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @now DATETIME2(0) = SYSDATETIME();

    WITH grp AS (
        SELECT recon_id, object, MAX(created_at) AS last_in, MIN(created_at) AS first_in
        FROM dbo.mail_outbox
        WHERE status = 0 AND next_attempt_at <= @now
        GROUP BY recon_id, object
    )
    SELECT TOP (@Limit)
           o.id AS Id, o.recon_id AS ReconId, o.object AS [Object], o.substation AS Substation,
           o.event_time AS EventTime, o.file_paths AS FilePaths, o.created_at AS CreatedAt, o.attempts AS Attempts
    FROM dbo.mail_outbox o
    JOIN grp g ON g.recon_id = o.recon_id AND g.object = o.object
    WHERE o.status = 0 AND o.next_attempt_at <= @now
      AND (g.last_in  <= DATEADD(SECOND, -@SettleSeconds,  @now)
        OR g.first_in <= DATEADD(SECOND, -@MaxWaitSeconds, @now))
    ORDER BY o.recon_id, o.object, o.event_time;
END
GO

CREATE OR ALTER PROCEDURE sp_MailOutbox_Mark
    @Id                BIGINT,
    @Status            TINYINT,
    @Recipients        NVARCHAR(MAX)  = NULL,
    @Error             NVARCHAR(1000) = NULL,
    @RetryAfterSeconds INT            = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE dbo.mail_outbox
    SET status          = @Status,
        attempts        = attempts + CASE WHEN @Status IN (0, 2) THEN 1 ELSE 0 END,
        sent_at         = CASE WHEN @Status = 1 THEN SYSDATETIME() ELSE sent_at END,
        recipients      = COALESCE(@Recipients, recipients),
        last_error      = @Error,
        next_attempt_at = CASE WHEN @RetryAfterSeconds IS NULL THEN next_attempt_at
                               ELSE DATEADD(SECOND, @RetryAfterSeconds, SYSDATETIME()) END
    WHERE id = @Id;
END
GO

CREATE OR ALTER PROCEDURE sp_MailOutbox_Cleanup
    @KeepDays INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    DELETE FROM dbo.mail_outbox
    WHERE status <> 0 AND created_at < DATEADD(DAY, -@KeepDays, SYSDATETIME());
END
GO
