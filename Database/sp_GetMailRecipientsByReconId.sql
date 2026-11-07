-- Recipients of event files (RECON/REXPR/DAILY) for one object.
--   @Restricted = 1 ("Пошта — обмежити контент по об'єктах" is on):
--       admins + users bound to the object (struct) or to its unit in user_object_bindings
--   @Restricted = 0: every active user with mailing switched on
CREATE OR ALTER PROCEDURE sp_GetMailRecipientsByReconId
    @ReconId    INT,
    @ObjectName NVARCHAR(200) = NULL,
    @Restricted BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT u.login
    FROM users u
    WHERE u.status = 1
      AND u.send_mail = 1
      AND (
          @Restricted = 0
          OR u.type = N'Адмін'
          OR EXISTS (
              SELECT 1
              FROM user_object_bindings uob
              WHERE uob.user_id = u.id
                AND (
                    -- bound to this exact object
                    uob.struct_id IN (
                        SELECT s.id FROM struct s
                        WHERE s.recon_id = @ReconId
                          AND (@ObjectName IS NULL OR s.object = @ObjectName)
                    )
                    OR
                    -- bound to the whole unit that contains the object
                    (uob.struct_id IS NULL AND uob.unit_id IN (
                        SELECT su.unit_id
                        FROM struct_units su
                        JOIN struct s ON su.struct_id = s.id
                        WHERE s.recon_id = @ReconId
                          AND (@ObjectName IS NULL OR s.object = @ObjectName)
                    ))
                )
          )
      );
END
GO
