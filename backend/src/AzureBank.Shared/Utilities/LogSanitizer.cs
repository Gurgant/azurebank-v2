using System.Text.RegularExpressions;

namespace AzureBank.Shared.Utilities;

/// <summary>
/// Central sanitizer for user-controlled values that end up in log messages.
/// Strips every control character (C0/C1 including CR, LF, TAB, ESC, NEL), DEL, Unicode
/// format/unassigned/private-use/surrogate characters, and the U+2028/U+2029 line separators —
/// preventing log forging (CWE-117) into plain-text sinks. Over-stripping (e.g. emoji, which are
/// surrogate pairs) is deliberate: for a log line, dropping a glyph is fail-safe, keeping a
/// forgeable character is not.
///
/// CONTRACT (do not weaken): the return value contains no character that can start a new log
/// line or alter terminal rendering. This exact guarantee is
///   1. pinned by AzureBank.Tests.Unit.Utilities.LogSanitizerTests, and
///   2. declared to CodeQL as a barrierModel row (kind "log-injection") in
///      .github/codeql/extensions/azurebank-csharp-models/models/logsanitizer.model.yml, which
///      matches this EXACT namespace/type/name/signature
///      ("AzureBank.Shared.Utilities", "LogSanitizer", "Sanitize", "(System.String)").
///      Renaming, moving, or adding overloads silently orphans that row — update the YAML if
///      you touch this API.
///      ⚠ THAT ROW IS NOT IN EFFECT TODAY: code scanning runs in **default setup**, which does
///      not load repository model packs, so CodeQL still reports log-forging through this
///      sanitizer and each report has to be dismissed by hand. The pack is staged for a planned
///      migration to advanced setup (own codeql.yml loading the pack); until that lands, treat
///      point 2 as intent, not as an active control. Point 1 — the tests — is the real guarantee.
///
/// Scope: safe for LOG output only. Not an HTML/SQL/header/path sanitizer.
/// </summary>
public static partial class LogSanitizer
{
    // \p{C} = Cc (all C0/C1 controls incl. \r \n \t and U+0085 NEL), Cf, Co, Cn, Cs.
    // U+2028/U+2029 are Zl/Zp (not in \p{C}) but render as newlines in some viewers.
    // Regex.Replace is chosen ON PURPOSE: it is one of the call shapes CodeQL's built-in
    // StringReplaceSanitizer recognises, so the flow is suppressed by the analyzer's own
    // heuristics today AND by the explicit barrier row regardless of future reimplementation.
    [GeneratedRegex(@"[\p{C}\u2028\u2029]")]
    private static partial Regex ControlChars();

    /// <summary>
    /// Returns <paramref name="value"/> with all control/format and line/paragraph-separator
    /// characters removed. Null yields an empty string (never throws from a logging path).
    /// </summary>
    public static string Sanitize(string? value) =>
        value is null ? string.Empty : ControlChars().Replace(value, string.Empty);
}
