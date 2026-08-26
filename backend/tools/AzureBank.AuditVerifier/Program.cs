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
// Usage, from the repository root:
//   dotnet run --project backend/tools/AzureBank.AuditVerifier -- verify
//   dotnet run --project backend/tools/AzureBank.AuditVerifier -- anchor
//
// `verify` walks the chain and reports. `anchor` walks it once and RECORDS what it found, chained to
// the record before it and authenticated under a key the database does not hold. It detects no
// truncation on its own -- see AnchorCommand -- and it is a mode of this tool rather than a scheduled
// job, because nothing in this deployment runs between sessions.
//
// Exit codes: 0 intact, 1 broken, 2 nothing to verify, 3 no verdict (the store could not be read),
// 4 the command line was wrong, 5 interrupted, 6 there WAS a verdict but nothing could be recorded
// from it. Only 0, 1 and 2 are statements about the chain. The list lives in the commands'
// constants -- 0 to 5 in VerifyCommand, 6 in AnchorCommand -- and this header repeats it, so
// changing one means changing both.
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
          THE VALIDATOR MOVED INTO THE COMMAND, and this comment records why rather than vanishing.

          It ran here, before the command line was parsed, so an unconfigured machine could not even
          print --help: every invocation exited 3 with "this tool is not configured to read the
          chain". Measured on a3e31a7, all four of --help, --version, no arguments and a mistyped
          command. It now runs at the start of VerifyCommand.RunAsync, which is the first moment
          anything actually needs the key -- so the guarantee is unchanged and the tool can still be
          asked what it is.
        */

        var rootCommand = new RootCommand("AzureBank Audit Chain Verifier")
        {
            Description = "Verifies the append-only, hash-chained audit trail (ADR-0044)",
        };

        rootCommand.AddCommand(VerifyCommand.Create(host.Services));
        rootCommand.AddCommand(AnchorCommand.Create(host.Services));

        var parsed = await rootCommand.InvokeAsync(args);

        /*
          TRANSLATE THE FRAMEWORK'S CODE; DO NOT PASS IT THROUGH.

          The previous version returned `parsed` unchanged, reasoning that "a parse failure must
          still win, or a typo would look like a verdict". It had it backwards. The default pipeline
          reports EVERY parse failure as exit 1 -- and 1 is this tool's word for CHAIN BROKEN, so a
          typo did not look like a verdict, it looked like the WORST verdict.

          Measured on the pinned 2.0.0-beta4, all three exited 1: no arguments at all ("Required
          command was not provided."), a mistyped command, and an unknown option. Running this tool
          with no arguments is the likeliest mistake there is, and it reported a tampered audit trail
          to anything reading the exit code.

          That pipeline only ever emits 0 or 1, so any non-zero here is a usage or framework failure
          and becomes UsageError. The handler's own verdict travels separately in
          Environment.ExitCode, read only when the command actually ran.
        */
        return VerifyCommand.CombineExitCodes(parsed, Environment.ExitCode);
    }
}
