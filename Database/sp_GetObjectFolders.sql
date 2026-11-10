CREATE OR ALTER PROCEDURE sp_GetObjectFolders
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT files_path AS FilesPath
    FROM struct
    WHERE files_path IS NOT NULL
      AND LTRIM(RTRIM(files_path)) <> '';
END
GO
