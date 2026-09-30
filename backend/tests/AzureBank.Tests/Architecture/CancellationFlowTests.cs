using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AzureBank.Api.Services.Interfaces;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.DTOs.Account;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.DTOs.Transaction;
using AzureBank.Shared.DTOs.Transfer;
using AzureBank.Shared.Entities;
using AzureBank.Shared.Enums;
using AzureBank.Tests.Fixtures;
using AzureBank.Tests.Integration;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Architecture;

/// <summary>
/// The request's cancellation token reaches the database: every controller action takes one, every
/// method of the services they call takes one, every EF call passes one, and the UserManager the host
/// registers hands Identity's store the deadline's (ADR-0058).
/// </summary>
/// <remarks>
/// <para>
/// The API's request deadline cancels one token. It caps a request only where that token is
/// passed: a command that was not given it runs to its own 30-second timeout whatever the deadline
/// says. Measured by these tests before this change: 0 of the 27 actions took a token, nor 31 of the
/// 32 service methods, and 69 of the 129 EF calls in the API and Infrastructure passed none (2 more
/// passed <c>CancellationToken.None</c>). CA2016 (an error in both projects) catches a token a method
/// has and does not forward; these catch the methods that never had one to forward.
/// </para>
/// <para>
/// Three token endpoints stay without one, on purpose: refresh, revoke and logout run to completion
/// once they have started (ADR-0057 §4.3), so they carry <c>[NoRequestDeadline]</c> instead.
/// </para>
/// </remarks>
public class CancellationFlowTests
{
    /// <summary>The actions the deadline does not apply to (ADR-0057 §4.3, ADR-0058).</summary>
    private static readonly string[] Exempt = ["Refresh", "Revoke", "Logout"];

    /// <summary>
    /// The seven interfaces the controllers reach the database through. The token each method takes
    /// is the request's own: the controllers pass it on.
    /// </summary>
    private static readonly Type[] RequestServices =
    [
        typeof(IAccountAccessService),
        typeof(IAccountService),
        typeof(IAuthService),
        typeof(ITransactionService),
        typeof(ITransferService),
        typeof(IUserService),
        typeof(IPinVerifier),
    ];

    private static IEnumerable<MethodInfo> Actions() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true } && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null);

    private static bool TakesAToken(MethodInfo method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken));

    [Fact]
    public void EveryActionButTheThreeExemptTokenEndpoints_TakesTheRequestsToken()
    {
        var actions = Actions().ToList();
        actions.Should().HaveCount(30, "the API's 30 operations; a scan that finds fewer is not reading them");

        var bound = actions.Where(a => !Exempt.Contains(a.Name)).ToList();
        bound.Should().HaveCount(27);

        var missing = bound.Where(a => !TakesAToken(a)).Select(a => $"{a.DeclaringType!.Name}.{a.Name}").ToList();
        missing.Should().BeEmpty(
            "an action without a CancellationToken parameter cannot hand the deadline's token to the "
            + "services below it; {0} of {1} take none: {2}",
            missing.Count, bound.Count, string.Join(", ", missing));
    }

    [Fact]
    public void EveryMethodOfTheRequestServices_TakesAToken()
    {
        var methods = RequestServices
            .SelectMany(i => i.GetMethods())
            .Where(m => typeof(Task).IsAssignableFrom(m.ReturnType))
            .Where(m => !(m.DeclaringType == typeof(IAuthService) && Exempt.Contains(m.Name.Replace("Async", string.Empty, StringComparison.Ordinal))))
            .ToList();

        // 30 methods, plus the one that already had a token (GetSessionStampsAsync).
        methods.Should().HaveCount(31, "a scan that finds fewer is not reading the interfaces");

        // The claim release, run on the error path in its own scope, takes the token that bounds it.
        methods.Add(typeof(IIdempotencyService).GetMethod(nameof(IIdempotencyService.ReleaseIfNotExecutedAsync))!);

        var missing = methods.Where(m => !TakesAToken(m)).Select(m => $"{m.DeclaringType!.Name}.{m.Name}").ToList();
        missing.Should().BeEmpty(
            "a service method without a token is where the request's cancellation stops; {0} of {1} take none: {2}",
            missing.Count, methods.Count, string.Join(", ", missing));
    }

    [Fact]
    public void TheUserManagerTheHostRegisters_TakesTheDeadlinesToken()
    {
        // Identity's calls take no token from the service methods above: UserManager hands its store
        // a token of its own. AddIdentity registers the base UserManager, whose token is always None,
        // so a sign-in's lookup ran past the deadline until the API registered its own
        // (RequestDeadlineTests shows what that one does).
        using var factory = new CustomWebApplicationFactory();
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().Should().BeOfType<
            AzureBank.Api.Middleware.RequestDeadlineUserManager>(
            "every UserManager call must take the request deadline's token");
    }

    // ── The source scan ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// EF Core's asynchronous calls that take a <see cref="CancellationToken"/>. Deliberately not
    /// here: <c>CloseConnectionAsync</c>, which has no overload that takes one.
    /// </summary>
    private static readonly HashSet<string> EfCalls =
    [
        "AnyAsync", "AllAsync", "CountAsync", "LongCountAsync", "FirstAsync", "FirstOrDefaultAsync",
        "LastAsync", "LastOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync", "MinAsync", "MaxAsync",
        "SumAsync", "AverageAsync", "ContainsAsync", "ElementAtAsync", "ElementAtOrDefaultAsync",
        "ToListAsync", "ToArrayAsync", "ToDictionaryAsync", "ToHashSetAsync", "ForEachAsync", "LoadAsync",
        "ExecuteUpdateAsync", "ExecuteDeleteAsync", "SaveChangesAsync", "FindAsync", "AddAsync",
        "AddRangeAsync", "BeginTransactionAsync", "ExecuteSqlRawAsync", "ExecuteSqlInterpolatedAsync",
        "ExecuteSqlAsync", "OpenConnectionAsync", "CommitAsync", "RollbackAsync", "CreateSavepointAsync",
        "RollbackToSavepointAsync", "ReleaseSavepointAsync", "ReloadAsync", "GetDatabaseValuesAsync",
        "ExecuteAsync", "ExecuteInTransactionAsync",
    ];

    /// <summary>
    /// Where <c>CancellationToken.None</c> is the design: a rollback must run whatever happened to the
    /// request, and a refusal's audit row is written in its own scope so that it survives the
    /// rollback of what it refused (ADR-0044 D1).
    /// </summary>
    private static readonly HashSet<string> UncancellableCalls = ["RollbackAsync", "RecordRefusalAsync"];

    private static readonly Regex TokenArgument = new(
        @"^(?:cancellationToken\s*:\s*)?(?<token>cancellationToken|ct|token|stoppingToken|\w*[cC]ancellation\w*|[\w.]+\.Token|[\w.]*RequestAborted|CancellationToken\.None|default|default\(CancellationToken\))$",
        RegexOptions.CultureInvariant);

    private static readonly Regex Call = new(@"\.(?<name>\w+Async)\s*(?:<[^()]*>)?\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex NoneToken = new(
        @"CancellationToken\.None|default\(CancellationToken\)|\b(?:cancellationToken|ct)\s*:\s*default\b",
        RegexOptions.CultureInvariant);

    private static bool IsNone(string token) =>
        token is "default" or "CancellationToken.None" or "default(CancellationToken)";

    [Fact]
    public void EveryEfCall_PassesTheRequestsToken_AndNoneOnlyWhereItMustNotBeCancelled()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var efCalls = 0;
        var files = 0;

        foreach (var project in new[] { "AzureBank.Api", "AzureBank.Infrastructure" })
        {
            var folder = Path.Combine(root, "backend", "src", project);
            Directory.Exists(folder).Should().BeTrue(because: $"expected to scan {folder}");

            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                if (segments.Contains("obj") || segments.Contains("bin") || segments.Contains("Migrations"))
                {
                    continue;
                }

                files++;
                var code = CodeOnly(File.ReadAllText(file));
                var calls = new List<(string Name, int Open, int Close)>();

                foreach (Match match in Call.Matches(code))
                {
                    var name = match.Groups["name"].Value;
                    var open = match.Index + match.Length;
                    var close = ClosingParenthesis(code, open);
                    calls.Add((name, open, close));

                    if (!EfCalls.Contains(name))
                    {
                        continue;
                    }

                    efCalls++;
                    var arguments = SplitArguments(code[open..close]);
                    var line = LineOf(code, match.Index);
                    var last = arguments.Count == 0 ? string.Empty : Regex.Replace(arguments[^1], @"\s+", " ");
                    var token = TokenArgument.Match(last);

                    // A token inside a params list is bound as a SQL parameter or a key value: it
                    // compiles, and fails at run time, on SQL Server only. The token goes after a
                    // collection: (sql, new object[] { ... }, ct) and (new object[] { ... }, ct).
                    var paramsShape = name switch
                    {
                        "ExecuteSqlRawAsync" when arguments.Count > 2
                            && !(arguments.Count == 3 && IsCollection(arguments[1])) =>
                            "; with values, the token goes after them as a collection: (sql, new object[] { ... }, ct)",
                        "FindAsync" when !(arguments.Count == 2 && IsCollection(arguments[0])) =>
                            "; the key values go in a collection before the token: (new object[] { ... }, ct)",
                        _ => null,
                    };

                    if (!token.Success)
                    {
                        offenders.Add($"{relative}:{line} {name}(...) passes no token{paramsShape}");
                    }
                    else if (paramsShape is not null)
                    {
                        offenders.Add($"{relative}:{line} {name}(...) binds its token as a value{paramsShape}");
                    }
                    else if (IsNone(token.Groups["token"].Value) && !UncancellableCalls.Contains(name))
                    {
                        offenders.Add($"{relative}:{line} {name}(..., {last}) passes a token nothing can cancel");
                    }
                }

                // Any other None: attributed to the innermost call it is an argument of. A rollback
                // and a refusal's audit row may take it; an EF call's was judged above.
                foreach (Match none in NoneToken.Matches(code))
                {
                    var owner = calls
                        .Where(c => none.Index >= c.Open && none.Index < c.Close)
                        .OrderBy(c => c.Close - c.Open)
                        .Select(c => c.Name)
                        .FirstOrDefault();

                    if (owner is null || !(UncancellableCalls.Contains(owner) || EfCalls.Contains(owner)))
                    {
                        offenders.Add($"{relative}:{LineOf(code, none.Index)} {none.Value} passed to "
                                      + $"{owner ?? "a call"}(...), outside a rollback or a refusal's audit row");
                    }
                }
            }
        }

        files.Should().BeGreaterThan(50, "a scan that reads nothing reports clean");
        efCalls.Should().BeGreaterThan(100, "measured at 129 EF calls when this was written; far fewer means the scan broke");

        offenders.Should().BeEmpty(
            "the deadline cancels one token and caps only what receives it ({0} offenders in {1} EF calls):{2}{3}",
            offenders.Count, efCalls, Environment.NewLine, string.Join(Environment.NewLine, offenders));
    }

    private static bool IsCollection(string argument)
    {
        var trimmed = argument.TrimStart();
        return trimmed.StartsWith("new", StringComparison.Ordinal) || trimmed.StartsWith('[');
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    private static int ClosingParenthesis(string code, int open)
    {
        var depth = 1;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '(')
            {
                depth++;
            }
            else if (code[i] == ')' && --depth == 0)
            {
                return i;
            }
        }

        return code.Length;
    }

    private static List<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in arguments)
        {
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }

            if (c == ',' && depth == 0)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.ToString().Trim() is { Length: > 0 } tail)
        {
            result.Add(tail);
        }

        return result;
    }

    /// <summary>
    /// The source with every comment and every string or character literal blanked, lengths and
    /// line breaks kept: a parenthesis in a SQL string or a word in a comment is not code.
    /// </summary>
    private static string CodeOnly(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            if (Starts(source, i, "//"))
            {
                var end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                Blank(output, source, i, end);
                i = end;
                continue;
            }

            if (Starts(source, i, "/*"))
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + 2;
                Blank(output, source, i, end);
                i = end;
                continue;
            }

            var raw = RawStringDelimiter(source, i);
            if (raw is { } delimiter)
            {
                var contentStart = source.IndexOf('"', i) + delimiter.Length;
                var end = source.IndexOf(delimiter, contentStart, StringComparison.Ordinal);
                end = end < 0 ? source.Length : end + delimiter.Length;
                Blank(output, source, i, end);
                i = end;
                continue;
            }

            if (StringStart(source, i) is { } quote)
            {
                var verbatim = source.AsSpan(i, quote).Contains('@');
                var j = i + quote;
                while (j < source.Length)
                {
                    if (verbatim)
                    {
                        if (source[j] == '"')
                        {
                            if (j + 1 < source.Length && source[j + 1] == '"')
                            {
                                j += 2;
                                continue;
                            }

                            break;
                        }
                    }
                    else if (source[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }
                    else if (source[j] is '"' or '\n')
                    {
                        break;
                    }

                    j++;
                }

                var end = Math.Min(j + 1, source.Length);
                Blank(output, source, i, end);
                i = end;
                continue;
            }

            if (source[i] == '\'')
            {
                var j = i + 1;
                if (j < source.Length && source[j] == '\\')
                {
                    j += 2;
                }
                else
                {
                    j++;
                }

                while (j < source.Length && source[j] != '\'' && j - i < 10)
                {
                    j++;
                }

                if (j < source.Length && source[j] == '\'')
                {
                    Blank(output, source, i, j + 1);
                    i = j + 1;
                    continue;
                }
            }

            output.Append(source[i]);
            i++;
        }

        return output.ToString();
    }

    private static bool Starts(string source, int index, string value) =>
        string.CompareOrdinal(source, index, value, 0, value.Length) == 0;

    /// <summary>The <c>"""</c> run that opens a raw string literal at <paramref name="index"/>, if one does.</summary>
    private static string? RawStringDelimiter(string source, int index)
    {
        // Interpolation only: a raw literal cannot be verbatim, and @"""" is a verbatim string
        // holding one quote.
        var j = index;
        while (j < source.Length && source[j] == '$')
        {
            j++;
        }

        var quotes = 0;
        while (j + quotes < source.Length && source[j + quotes] == '"')
        {
            quotes++;
        }

        return quotes >= 3 ? new string('"', quotes) : null;
    }

    /// <summary>The length of the prefix and quote that open a string literal at <paramref name="index"/>.</summary>
    private static int? StringStart(string source, int index)
    {
        if (source[index] == '"')
        {
            return 1;
        }

        if (source[index] is '$' or '@' && index + 1 < source.Length)
        {
            if (source[index + 1] == '"')
            {
                return 2;
            }

            if (source[index + 1] is '$' or '@' && index + 2 < source.Length && source[index + 2] == '"')
            {
                return 3;
            }
        }

        return null;
    }

    private static void Blank(StringBuilder output, string source, int start, int end)
    {
        for (var k = start; k < end; k++)
        {
            output.Append(source[k] == '\n' ? '\n' : ' ');
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".github")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull(because: "the scan needs the sources; a guard that cannot run must fail loudly");
        return dir!.FullName;
    }
}

/// <summary>
/// Each of the seven service families stops at the token it is given (ADR-0058): called with one
/// that is already cancelled, it throws <see cref="OperationCanceledException"/> and writes nothing.
/// </summary>
/// <remarks>
/// The signatures above prove each method TAKES a token and CA2016 that each one it holds is
/// forwarded; neither proves that a family's first database call is the one that receives it. On
/// the in-memory provider, which checks the token before every query and every save.
/// </remarks>
public class CancellationFlowFamilyTests : IntegrationTestBase
{
    public CancellationFlowFamilyTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    private static CancellationToken Cancelled => new(canceled: true);

    private T Service<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task AccountAccess_StopsAtTheToken()
    {
        var (_, userId, accountId) = await RegisterTestUserAsync();
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<IAccountAccessService>(scope).GetAccountWithOwnershipCheckAsync(accountId, userId, Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Accounts_StopAtTheToken_AndOpenNoAccount()
    {
        var (_, userId, _) = await RegisterTestUserAsync();
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<IAccountService>(scope).CreateAccountAsync(
            userId, new CreateAccountRequest { Name = "Savings", Type = AccountType.Savings }, Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Accounts.CountAsync(a => a.UserId == userId)))
            .Should().Be(1, "the starter account only");
    }

    [Fact]
    public async Task Auth_StopsAtTheToken_AndRegistersNobody()
    {
        var email = $"cancelled{Guid.NewGuid():N}"[..20] + "@example.com";
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<IAuthService>(scope).RegisterAsync(new RegisterRequest
        {
            AzureTag = $"cx_{Guid.NewGuid():N}"[..12],
            Email = email,
            Password = TestUserPassword,
            FirstName = "Can",
            LastName = "Celled",
        }, Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Users.AnyAsync(u => u.Email == email))).Should().BeFalse();
    }

    [Fact]
    public async Task Transactions_StopAtTheToken_AndMoveNoMoney()
    {
        var (_, userId, accountId) = await RegisterTestUserAsync();
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<ITransactionService>(scope).DepositAsync(
            userId, new DepositRequest { AccountId = accountId, Amount = 25m }, Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Transactions.CountAsync(t => t.AccountId == accountId))).Should().Be(0);
        (await ReadAsync(db => db.Accounts.SingleAsync(a => a.Id == accountId))).Balance.Should().Be(0m);
    }

    [Fact]
    public async Task Transfers_StopAtTheToken_AndMoveNoMoney()
    {
        var (token, userId, accountId) = await RegisterTestUserAsync();
        await DepositAsync(token, accountId, 50m);
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<ITransferService>(scope).InternalTransferAsync(
            userId,
            new InternalTransferRequest { FromAccountId = accountId, ToAccountId = Guid.NewGuid(), Amount = 1m },
            Guid.NewGuid(),
            Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Transactions.CountAsync(t => t.AccountId == accountId)))
            .Should().Be(1, "the funding deposit only");
    }

    [Fact]
    public async Task Users_StopAtTheToken_AndRenameNothing()
    {
        var (_, userId, _) = await RegisterTestUserAsync();
        var before = (await ReadAsync(db => db.Users.SingleAsync(u => u.Id == userId))).AzureTag;
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<IUserService>(scope).RenameAzureTagAsync(userId, $"rn_{Guid.NewGuid():N}"[..12], Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Users.SingleAsync(u => u.Id == userId))).AzureTag.Should().Be(before);
    }

    [Fact]
    public async Task ThePinVerifier_StopsAtTheToken_AndCountsNothing()
    {
        // A PIN is enrolled, so a wrong one WOULD be counted if the verifier ran.
        var (token, userId, _) = await RegisterTestUserAsync();
        await SetPinAsync(token);
        using var scope = Factory.Services.CreateScope();

        var act = () => Service<IPinVerifier>(scope).VerifyPinAsync(userId, "000000", Cancelled);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(db => db.Users.SingleAsync(u => u.Id == userId))).PinAccessFailedCount.Should().Be(0);
    }

    /// <summary>Reads through a fresh context, so what it reads is the store's, not a tracked copy.</summary>
    private async Task<T> ReadAsync<T>(Func<AzureBankDbContext, Task<T>> read)
    {
        using var scope = Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AzureBankDbContext>());
    }
}
