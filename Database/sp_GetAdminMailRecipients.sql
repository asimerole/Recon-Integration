CREATE OR ALTER PROCEDURE sp_GetAdminMailRecipients
AS
BEGIN
    SET NOCOUNT ON;

    SELECT login
    FROM users
    WHERE status = 1
      AND send_mail = 1
      AND type = N'Адмін';
END
GO
