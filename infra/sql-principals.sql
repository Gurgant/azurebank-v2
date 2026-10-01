-- Connect directly to AzureBank as its Entra administrator, with sqlcmd -b.
-- AppPassword and MigratorPassword come from process environment variables.
-- sqlcmd substitution is textual: use ONLY generated alphanumeric passwords
-- as described in README.md. Never supply arbitrary text or use sqlcmd -e/-v.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF DB_NAME() <> N'AzureBank'
    THROW 50000, 'Connect directly to the AzureBank database.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'azurebank_app')
    CREATE USER [azurebank_app] WITH PASSWORD = '$(AppPassword)';

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'azurebank_migrator')
    CREATE USER [azurebank_migrator] WITH PASSWORD = '$(MigratorPassword)';

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
