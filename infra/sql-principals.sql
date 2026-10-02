-- The two database users of AzureBank. Run by sql-principals.ps1, connected to the AzureBank
-- database as the server's Microsoft Entra administrator.
--
-- @AppPassword and @MigratorPassword are bound parameters: no password is ever text in this file,
-- on a command line or in a client's substitution. After a run both users exist with exactly these
-- passwords and these roles, whether they existed before or not, so a second run changes nothing.
--   azurebank_app       reads and writes rows                (the app)
--   azurebank_migrator  the same, and may change the schema  (the migrate job)
SET XACT_ABORT ON;
SET NOCOUNT ON;

IF DB_NAME() <> N'AzureBank'
    THROW 50000, 'Connect to the AzureBank database, not to master.', 1;

-- The generator's alphabet is letters and digits. Anything else is refused here, before it could
-- reach the statement text below; QUOTENAME then quotes what is already safe.
IF @AppPassword IS NULL OR @MigratorPassword IS NULL
   OR LEN(@AppPassword) NOT BETWEEN 32 AND 128 OR LEN(@MigratorPassword) NOT BETWEEN 32 AND 128
   OR @AppPassword COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9]%'
   OR @MigratorPassword COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9]%'
    THROW 50001, 'A password is missing, shorter than 32 or longer than 128 characters, or holds something other than letters and digits.', 1;

DECLARE @statement nvarchar(max);

BEGIN TRANSACTION;

SET @statement = IIF(DATABASE_PRINCIPAL_ID(N'azurebank_app') IS NULL, N'CREATE', N'ALTER')
    + N' USER [azurebank_app] WITH PASSWORD = ' + QUOTENAME(@AppPassword, N'''') + N';';
EXEC (@statement);

SET @statement = IIF(DATABASE_PRINCIPAL_ID(N'azurebank_migrator') IS NULL, N'CREATE', N'ALTER')
    + N' USER [azurebank_migrator] WITH PASSWORD = ' + QUOTENAME(@MigratorPassword, N'''') + N';';
EXEC (@statement);

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datareader')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_app'))
    ALTER ROLE [db_datareader] ADD MEMBER [azurebank_app];

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datawriter')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_app'))
    ALTER ROLE [db_datawriter] ADD MEMBER [azurebank_app];

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datareader')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_migrator'))
    ALTER ROLE [db_datareader] ADD MEMBER [azurebank_migrator];

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'db_datawriter')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_migrator'))
    ALTER ROLE [db_datawriter] ADD MEMBER [azurebank_migrator];

IF NOT EXISTS (SELECT 1 FROM sys.database_role_members
    WHERE role_principal_id = DATABASE_PRINCIPAL_ID(N'db_ddladmin')
      AND member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_migrator'))
    ALTER ROLE [db_ddladmin] ADD MEMBER [azurebank_migrator];

COMMIT TRANSACTION;

-- What the runner prints: each user with its roles, read back from the catalog.
SELECT u.name AS [user], STRING_AGG(r.name, N', ') WITHIN GROUP (ORDER BY r.name) AS [roles]
FROM sys.database_principals AS u
LEFT JOIN sys.database_role_members AS m ON m.member_principal_id = u.principal_id
LEFT JOIN sys.database_principals AS r ON r.principal_id = m.role_principal_id
WHERE u.name IN (N'azurebank_app', N'azurebank_migrator')
GROUP BY u.name
ORDER BY u.name;
