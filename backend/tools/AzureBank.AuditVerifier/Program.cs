using System.CommandLine;
using AzureBank.AuditVerifier.Commands;
using AzureBank.AuditVerifier.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace AzureBank.AuditVerifier;

// ============================================
// AzureBank Audit Chain Verifier
// ============================================
// The operator-runnable verification ADR-0044 recorded as missing. AuditChain.VerifyAsync existed
// and the suite called it, but nothing exposed it, so the runbook could not tell an operator to
// check that the hashes still link.
//
// Usage:
//   dotnet run --project tools/AzureBank.AuditVerifier -- verify
//
// Exit codes: 0 intact, 1 broken, 2 nothing to verify.
// ============================================
/*
  ANCHORED TO THE BINARY, NOT TO THE SHELL'S CURRENT DIRECTORY.
  Host.CreateApplicationBuilder(args) resolves appsettings.json relative to the working directory, so
  the tool's own configuration is read only when it happens to be invoked from its output folder.
  Measured: run from bin/Debug it printed a clean verdict; run through `dotnet run --project` from
  the repository root it dumped every EF SQL statement above the answer, because the Serilog levels
  were never loaded. An operator tool whose output depends on which directory you launched it from
  is a tool that behaves differently in production than in the runbook.
*/

/*
  AN EXPLICIT ENTRY POINT, NOT TOP-LEVEL STATEMENTS, and the reason is mechanical rather than
  stylistic. Top-level statements generate an internal Program class in the GLOBAL namespace. The
  API does the same, and the test project references both this tool and the API -- so
  CustomWebApplicationFactory<Program> stopped compiling with CS0433, "the type 'Program' exists in
  both". Naming the class here keeps the tool testable from the same suite as everything else.
*/
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Services.AddSerilog();
        builder.Services.AddVerifierServices(builder.Configuration, builder.Environment);

        var host = builder.Build();

        /*
          VALIDATE BEFORE READING A SINGLE ROW, and the reason is specific to this tool.

          A CLI never calls host.StartAsync(), so .ValidateOnStart() alone never fires — the same trap the
          seeder documents. For a seeder that means a late failure; here it would mean a WRONG ANSWER: the
          row hash is an HMAC over Audit:ChainKey, so a missing or mistyped key does not throw, it
          recomputes every hash differently and reports the chain broken at sequence 1.

          A tamper-evidence tool that accuses an intact chain is worse than no tool, because somebody will
          act on it during the incident it was reached for. So the validator runs first, and a bad key stops
          the process with a message about the key rather than a verdict about the bank.
        */
        try
        {
            host.Services.GetService<IStartupValidator>()?.Validate();
        }
        catch (OptionsValidationException invalid)
        {
            /*
              A CONFIGURATION PROBLEM IS AN OUTCOME, NOT A CRASH. Left unhandled this exited 127 --
              which means nothing to a script -- under eleven lines of stack trace, with the one
              sentence that matters as its first line. An operator reaching for this tool during an
              incident should not have to read a .NET stack to learn they mistyped an environment
              variable.
            */
            Console.Error.WriteLine("CANNOT VERIFY: this tool is not configured to read the chain.");
            foreach (var failure in invalid.Failures)
            {
                Console.Error.WriteLine($"  {failure}");
            }

            return VerifyCommand.Misconfigured;
        }

        var rootCommand = new RootCommand("AzureBank Audit Chain Verifier")
        {
            Description = "Verifies the append-only, hash-chained audit trail (ADR-0044)",
        };

        rootCommand.AddCommand(VerifyCommand.Create(host.Services));

        var parsed = await rootCommand.InvokeAsync(args);

        // InvokeAsync returns the parser's own result; the command sets Environment.ExitCode to say what it
        // FOUND. A parse failure must still win, or a typo would look like a verdict.
        return parsed != 0 ? parsed : Environment.ExitCode;
    }
}
