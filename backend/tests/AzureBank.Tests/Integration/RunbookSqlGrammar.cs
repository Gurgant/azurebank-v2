using System.Text.RegularExpressions;

namespace AzureBank.Tests.Integration;

/// <summary>
/// What a T-SQL statement looks like where it OPENS a line of a runbook: the pattern
/// <see cref="RunbookSqlIsFencedTests"/> scans with, and the vocabulary it is built from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a grammar and not a list of verbs.</b> Until 2026-09-18 the pattern was a list of
/// verbs that grew one review at a time: <c>DBCC</c> in one round, <c>DENY TAKE OWNERSHIP</c> in the
/// next. Measured against <c>RunbookSqlCorpus.json</c> — 3,011 statements, every one accepted
/// by SQL Server's own parser, and 376 sentences an operator runbook could hold — that pattern
/// missed 818 of the statements standing alone and reported 133 of the sentences: <i>"Grant the
/// on-call engineer read access to the dashboard"</i>, <i>"Deny update requests from the vendor
/// until the contract has been renewed."</i> A verb plus loose evidence is wrong in both directions
/// at once, because half of T-SQL's verbs are English ones.
/// </para>
/// <para>
/// <b>Two groups.</b> A statement with a SHAPE is recognised by grammar prose does not have:
/// <c>UPDATE</c> reaches its <c>SET</c> and an assignment, <c>GRANT</c> a permission SQL Server
/// knows and a <c>TO</c> that is not followed by "the". A statement with NO shape of its own —
/// <c>COMMIT</c>, <c>USE master</c>, <c>DROP USER auditor</c>, <c>DBCC CHECKDB</c> — must run to a
/// statement boundary: the end, a semicolon, or another statement opening the way prose does not.
/// That is what keeps <i>"Use staging."</i> a sentence, since a name does not end in a full stop.
/// Where a name is due, an English determiner (<c>the</c>, <c>a</c>, <c>this</c>…) says prose.
/// </para>
/// <para>
/// <b>The vocabularies are the server's, not a guess.</b> <see cref="Permissions"/> is what
/// <c>sys.fn_builtin_permissions(DEFAULT)</c> returns on SQL Server 2025, and
/// <see cref="RunbookSqlCorpusSqlServerTests"/> asks the server under test for its list and fails
/// on a name this one lacks. The <c>SET</c> options and the <c>DBCC</c> commands are the documented
/// ones.
/// </para>
/// <para>
/// <b>What it reaches, as numbers.</b> Of the corpus's 3,011 statements the scan reports 2,851,
/// alone and followed by another statement. The other 160 are published in the corpus as
/// <c>notReported</c> and pinned by the same test, so the list is what a reviewer reads instead of
/// finding its entries one round at a time: a literal or an expression with no table
/// (<c>SELECT 1 + 1</c>, <c>PRINT 42</c>), a table called <c>a</c> or <c>this</c>, a logical
/// backup device, an unquoted string argument, undocumented <c>DBCC</c> commands, a join or query
/// hint between the keywords. A paragraph of SQL escapes only if EVERY statement in it is on that
/// list, since each line that opens one is judged. Of the 376 sentences it reports 40
/// (<c>reportedProse</c>), each of them also the opening of valid T-SQL — <i>"Open Questions"</i>
/// is <c>OPEN cursor</c>, <i>"Select 'Production', then open the Overview blade"</i> selects a
/// literal with an alias — and the test's message says what to do with one. Over 507,700 lines
/// of prose in 3,648 Markdown files on the machine it was developed on, the verb list reported 28
/// lines and this reports 8, all of them list items of one to three words of that kind
/// (<i>"- Commit"</i>, <i>"1. Open Bruno"</i>, <i>"5. Create User entity"</i>).
/// </para>
/// <para>
/// <b>What it does not see at all</b>, stated rather than left to be found. SQL inside a sentence
/// or an inline code span: the scan reads what opens a line. A lone <c>GOTO</c> label
/// (<c>finish:</c>), which is every <i>"Note:"</i> in Markdown. A wrapped statement whose next
/// line opens with <c>- </c>, which Markdown and the scan both read as a bullet. And a fence in
/// another language is read as if it were T-SQL, because a statement pasted into a <c>```text</c>
/// fence is the case that matters; a Python or JavaScript fence that spells <c>if (-1 === …</c>
/// would be reported. The runbooks have none.
/// </para>
/// </remarks>
internal static class RunbookSqlGrammar
{
    /// <summary>An object name: bracketed or double-quoted parts, which may hold anything, and
    /// runs of anything but whitespace, a separator, a parenthesis or a quote. So
    /// <c>dbo.Users</c>, <c>[dbo].[Order Details]</c>, <c>#pin_before</c> and
    /// <c>OBJECT::dbo.Users</c> are each one name.</summary>
    private const string Name = @"(?:\[[^\]]*\]|""[^""]*""|[^\s;,()'\[""])+";

    /// <summary>A name in the group with no shape of its own, where the name is all the evidence
    /// there is: it may not look like a path, a URL or inline code. <i>"Use `staging`"</i> and
    /// <i>"Drop table ./tmp/old"</i> are prose.</summary>
    private const string ShapelessName =
        @"(?![`~/]|\x5c|\.{1,2}[/\x5c]|\w+:[/\x5c])(?:\[[^\]]*\]|""[^""]*""|[^\s;,()'\[""`])+";

    /// <summary>An English determiner. Where the grammar puts a name, prose puts one of
    /// these.</summary>
    private const string Determiner = @"(?:the|an?|this|that|your|each|every)\b";

    /// <summary>
    /// What <c>IF</c> and <c>WHILE</c> test. A bare <c>@</c> is not enough — <i>"If @alice is on
    /// call"</i> is a sentence — and neither is a bare parenthesis: <i>"If user(s) cannot sign
    /// in"</i>. A bracketed function is schema-qualified, as T-SQL requires of a scalar function,
    /// because <c>[text](url)</c> is a Markdown link: <i>"If [`useRouter`](#router-object) is not
    /// the best fit"</i> was reported in four real files before this.
    /// </summary>
    private const string Condition =
        @"@@?\w+\s*(?:[=<>!&|^%*/+)]|-(?![a-z])|\.\w+\(|IS\s+(?:NOT\s+)?NULL\b|(?:NOT\s+)?(?:IN|LIKE|BETWEEN)\b)"
        + @" | N?'[^']*'\s*[=<>!] | -?\d+\s*[=<>!] | (?:NOT\s+)?EXISTS\s*\( | [\w.]+\((?!e?s\))"
        + @" | (?:(?:\w+|\[[^\]]+\])\.)+\[[^\]]+\]\("
        + @" | (?:CURRENT_USER|SYSTEM_USER|SESSION_USER|CURRENT_TIMESTAMP)\s*[=<>!]";

    /// <summary>
    /// The 166 permission names <c>sys.fn_builtin_permissions(DEFAULT)</c> reports on SQL Server
    /// 2025 (17.0), which is what <c>GRANT</c>, <c>DENY</c> and <c>REVOKE</c> accept, besides
    /// <c>ALL [PRIVILEGES]</c>.
    /// </summary>
    internal static readonly string[] Permissions =
    [
        "ADMINISTER BULK OPERATIONS", "ADMINISTER DATABASE BULK OPERATIONS", "ALTER",
        "ALTER ANY APPLICATION ROLE", "ALTER ANY ASSEMBLY", "ALTER ANY ASYMMETRIC KEY",
        "ALTER ANY AVAILABILITY GROUP", "ALTER ANY CERTIFICATE", "ALTER ANY COLUMN ENCRYPTION KEY",
        "ALTER ANY COLUMN MASTER KEY", "ALTER ANY CONNECTION", "ALTER ANY CONTRACT",
        "ALTER ANY CREDENTIAL", "ALTER ANY DATABASE", "ALTER ANY DATABASE AUDIT",
        "ALTER ANY DATABASE DDL TRIGGER", "ALTER ANY DATABASE EVENT NOTIFICATION",
        "ALTER ANY DATABASE EVENT SESSION", "ALTER ANY DATABASE EVENT SESSION ADD EVENT",
        "ALTER ANY DATABASE EVENT SESSION ADD TARGET", "ALTER ANY DATABASE EVENT SESSION DISABLE",
        "ALTER ANY DATABASE EVENT SESSION DROP EVENT",
        "ALTER ANY DATABASE EVENT SESSION DROP TARGET", "ALTER ANY DATABASE EVENT SESSION ENABLE",
        "ALTER ANY DATABASE EVENT SESSION OPTION", "ALTER ANY DATABASE SCOPED CONFIGURATION",
        "ALTER ANY DATASPACE", "ALTER ANY ENDPOINT", "ALTER ANY EVENT NOTIFICATION",
        "ALTER ANY EVENT SESSION", "ALTER ANY EVENT SESSION ADD EVENT",
        "ALTER ANY EVENT SESSION ADD TARGET", "ALTER ANY EVENT SESSION DISABLE",
        "ALTER ANY EVENT SESSION DROP EVENT", "ALTER ANY EVENT SESSION DROP TARGET",
        "ALTER ANY EVENT SESSION ENABLE", "ALTER ANY EVENT SESSION OPTION",
        "ALTER ANY EXTERNAL DATA SOURCE", "ALTER ANY EXTERNAL FILE FORMAT",
        "ALTER ANY EXTERNAL JOB", "ALTER ANY EXTERNAL LANGUAGE", "ALTER ANY EXTERNAL LIBRARY",
        "ALTER ANY EXTERNAL MIRROR", "ALTER ANY EXTERNAL MODEL", "ALTER ANY EXTERNAL STREAM",
        "ALTER ANY FULLTEXT CATALOG", "ALTER ANY INFORMATION PROTECTION", "ALTER ANY LINKED SERVER",
        "ALTER ANY LOGIN", "ALTER ANY MASK", "ALTER ANY MESSAGE TYPE",
        "ALTER ANY REMOTE SERVICE BINDING", "ALTER ANY ROLE", "ALTER ANY ROUTE", "ALTER ANY SCHEMA",
        "ALTER ANY SECURITY POLICY", "ALTER ANY SENSITIVITY CLASSIFICATION",
        "ALTER ANY SERVER AUDIT", "ALTER ANY SERVER ROLE", "ALTER ANY SERVICE",
        "ALTER ANY SYMMETRIC KEY", "ALTER ANY USER", "ALTER LEDGER", "ALTER LEDGER CONFIGURATION",
        "ALTER RESOURCES", "ALTER SERVER STATE", "ALTER SETTINGS", "ALTER TRACE", "AUTHENTICATE",
        "AUTHENTICATE SERVER", "BACKUP DATABASE", "BACKUP LOG", "CHECKPOINT", "CONNECT",
        "CONNECT ANY DATABASE", "CONNECT REPLICATION", "CONNECT SQL", "CONTROL", "CONTROL SERVER",
        "CREATE AGGREGATE", "CREATE ANY DATABASE", "CREATE ANY DATABASE EVENT SESSION",
        "CREATE ANY EVENT SESSION", "CREATE ASSEMBLY", "CREATE ASYMMETRIC KEY",
        "CREATE AVAILABILITY GROUP", "CREATE CERTIFICATE", "CREATE CONTRACT", "CREATE DATABASE",
        "CREATE DATABASE DDL EVENT NOTIFICATION", "CREATE DDL EVENT NOTIFICATION", "CREATE DEFAULT",
        "CREATE ENDPOINT", "CREATE EXTERNAL LANGUAGE", "CREATE EXTERNAL LIBRARY",
        "CREATE EXTERNAL MODEL", "CREATE FULLTEXT CATALOG", "CREATE FUNCTION", "CREATE LOGIN",
        "CREATE MESSAGE TYPE", "CREATE PROCEDURE", "CREATE QUEUE", "CREATE REMOTE SERVICE BINDING",
        "CREATE ROLE", "CREATE ROUTE", "CREATE RULE", "CREATE SCHEMA", "CREATE SEQUENCE",
        "CREATE SERVER ROLE", "CREATE SERVICE", "CREATE SYMMETRIC KEY", "CREATE SYNONYM",
        "CREATE TABLE", "CREATE TRACE EVENT NOTIFICATION", "CREATE TYPE", "CREATE USER",
        "CREATE VIEW", "CREATE XML SCHEMA COLLECTION", "DELETE", "DROP ANY DATABASE EVENT SESSION",
        "DROP ANY EVENT SESSION", "ENABLE LEDGER", "EXECUTE", "EXECUTE ANY EXTERNAL ENDPOINT",
        "EXECUTE ANY EXTERNAL SCRIPT", "EXECUTE EXTERNAL SCRIPT", "EXTERNAL ACCESS ASSEMBLY",
        "IMPERSONATE", "IMPERSONATE ANY LOGIN", "INSERT", "KILL DATABASE CONNECTION", "RECEIVE",
        "REFERENCES", "SELECT", "SELECT ALL USER SECURABLES", "SEND", "SHOWPLAN", "SHUTDOWN",
        "SUBSCRIBE QUERY NOTIFICATIONS", "TAKE OWNERSHIP", "UNMASK", "UNSAFE ASSEMBLY", "UPDATE",
        "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION", "VIEW ANY COLUMN MASTER KEY DEFINITION",
        "VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION", "VIEW ANY DATABASE", "VIEW ANY DEFINITION",
        "VIEW ANY ERROR LOG", "VIEW ANY PERFORMANCE DEFINITION", "VIEW ANY SECURITY DEFINITION",
        "VIEW ANY SENSITIVITY CLASSIFICATION", "VIEW CHANGE TRACKING",
        "VIEW CRYPTOGRAPHICALLY SECURED DEFINITION", "VIEW DATABASE PERFORMANCE STATE",
        "VIEW DATABASE SECURITY AUDIT", "VIEW DATABASE SECURITY STATE", "VIEW DATABASE STATE",
        "VIEW DEFINITION", "VIEW LEDGER CONTENT", "VIEW PERFORMANCE DEFINITION",
        "VIEW SECURITY DEFINITION", "VIEW SERVER PERFORMANCE STATE", "VIEW SERVER SECURITY AUDIT",
        "VIEW SERVER SECURITY STATE", "VIEW SERVER STATE",
    ];

    /// <summary>
    /// The pattern, with <c>&lt;N&gt;</c> for <see cref="Name"/>, <c>&lt;SN&gt;</c> for
    /// <see cref="ShapelessName"/>, <c>&lt;DET&gt;</c> for <see cref="Determiner"/>,
    /// <c>&lt;COND&gt;</c> for <see cref="Condition"/> and <c>&lt;PERM&gt;</c> for any of
    /// <see cref="Permissions"/>. It is read with <see cref="RegexOptions.IgnorePatternWhitespace"/>,
    /// so its layout and its <c>#</c> comments are for the reader. Optional whitespace is spelt so
    /// it can be consumed one way only — <c>(?:\(\s*)?</c>, never <c>\(?\s*</c>. Measured in .NET:
    /// a draft with the looser spelling ran past a 20-second timeout on a 1,008-character line
    /// (<c>return</c>, 500 spaces, <c>1</c>, 500 spaces, <c>x</c>), where this one answers in 1.4
    /// ms; over the same 420 timing inputs its worst is 0.4 s, on 5,000 characters of <c>a:a:a:</c>.
    /// </summary>
    private const string Template = @"
^(?!\s*BEGIN\s+(?:ROLLBACK|COMMIT|SHUTDOWN|CHECKPOINT|REVERT|RETURN|BREAK|CONTINUE|THROW|RECONFIGURE)\s*$)\s*(?:;\s*)?
  # A statement can sit behind a label, inside BEGIN or BEGIN TRY, or be the body of a procedure.
  (?: \w+:\s* | BEGIN\s+(?:TRY\s+|CATCH\s+)? | (?:CREATE|ALTER)\s+PROC(?:EDURE)?\s+<N>\s+AS\s+ )*
  (?:
    # ---- Queries and data changes
      SELECT\s+(?:(?:ALL|DISTINCT)\s+)?(?:TOP\s*(?:\(\s*)?[\d@]\w*\s*(?:\)\s*)?(?:PERCENT\s+)?)?
        (?: @ | [\w.]+\((?!e?s\)) | (?:(?:\w+|\[[^\]]+\])\.)+\[[^\]]+\]\( | \(\s*SELECT\b | -?\d+\s*,(?!\s*(?:then|and|or|
            confirm)\b) | N?'[^']*'\s*(?:,|AS\b) | CASE\s+(?:WHEN\b|@) | NEXT\s+VALUE\s+FOR\b
          | (?:CURRENT_TIMESTAMP|CURRENT_USER|SYSTEM_USER|SESSION_USER)\b | -?\d+\s+AS\s+\w+\s*(?:$|[;,]) | N?'[^']*'\s*(?:;|
              $) )
    | (?:SELECT|RECEIVE)\b(?!\s+<DET>).*?\bFROM\s+(?!<DET>)<N>(?:\s+(?:AS\s+)?(?:\w+|\[[^\]]+\]))?
        (?:\s*,\s*<N>(?:\s+(?:AS\s+)?(?:\w+|\[[^\]]+\]))?)*\s*
        (?: (?<!\s)\( | \(\s*(?:@|N?'|-?\d|\)|DEFAULT\b|NULL\b|SELECT\b|[\w\[\]]+\.[\w\[\]]+\s*[,)]
               |(?:NOLOCK|READPAST|READUNCOMMITTED|READCOMMITTED|READCOMMITTEDLOCK|REPEATABLEREAD|SERIALIZABLE|HOLDLOCK
                  |UPDLOCK|XLOCK|TABLOCK|TABLOCKX|PAGLOCK|ROWLOCK|NOWAIT|SNAPSHOT|NOEXPAND|FORCESEEK|FORCESCAN|INDEX)\b)
          | (?:WHERE|JOIN|UNION|EXCEPT|INTERSECT)\b | (?:ORDER|GROUP)\s+BY\b | INTO\s+@
          | (?:INNER|LEFT|RIGHT|FULL|CROSS|OUTER)\s+(?:OUTER\s+)?(?:JOIN|APPLY)\b
          | WITH\s*\( | OPTION\s*\( | FOR\s+(?:XML|JSON|BROWSE|SYSTEM_TIME)\b )
    | INSERT\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?(?:INTO\s+)?(?!<DET>)<N>\s*(?:WITH\s*\([^)]*\)\s*)?(?:\([^)]*\)\s*)?
        (?:VALUES\s*(?:\(|$)|\(?\s*SELECT\b|EXEC\b|EXECUTE\b|DEFAULT\s+VALUES\b|OUTPUT\s+\[?(?:inserted|deleted)\]?\.)
    | BULK\s+INSERT\s+<N>\s+FROM\s+N?'
    | UPDATE\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?<N>\s+(?:WITH\s*\([^)]*\)\s*)?
        SET\s+(?:\[[^\]]+\]|""[^""]+""|[\w.@$])+\s*(?:[-+*/%&|^]?=|\.WRITE\s*\(|\()
    | UPDATE\s+STATISTICS\s+<N>(?:\s*\([^)]*\)|\s+<N>)?\s+
        WITH\s+(?:FULLSCAN|SAMPLE|RESAMPLE|NORECOMPUTE|INCREMENTAL|ALL|COLUMNS|INDEX|MAXDOP|AUTO_DROP
          |PERSIST_SAMPLE_PERCENT|STATS_STREAM|ROWCOUNT|PAGECOUNT)\b
    | DELETE\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?(?:FROM\s+)?<N>\s+(?:WITH\s*\([^)]*\)\s*)?
        (?: OUTPUT\s+(?:\[?deleted\]?\.|\d+\s+WHERE\b)
          | (?:FROM\s+<N>\s+(?:(?:AS\s+)?(?:\w+|\[[^\]]+\])\s+)?)?
              (?: (?:(?:INNER|LEFT|RIGHT|FULL|CROSS|OUTER)\s+(?:OUTER\s+)?)?(?:JOIN|APPLY)\b
                | WHERE(?:\s+|(?=\())(?: CURRENT\s+OF\b | (?:NOT\s+)?EXISTS\s*\(
                            | (?:\(\s*|NOT\s+)*(?:\[[^\]]+\]|""[^""]+""|[\w.@])+\s*(?:[=<>!(%*/+-]|(?:NOT\s+)?(?:IN|LIKE|
                                BETWEEN)\b|IS\s+(?:NOT\s+)?NULL\b)
                            | NOT\s*\( ) )
          | (?:WITH|OPTION)\s*\( [^)]*\) \s*;?\s*$ )
    | MERGE\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?(?:INTO\s+)?<N>\s+(?:WITH\s*\([^)]*\)\s*)?(?:(?:AS\s+)?(?:\w+|
        \[[^\]]+\])\s+)?
        USING\s*(?: \( | <N>\s+(?:(?:AS\s+)?(?:\w+|\[[^\]]+\])\s+)?ON\s+(?:\(\s*)?[\w.\[\]]+\s*[=<>] )
    | WITH\s+(?: XMLNAMESPACES\s*\( | CHANGE_TRACKING_CONTEXT\s*\( | <N>\s*(?:\([^)]*\)\s*)?AS\s*\( )
    | TRUNCATE\s+TABLE\s+<N>\s+WITH\s*\(\s*PARTITIONS\b
    | (?:READTEXT|WRITETEXT|UPDATETEXT)\s+\S
    # A statement cut short because its next line opens with a temp table, which the scan reads as a heading.
    | (?: (?:SELECT|RECEIVE)\b(?!\s+<DET>).*\b(?:FROM|INTO|JOIN) | (?:INSERT|MERGE)\s+INTO | DELETE\s+FROM
        | (?:CREATE|ALTER|DROP|TRUNCATE)\s+TABLE )\s*$

    # ---- Variables, flow, messages
    | DECLARE\s+(?: @\w+ | \w+\s+(?:INSENSITIVE\s+)?(?:SCROLL\s+)?CURSOR\b )
    | SET\s+(?: @\w+(?:\.\w+)*\s*(?:[-+*/%&|^]?=|\.\w+\s*\()
              | TRAN(?:SACTION)?\s+ISOLATION\s+LEVEL\b
              | STATISTICS\s+(?:IO|PROFILE|TIME|XML)(?:\s*,\s*(?:IO|PROFILE|TIME|XML))*\s+(?:ON|OFF)\b
              | OFFSETS\s+\w+(?:\s*,\s*\w+)*\s+(?:ON|OFF)\b
              | (?:ANSI_DEFAULTS|ANSI_NULL_DFLT_OFF|ANSI_NULL_DFLT_ON|ANSI_NULLS|ANSI_PADDING|ANSI_WARNINGS
                  |ARITHABORT|ARITHIGNORE|CONCAT_NULL_YIELDS_NULL|CONTEXT_INFO|CURSOR_CLOSE_ON_COMMIT
                  |DATEFIRST|DATEFORMAT|DEADLOCK_PRIORITY|FIPS_FLAGGER|FMTONLY|FORCEPLAN|IDENTITY_INSERT
                  |IMPLICIT_TRANSACTIONS|LOCK_TIMEOUT|NOCOUNT|NOEXEC|NUMERIC_ROUNDABORT|PARSEONLY
                  |QUERY_GOVERNOR_COST_LIMIT|QUOTED_IDENTIFIER|REMOTE_PROC_TRANSACTIONS|RESULT_SET_CACHING|ROWCOUNT
                  |SHOWPLAN_ALL|SHOWPLAN_TEXT|SHOWPLAN_XML|TEXTSIZE|XACT_ABORT)\b )
    | (?:IF|WHILE)\s*(?:NOT\s+)?(?: \(+\s*SELECT\b | (?:\(+\s*)?(?: <COND> ) )
    | PRINT\s*(?: N?' | @ | \( | [\w.]+\((?!e?s\)) )
    | RAISERROR\s*\(
    | THROW\s+(?:\d+|@\w+)\s*,\s*(?:N?'|@)
    | WAITFOR\s*(?: \( | (?:DELAY|TIME)\b )
    | GOTO\s+\w+\s*(?:;|$|--|/\*|\w+:)
    | (?:BEGIN|END)\s+(?:TRY|CATCH)(?![-\w])
    | EXEC(?:UTE)?\s*\(\s*(?:N?'|@)
    | EXEC(?:UTE)?\s+AS\s+(?: (?:LOGIN|USER)\s*= | CALLER\b )
    | EXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?!(?:steps?|phases?|stages?|tasks?|items?|options?|runbook|script|
        command)\b)<N>\s+(?: @@?\w+ | N?' | -?\d+\s*, | -?\d+\.\d+ | (?:NULL|DEFAULT)\s*(?:,|;|$)
          | (?:-?\d+\s+)?WITH\s+(?:RECOMPILE|RESULT\s+SETS)\b )
    | EXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?:[\w\[\]]+\.){0,3}\[?(?:sp|xp)_\w+
    | (?:[\w\[\]]*\.){0,3}\[?(?:u?sp|xp)_\w+\]?\s+(?: @\w+ | N?' | -?\d )
    | <N>\s+@\w+\s*=

    # ---- Transactions, cursors, keys, Service Broker
    | BEGIN\s+(?:DISTRIBUTED\s+)?TRAN(?:SACTION)?\s+[\w@]+\s+WITH\s+MARK\b
    | COMMIT\s+TRAN(?:SACTION)?\s+(?:[\w@]+\s+)?WITH\s*\(\s*DELAYED_DURABILITY\b
    | OPEN\s+(?:MASTER\s+KEY|SYMMETRIC\s+KEY\s+<N>)\s+DECRYPTION\s+BY\b
    | (?:ADD|DROP)\s+(?: (?:COUNTER\s+)?SIGNATURE\s+(?:TO|FROM)\s+<N>\s+BY\b
                       | SENSITIVITY\s+CLASSIFICATION\s+(?:TO|FROM)\b )
    | (?:SEND\s+ON|END|MOVE)\s+CONVERSATION\s*[@'(]
    | GET\s+CONVERSATION\s+GROUP\s+@
    | BEGIN\s+(?: DIALOG\s+(?:CONVERSATION\s+)?@ | CONVERSATION\s+TIMER\s*\( )

    # ---- Permissions: the names are sys.fn_builtin_permissions(DEFAULT) on SQL Server 2025
    | (?:GRANT|DENY|REVOKE(?:\s+GRANT\s+OPTION\s+FOR)?)\s+
        (?: <PERM>\b(?:\s*\([^)]*\))?\s*(?:,\s*)? )+
        (?:ON\s+(?:\w+\s+){0,3}?<N>\s*(?:\([^)]*\)\s*)?)?(?:TO|FROM)\s+(?!<DET>)[\w\[""]
    | REVERT\s+WITH\s+COOKIE\s*=
    | SETUSER\b

    # ---- CREATE, ALTER, DROP: an object type that is not English needs no more than its name...
    | (?:CREATE|ALTER|DROP)\s+
        (?: OR\s+ALTER | (?:UNIQUE\s+)?(?:(?:NON)?CLUSTERED\s+)?(?:COLUMNSTORE\s+)?INDEX\s+<N>\s+ON | NONCLUSTERED |
            COLUMNSTORE
          | (?:SPATIAL|JSON|VECTOR|(?:PRIMARY\s+|SELECTIVE\s+)?XML)\s+INDEX | FULLTEXT\s+(?:CATALOG|INDEX|STOPLIST) |
              A?SYMMETRIC\s+KEY
          | SERVICE\s+MASTER\s+KEY | COLUMN\s+(?:ENCRYPTION|MASTER)\s+KEY | CRYPTOGRAPHIC\s+PROVIDER
          | DATABASE\s+(?:AUDIT\s+SPECIFICATION|ENCRYPTION\s+KEY|SCOPED\s+(?:CREDENTIAL|CONFIGURATION))
          | SERVER\s+AUDIT\s+SPECIFICATION | PARTITION\s+(?:FUNCTION|SCHEME)
          | EXTERNAL\s+(?:DATA\s+SOURCE|FILE\s+FORMAT|LANGUAGE|LIBRARY|MODEL|RESOURCE\s+POOL|STREAM|TABLE)
          | EVENT\s+NOTIFICATION | REMOTE\s+SERVICE\s+BINDING | BROKER\s+PRIORITY
          | SEARCH\s+PROPERTY\s+LIST | XML\s+SCHEMA\s+COLLECTION | RESOURCE\s+(?:GOVERNOR|POOL)
          | WORKLOAD\s+(?:GROUP|CLASSIFIER) | AUTHORIZATION\s+ON | SERVER\s+CONFIGURATION\s+SET )\b
    | DROP\s+(?:\w+\s+){1,3}IF\s+EXISTS\b
    # ---- ...and one that is English too is followed past the object name, to the clause its grammar puts there
    | (?:CREATE|ALTER)\s+TABLE\s+<N>\s*
        (?: \(\s*(?:<N>\s+AS\s|(?:CONSTRAINT|PRIMARY|UNIQUE|FOREIGN|CHECK|INDEX|PERIOD)\b|<N>\s+<N>\s*(?:\(|,|$|\)\s*(?:$|;|
            --|/\*|WITH\b|ON\b|AS\b|TEXTIMAGE_ON\b)|\s(?:NOT\s+NULL|NULL|IDENTITY|PRIMARY|CONSTRAINT|DEFAULT|COLLATE|UNIQUE|
            REFERENCES|CHECK|SPARSE|ROWGUIDCOL|GENERATED|MASKED|ENCRYPTED|FOREIGN|INDEX|PERSISTED|HIDDEN|FILESTREAM)\b)) |
            SET\s*\( | AS\s+(?:FILETABLE|NODE|EDGE)\b
          | (?:ADD|ALTER\s+COLUMN|DROP|WITH\s+(?:NO)?CHECK|(?:NO)?CHECK\s+CONSTRAINT|ENABLE|DISABLE|SWITCH|REBUILD
              |SPLIT\s+RANGE|MERGE\s+RANGE)\b )
    | (?:CREATE|ALTER)\s+INDEX\s+<N>\s+ON\s+<N>\s*
        (?: \( | SET\s*\( | (?:REBUILD|REORGANIZE|DISABLE|RESUME|PAUSE|ABORT)\b )
    | (?:CREATE|ALTER)\s+VIEW\s+<N>\s*(?:\([^)]*\)\s*)?(?:WITH\s+(?:[\w,]+\s+)+?)?AS\s+(?:SELECT|WITH)\b
    | (?:CREATE|ALTER)\s+PROC(?:EDURE)?\s+<N>\s*(?:;\s*\d+\s*)?
        (?: @ | \(\s*@ | WITH\s+(?:ENCRYPTION|RECOMPILE|EXEC|EXECUTE|NATIVE_COMPILATION|SCHEMABINDING)\b |
            FOR\s+REPLICATION\b )
    | (?:CREATE|ALTER)\s+FUNCTION\s+<N>\s*\(\s*(?:@|\)|$)
    | (?:CREATE|ALTER)\s+TRIGGER\s+<N>\s+ON\s+(?:ALL\s+SERVER|<N>)\s+(?:WITH\s+(?:\S+\s+)+?)?(?:FOR|AFTER|INSTEAD\s+OF)\b
    | (?:CREATE|ALTER)\s+DATABASE\s+<N>\s+
        (?: SET\s+\w | MODIFY\s+(?:NAME|FILE|FILEGROUP)\b | ADD\s+(?:LOG\s+)?FILE | REMOVE\s+FILE | COLLATE\s+\w
          | CONTAINMENT\s*= | ON\s*(?:PRIMARY\s*)?\( | AS\s+(?:SNAPSHOT|COPY)\s+OF\b | FOR\s+ATTACH | WITH\s+\w+\s*= )
    | (?:CREATE|ALTER)\s+LOGIN\s+<N>\s+
        (?: WITH\s+\w+\s*= | FROM\s+(?:WINDOWS|CERTIFICATE|ASYMMETRIC\s+KEY|EXTERNAL\s+PROVIDER)\b
          | ENABLE\b | DISABLE\b | (?:ADD|DROP)\s+CREDENTIAL\b )
    | (?:CREATE|ALTER)\s+USER\s+<N>\s+
        (?: (?:FOR|FROM)\s+(?:LOGIN|CERTIFICATE|ASYMMETRIC\s+KEY|EXTERNAL\s+PROVIDER)\b | WITHOUT\s+LOGIN\b | WITH\s+\w+\s*= )
    | (?:CREATE|ALTER)\s+(?:(?:SERVER|APPLICATION)\s+)?ROLE\s+<N>\s+
        (?: AUTHORIZATION\s | (?:ADD|DROP)\s+MEMBER\b | WITH\s+\w+\s*= )
    | (?:CREATE|ALTER)\s+SCHEMA\s+<N>\s+(?:AUTHORIZATION|TRANSFER)\s
    | (?:CREATE|ALTER)\s+SEQUENCE\s+<N>\s+
        (?: AS\s+\[?(?:tinyint|smallint|int|bigint|decimal|numeric|[\w\]]+\.\[?\w+)\b | START\s+WITH\b | RESTART\b |
            INCREMENT\s+BY\b | (?:NO\s+)?(?:MINVALUE|MAXVALUE|CYCLE|CACHE)\b )
    | CREATE\s+TYPE\s+<N>\s+(?: FROM\s+(?!<DET>)\[?\w+\]?\s*(?:\(|;|$|NOT\s+NULL|NULL\b) | AS\s+TABLE\b | EXTERNAL\s+NAME\b )
    | CREATE\s+STATISTICS\s+<N>\s+ON\s+<N>\s*\(
    | (?:CREATE|ALTER)\s+MASTER\s+KEY\s+(?: ENCRYPTION\s+BY | (?:ADD|DROP)\s+ENCRYPTION | (?:FORCE\s+)?REGENERATE )\b
    | CREATE\s+RULE\s+<N>\s+AS\s+@ | CREATE\s+DEFAULT\s+<N>\s+AS\s+(?:N?'|[-(\d]|[\w.]+\()
    | (?:CREATE|ALTER|DROP)\s+EVENT\s+SESSION\s+<N>\s+ON\s+(?:SERVER|DATABASE)\b
    | (?:CREATE|ALTER)\s+AVAILABILITY\s+GROUP\s+<N>\s+
        (?: WITH\s*\( | FOR\s+(?:DATABASE|REPLICA)\b | SET\s*\( | FAILOVER\b | FORCE_FAILOVER_ALLOW_DATA_LOSS\b | OFFLINE\b
          | (?:ADD|REMOVE|MODIFY|GRANT|DENY)\s | JOIN\s*(?:;|$|WITH\s*\() | RESTART\s+LISTENER\b )
    | (?:CREATE|ALTER)\s+(?:ASSEMBLY|CERTIFICATE|CREDENTIAL|QUEUE|SECURITY\s+POLICY|SERVER\s+AUDIT|SERVICE|ROUTE
          |CONTRACT|ENDPOINT|MESSAGE\s+TYPE|AGGREGATE)\s+<N>\s*
        (?: \(\s*(?:@|(?:ADD|DROP)\s+CONTRACT\b|\S+\s+SENT\s+BY\b|\S+\s+\S+\s*[,)]\s*(?:$|;|WITH\b|EXTERNAL\b|RETURNS\b)) |
            WITH\s*\( | WITH\s+\w+\s*= | WITH\s+(?:PRIVATE\s+KEY|ACTIVATION)\b | AUTHORIZATION\s
          | FROM\s+(?:FILE\b|EXECUTABLE\b|ASSEMBLY\b|BINARY\s*=|N?'|0x) | AS\s+(?:TCP|HTTP)\s*\(
          | FOR\s+(?:TSQL|SERVICE_BROKER|DATABASE_MIRRORING|DATA_MIRRORING)\s*\( | (?:REBUILD|REORGANIZE)\s*(?:;|$|
              WITH\s*\() | MOVE\s+TO\s | ENCRYPTION\s+BY\b | REMOVE\s+(?:PRIVATE\s+KEY|WHERE)\b
          | (?:ADD|DROP|ALTER)\s+(?:FILE|(?:FILTER|BLOCK)\s+PREDICATE|CONTRACT)\b | MODIFY\s+NAME\b
          | WHERE\s+(?:\(\s*)?[\w.\[\]@]+\s*(?:[=<>!]|(?:NOT\s+)?(?:IN|LIKE|BETWEEN)\b|IS\s)
          | TO\s+(?:FILE|APPLICATION_LOG|SECURITY_LOG|URL|EXTERNAL_MONITOR)\b | ON\s+QUEUE\b | STATE\s*= | VALIDATION\s*= )
    | DROP\s+INDEX\s+<N>\s+ON\s+<N>\s+WITH\s*\(
    | DROP\s+ASSEMBLY\s+<N>\s+WITH\s+NO\s+DEPENDENTS\b

    # ---- Server administration
    | BACKUP\s+(?:DATABASE|LOG)\s+<N>\s+
        (?: TO\s+(?:DISK|URL|TAPE)\s*= | (?:FILE|FILEGROUP)\s*= | READ_WRITE_FILEGROUPS\b )
    | RESTORE\s+(?:DATABASE|LOG)\s+<N>\s+
        (?: FROM\s+(?:DISK|URL|TAPE|DATABASE_SNAPSHOT)\s*= | (?:FILE|FILEGROUP|PAGE)\s*=
          | WITH\s+(?:STANDBY\s*=|(?:NO)?RECOVERY\s*,) )
    | BACKUP\s+(?:SERVER|GROUP\s+\S.*?)\s+TO\s+(?:DISK|URL)\s*=
    | RESTORE\s+(?:HEADERONLY|FILELISTONLY|LABELONLY|VERIFYONLY|REWINDONLY)\b
    | (?:BACKUP|RESTORE)\s+(?:CERTIFICATE\s+<N>|(?:SERVICE\s+)?MASTER\s+KEY|SYMMETRIC\s+KEY\s+<N>)\s+(?:TO|FROM)\s+(?:FILE|
        URL)\s*=
    | KILL\s+(?:QUERY\s+NOTIFICATION\s+SUBSCRIPTION|STATS\s+JOB)\b

    # ---- Statements with no shape of their own. Not behind a label, where Decision: rollback is prose, and what
    #      is recognised must run to a statement boundary...
    | (?<!:)(?<!:\s)(?<!:\s\s)(?<!:\s\s\s)
      (?: SELECT\s+-?\d+(?:\s+AS\s+\[?\w+\]?)?
        | SELECT\s+(?:CURRENT_USER|SYSTEM_USER|SESSION_USER|CURRENT_TIMESTAMP)
        | (?:SELECT|RECEIVE)\b(?!\s+<DET>).*?\bFROM\s+(?!<DET>)<SN>(?:\s+(?:AS\s+)?(?:\w+|\[[^\]]+\]))?
        | DELETE\s+(?:TOP\s*\([^)]*\)\s*(?:PERCENT\s+)?)?(?:FROM\s+)?<SN>
        | UPDATE\s+STATISTICS\s+<SN>(?:\s*\([^)]*\)|\s+<SN>)?
        | TRUNCATE\s+TABLE\s+<SN>
        | RESTORE\s+(?:DATABASE|LOG)\s+<SN>\s+WITH\s+(?:NO)?RECOVERY
        | SET\s+LANGUAGE\s+(?:N?'[^']+'|[\w@]+)
        | THROW | BREAK | CONTINUE | RETURN(?:\s*(?:\(\s*)?[-@\d]\w*(?:\s*\))?)?
        | EXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?<SN>(?:\s+-?\d+)?
        | (?:[\w\[\]]*\.){0,3}\[?(?:u?sp|xp)_\w+\]?(?:\s+<SN>)?
        | BEGIN\s+(?:DISTRIBUTED\s+)?TRAN(?:SACTION)?(?:\s+[\w@]+)?
        | (?:COMMIT|ROLLBACK)(?:\s+WORK|\s+TRAN(?:SACTION)?(?:\s+[\w@]+)?)?
        | SAVE\s+TRAN(?:SACTION)?\s+[\w@]+
        | (?:OPEN|CLOSE|DEALLOCATE)\s+(?!<DET>)(?:GLOBAL\s+)?[\w@]+
        | FETCH\s+(?:(?:(?:NEXT|PRIOR|FIRST|LAST|(?:ABSOLUTE|RELATIVE)\s+[-\w@]+)\s+)?FROM\s+)?(?:GLOBAL\s+)?[\w@]+
            (?:\s+INTO\s+@\w+(?:\s*,\s*@\w+)*)?
        | CLOSE\s+(?:MASTER\s+KEY|ALL\s+SYMMETRIC\s+KEYS|SYMMETRIC\s+KEY\s+<SN>)
        | REVERT
        | CREATE\s+(?:DATABASE|USER|(?:SERVER\s+)?ROLE|SCHEMA|SEQUENCE|QUEUE|MESSAGE\s+TYPE|SERVER\s+AUDIT)\s+<SN>
        | CREATE\s+SYNONYM\s+<SN>\s+FOR\s+<SN>
        | DROP\s+(?:TABLE|VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER|INDEX|DATABASE|LOGIN|USER|(?:(?:SERVER|APPLICATION)\s+)?ROLE
              |SCHEMA|SEQUENCE|TYPE|SYNONYM|STATISTICS|ASSEMBLY|CERTIFICATE|CREDENTIAL|QUEUE|SERVICE|ROUTE|CONTRACT
              |ENDPOINT|MESSAGE\s+TYPE|RULE|DEFAULT|AGGREGATE|SECURITY\s+POLICY|SERVER\s+AUDIT|AVAILABILITY\s+GROUP)\s+
            <SN>(?:\s*,\s*<SN>)*(?:\s+ON\s+(?:ALL\s+SERVER|<SN>))?
        | DROP\s+MASTER\s+KEY
        | (?:ENABLE|DISABLE)\s+TRIGGER\s+<SN>(?:\s*,\s*<SN>)*\s+ON\s+(?:ALL\s+SERVER|<SN>)
        | USE\s+(?!<DET>)<SN>
        | KILL\s+(?:\d+|N?'[^']+')(?:\s+WITH\s+(?:STATUSONLY|COMMIT|ROLLBACK))?
        | CHECKPOINT(?:\s+\d+)?
        | RECONFIGURE(?:\s+WITH\s+OVERRIDE)?
        | SHUTDOWN(?:\s+WITH\s+NOWAIT)?
        # the documented DBCC commands, and four undocumented ones in common use
        | DBCC\s+(?:CHECKALLOC|CHECKCATALOG|CHECKCONSTRAINTS|CHECKDB|CHECKFILEGROUP|CHECKIDENT|CHECKTABLE|CLEANTABLE
              |CLONEDATABASE|DBREINDEX|DROPCLEANBUFFERS|FLUSHAUTHCACHE|FREEPROCCACHE|FREESESSIONCACHE
              |FREESYSTEMCACHE|HELP|INDEXDEFRAG|INPUTBUFFER|OPENTRAN|OUTPUTBUFFER|PROCCACHE|SHOW_STATISTICS
              |SHOWCONTIG|SHRINKDATABASE|SHRINKFILE|SQLPERF|TRACEOFF|TRACEON|TRACESTATUS|UPDATEUSAGE|USEROPTIONS
              |PAGE|IND|LOGINFO|MEMORYSTATUS|ERRORLOG|FLUSHPROCINDB|DBINFO|DBTABLE|WRITEPAGE|STACKDUMP|
                  \w+(?=\s*\(\s*FREE\s*\)))
            (?:\s*\((?:[^()'""]|'[^']*'|""[^""]*""|\([^()]*\))*\))?(?:\s+WITH\s+\w+(?:\s*=\s*\w+|
                \s*\([^()]*\))?(?:\s*,\s*\w+(?:\s*=\s*\w+|\s*\([^()]*\))?)*)?
      )
      # ...which is the end, a comment, a semicolon before another statement's keyword, or, with no semicolon,
      # another statement opening the way prose does not: with a keyword that is not English, with a keyword
      # before a variable, a literal, a bracket, a temp table or a star, or with a two-word opener.
      # A name does not end in a full stop or a colon: Use staging. is a sentence.
      (?<![.:])\s*
      (?: $ | -- | /\*
        | ;\s*(?: $ | -- | /\*
                | (?:SELECT|INSERT|UPDATE|DELETE|MERGE|WITH|BULK|TRUNCATE|READTEXT|WRITETEXT|UPDATETEXT|DECLARE|SET|PRINT
                    |RAISERROR|THROW|WAITFOR|GOTO|RETURN|BREAK|CONTINUE|IF|WHILE|BEGIN|END|ELSE|EXEC|EXECUTE|COMMIT
                    |ROLLBACK|SAVE|OPEN|CLOSE|FETCH|DEALLOCATE|ADD|SEND|RECEIVE|MOVE|GET|GRANT|DENY|REVOKE|REVERT
                    |SETUSER|CREATE|ALTER|DROP|ENABLE|DISABLE|DBCC|BACKUP|RESTORE|USE|KILL|CHECKPOINT|RECONFIGURE
                    |SHUTDOWN|GO)\b(?!\s+<DET>) )
        | (?:DECLARE|RAISERROR|WAITFOR|DBCC|GOTO|SETUSER|READTEXT|WRITETEXT|UPDATETEXT|DEALLOCATE|RECONFIGURE)\b
        | (?:SELECT|INSERT|UPDATE|DELETE|MERGE|SET|PRINT|RETURN|THROW|EXEC|EXECUTE|USE|KILL|OPEN|CLOSE)
            \s*(?:[@\#]\w|N?'|\[(?![^\]]*\]\())
        | SELECT\s*\*(?![*\w])
        | (?:IF|WHILE)\s*(?:NOT\s+)?(?: \(+\s*SELECT\b | (?:\(+\s*)?(?: <COND> ) )
        | (?:SELECT|PRINT|RETURN|EXEC|EXECUTE)\s*\(\s*(?:@|N?'|-?\d|SELECT\b|[\w.]+\()
        | (?:INSERT|MERGE)\s+INTO\b | DELETE\s+FROM\b | UPDATE\s+<N>\s+SET\b | SELECT\b.*?\bFROM\b
        | SET\s+\w+\s+(?:ON|OFF)\b | (?:BEGIN|COMMIT|ROLLBACK|SAVE)\s+TRAN | (?:BEGIN|END)\s+(?:TRY|CATCH)\b
        | (?:CREATE|ALTER|DROP|TRUNCATE)\s+(?:TABLE|INDEX|VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER|DATABASE|LOGIN|USER|ROLE)\b
        | (?:BACKUP|RESTORE)\s+(?:DATABASE|LOG)\b | EXEC(?:UTE)?\s+(?:[\w\[\]]+\.|\[?(?:sp|xp)_)
        | FETCH\s+\w+\s+FROM\b | KILL\s+\d
        | (?:GO|END|COMMIT|ROLLBACK|RETURN|BREAK|CONTINUE|THROW|CHECKPOINT|REVERT|SHUTDOWN|SELECT\s+\d+
            |(?:USE|OPEN|CLOSE)\s+<N>)\s*(?:;\s*)?$ )
  )
";

    /// <summary>
    /// A line that OPENS a T-SQL statement, in any case, at any indentation. Bounded by a timeout:
    /// a line that makes it backtrack for two seconds fails the scan loudly instead of hanging it.
    /// </summary>
    internal static readonly Regex Statement = new(
        Template
            .Replace("<PERM>", PermissionNames(), StringComparison.Ordinal)
            .Replace("<COND>", Condition, StringComparison.Ordinal)
            .Replace("<SN>", ShapelessName, StringComparison.Ordinal)
            .Replace("<N>", Name, StringComparison.Ordinal)
            .Replace("<DET>", Determiner, StringComparison.Ordinal),
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    /// <summary>Longest first, so <c>ALTER ANY DATABASE AUDIT</c> is tried before <c>ALTER ANY
    /// DATABASE</c>; the words of a name may be wrapped across lines, so any whitespace joins
    /// them; and <c>EXEC</c> is accepted for <c>EXECUTE</c>, as <c>GRANT EXEC ON</c> is by the
    /// server.</summary>
    private static string PermissionNames() =>
        @"(?:ALL(?:\s+PRIVILEGES)?|"
        + string.Join(
            "|",
            Permissions
                .OrderByDescending(name => name.Length)
                .ThenBy(name => name, StringComparer.Ordinal)
                .Select(name => name
                    .Replace(" ", @"\s+", StringComparison.Ordinal)
                    .Replace("EXECUTE", "EXEC(?:UTE)?", StringComparison.Ordinal)))
        + ")";
}
