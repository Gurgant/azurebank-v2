-- The two database users of AzureBank, each bound to a managed identity. No password exists.
-- Run by sql-principals.ps1 with sqlcmd, connected to the AzureBank database as the server's
-- Microsoft Entra administrator, and always with -b: without it a THROW below would exit 0.
--   azurebank_app       reads and writes rows                (the identity azurebank-app)
--   azurebank_migrator  the same, and may change the schema  (the identity azurebank-migrate)
-- A second run changes nothing. A user whose identity was deleted and made again is replaced.
--
-- This file runs as the owner of the database, and so does a DDL trigger that its statements
-- fire. The migrator may create such a trigger (db_ddladmin), and the form of CREATE USER used
-- here asks the directory nothing: through a trigger it could make anyone in the tenant an owner.
-- So the file
--   1. refuses to run in a database that holds a trigger or any other module (the app's
--      migrations create none), and
--   2. before it commits, compares every user, role, role member, permission and schema owner
--      with what it expects. One thing more or less and nothing is kept: the THROW rolls back
--      this transaction, and with it whatever a trigger did inside it.
-- That is a guard, not a proof that the database is clean: a trigger that ran in some other
-- statement of an administrator could have done what no list here looks at. Run nothing but this
-- file as administrator in this database (README.md).
SET XACT_ABORT ON;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() <> N'AzureBank'
    THROW 50000, 'Connect to the AzureBank database, not to master.', 1;

-- sqlcmd puts the five values in as text before the server reads the file. They are identifiers,
-- not secrets. The uniqueidentifier type stops a typing mistake and nothing more: what keeps
-- other text out is the runner, which parses each ID and passes on what it parsed. This file
-- cannot do it: a value that carried a statement would run here, before the transaction below
-- begins, and the lists before the commit would name what it left without undoing it.
DECLARE @AppClientId uniqueidentifier = '$(AppClientId)';
DECLARE @AppObjectId uniqueidentifier = '$(AppObjectId)';
DECLARE @MigratorClientId uniqueidentifier = '$(MigratorClientId)';
DECLARE @MigratorObjectId uniqueidentifier = '$(MigratorObjectId)';
-- Sid: CREATE USER ... WITH SID, TYPE = E, which asks the directory nothing.
-- ExternalProvider: CREATE USER ... FROM EXTERNAL PROVIDER WITH OBJECT_ID, which looks the
-- identity up by its object ID, never by its name; the user it makes carries the client ID.
DECLARE @CreateForm nvarchar(20) = N'$(CreateForm)';

IF @CreateForm NOT IN (N'Sid', N'ExternalProvider')
    THROW 50002, 'CreateForm is Sid or ExternalProvider.', 1;
IF @AppClientId = @MigratorClientId OR @AppObjectId = @MigratorObjectId
    THROW 50001, 'The app and the migrate job must be two different identities.', 1;

-- The ID each user must carry: its identity's client ID, in the byte order the server stores.
-- The object ID is used for one thing only, to name the identity in the second form of CREATE USER.
DECLARE @AppSid varbinary(16) = CONVERT(varbinary(16), @AppClientId);
DECLARE @MigratorSid varbinary(16) = CONVERT(varbinary(16), @MigratorClientId);
DECLARE @statement nvarchar(max);
DECLARE @found nvarchar(max);
DECLARE @bad nvarchar(2000) = N'';

BEGIN TRANSACTION;

-- 1. No code may live here. A SELECT fires no trigger, so nothing has run when this stops.
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(code.name), N'(no name)')), N', ')
              FROM (SELECT name FROM sys.triggers
                    UNION ALL
                    SELECT OBJECT_NAME(object_id) FROM sys.sql_modules
                    WHERE object_id NOT IN (SELECT object_id FROM sys.triggers)) AS code);
IF @found IS NOT NULL
BEGIN
    PRINT N'Code found: ' + LEFT(@found, 3500);
    THROW 50003, 'Code found in the database (a trigger or a module). Nothing was run. Treat the database as tampered with.', 1;
END

-- 2. The two users and their five memberships.
-- An identity deleted and made again has a new ID: the user it left matches nothing and is replaced.
IF EXISTS (SELECT 1 FROM sys.database_principals
           WHERE name = N'azurebank_app' AND (sid <> @AppSid OR type <> 'E'))
    DROP USER [azurebank_app];
IF EXISTS (SELECT 1 FROM sys.database_principals
           WHERE name = N'azurebank_migrator' AND (sid <> @MigratorSid OR type <> 'E'))
    DROP USER [azurebank_migrator];

-- Each statement is built from a typed value, never from the text sqlcmd put in.
IF DATABASE_PRINCIPAL_ID(N'azurebank_app') IS NULL
BEGIN
    SET @statement = IIF(@CreateForm = N'Sid',
        N'CREATE USER [azurebank_app] WITH SID = ' + CONVERT(nvarchar(34), @AppSid, 1) + N', TYPE = E;',
        N'CREATE USER [azurebank_app] FROM EXTERNAL PROVIDER WITH OBJECT_ID = ''' + CONVERT(nvarchar(36), @AppObjectId) + N''';');
    EXEC (@statement);
END
IF DATABASE_PRINCIPAL_ID(N'azurebank_migrator') IS NULL
BEGIN
    SET @statement = IIF(@CreateForm = N'Sid',
        N'CREATE USER [azurebank_migrator] WITH SID = ' + CONVERT(nvarchar(34), @MigratorSid, 1) + N', TYPE = E;',
        N'CREATE USER [azurebank_migrator] FROM EXTERNAL PROVIDER WITH OBJECT_ID = ''' + CONVERT(nvarchar(36), @MigratorObjectId) + N''';');
    EXEC (@statement);
END

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

-- 3. Before the commit: everything that can hold a right, listed whole. Each list is what is
-- there and should not be. What is printed is a name, never an ID, so that a baseline that
-- differs on another engine is read at once and not guessed.
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(code.name), N'(no name)')), N', ')
              FROM (SELECT name FROM sys.triggers
                    UNION ALL
                    SELECT OBJECT_NAME(object_id) FROM sys.sql_modules
                    WHERE object_id NOT IN (SELECT object_id FROM sys.triggers)) AS code);
IF @found IS NOT NULL
BEGIN
    SET @bad += N'code; ';
    PRINT N'Code found: ' + LEFT(@found, 3500);
END

-- Every user beyond the four the engine makes, compared by name and by stored ID together.
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(name) + N' '
                                + type_desc COLLATE DATABASE_DEFAULT, N'(no name)')), N', ')
              FROM sys.database_principals
              WHERE principal_id > 4 AND type <> 'R'
                AND NOT (type = 'E' AND ((name = N'azurebank_app' AND sid = @AppSid)
                                      OR (name = N'azurebank_migrator' AND sid = @MigratorSid))));
IF @found IS NOT NULL
BEGIN
    SET @bad += N'unknown user; ';
    PRINT N'Unknown user: ' + LEFT(@found, 3500);
END

SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(name), N'(no name)')), N', ')
              FROM sys.database_principals
              WHERE type = 'R' AND is_fixed_role = 0 AND name <> N'public');
IF @found IS NOT NULL
BEGIN
    SET @bad += N'unknown role; ';
    PRINT N'Unknown role: ' + LEFT(@found, 3500);
END

-- Every membership of every role, db_owner included. dbo in db_owner is how the engine makes a database.
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(USER_NAME(m.member_principal_id)) + N' in '
                                + QUOTENAME(USER_NAME(m.role_principal_id)), N'(no name)')), N', ')
              FROM sys.database_role_members AS m
              WHERE NOT ((m.member_principal_id = 1 AND USER_NAME(m.role_principal_id) = N'db_owner')
                      OR (USER_NAME(m.member_principal_id) = N'azurebank_app'
                          AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter'))
                      OR (USER_NAME(m.member_principal_id) = N'azurebank_migrator'
                          AND USER_NAME(m.role_principal_id) IN (N'db_datareader', N'db_datawriter', N'db_ddladmin'))));
IF @found IS NOT NULL
BEGIN
    SET @bad += N'unknown role member; ';
    PRINT N'Unknown role member: ' + LEFT(@found, 3500);
END
IF (SELECT COUNT(*) FROM sys.database_role_members WHERE member_principal_id > 4) <> 5
    SET @bad += N'our role memberships are not five; ';

-- Expected: CONNECT for dbo and for the two users, and what the engine grants to public (two
-- database permissions about encryption key metadata, and its grants on system objects).
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(d.state_desc COLLATE DATABASE_DEFAULT + N' '
                                + d.permission_name COLLATE DATABASE_DEFAULT + N' ('
                                + d.class_desc COLLATE DATABASE_DEFAULT + N') to '
                                + QUOTENAME(USER_NAME(d.grantee_principal_id)), N'(no name)')), N', ')
              FROM sys.database_permissions AS d
              WHERE NOT (d.class = 0 AND d.type = 'CO' AND d.state = 'G'
                         AND USER_NAME(d.grantee_principal_id) IN (N'dbo', N'azurebank_app', N'azurebank_migrator'))
                AND NOT (d.grantee_principal_id = 0 AND d.state = 'G'
                         AND ((d.class = 1 AND d.major_id < 0) OR (d.class = 0 AND d.type IN ('VWCK', 'VWCM')))));
IF @found IS NOT NULL
BEGIN
    SET @bad += N'unknown permission; ';
    PRINT N'Unknown permission: ' + LEFT(@found, 3500);
END

SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), ISNULL(QUOTENAME(s.name) + N' owned by ' + QUOTENAME(p.name), N'(no name)')), N', ')
              FROM sys.schemas AS s
              JOIN sys.database_principals AS p ON p.principal_id = s.principal_id
              WHERE p.type <> 'R' AND p.principal_id NOT IN (1, 2, 3, 4));
IF @found IS NOT NULL
BEGIN
    SET @bad += N'schema owned by a user; ';
    PRINT N'Schema owned by a user: ' + LEFT(@found, 3500);
END

IF @bad <> N''
BEGIN
    SET @bad = N'Not committed: ' + @bad;
    THROW 50004, @bad, 1;
END

COMMIT TRANSACTION;

-- What the runner waits for: each user with its roles, read back from the catalog after the
-- commit. "ID as asked: 1" says the stored ID is the one given; the IDs are never printed.
DECLARE @asked nchar(1);
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), USER_NAME(role_principal_id)), N', ')
                     WITHIN GROUP (ORDER BY USER_NAME(role_principal_id))
              FROM sys.database_role_members
              WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_app'));
SET @asked = IIF(EXISTS (SELECT 1 FROM sys.database_principals
                         WHERE name = N'azurebank_app' AND type = 'E' AND sid = @AppSid), N'1', N'0');
PRINT N'azurebank_app: ' + ISNULL(@found, N'no role') + N'; ID as asked: ' + @asked;
SET @found = (SELECT STRING_AGG(CONVERT(nvarchar(max), USER_NAME(role_principal_id)), N', ')
                     WITHIN GROUP (ORDER BY USER_NAME(role_principal_id))
              FROM sys.database_role_members
              WHERE member_principal_id = DATABASE_PRINCIPAL_ID(N'azurebank_migrator'));
SET @asked = IIF(EXISTS (SELECT 1 FROM sys.database_principals
                         WHERE name = N'azurebank_migrator' AND type = 'E' AND sid = @MigratorSid), N'1', N'0');
PRINT N'azurebank_migrator: ' + ISNULL(@found, N'no role') + N'; ID as asked: ' + @asked;
