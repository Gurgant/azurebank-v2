using System.Globalization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;

namespace AzureBank.AuditVerifier.Notices;

/// <summary>
/// Turns an owed-notice row into the words the account holder reads: an enrolment (ADR-0045) or a
/// change of an existing PIN (ADR-0047).
/// </summary>
/// <remarks>
/// <para>
/// IT RECEIVES NO ADDRESS, NO USER, NO PIN AND NO HASH, and that is the design rather than a
/// convenience: a renderer cannot leak what it never receives. The three inputs are the row, the
/// contact the operator typed, and the clock.
/// </para>
/// <para>
/// WHAT THE NOTICE SAYS, and where each sentence comes from. The service name and the date of the
/// event (NIST SP 800-63A-4 §3.10). What happened to the credential and what it authorises — until
/// 2026-09-04 this said "what was added", when an enrolment was the only kind — so the reader can judge
/// urgency. Repudiation instructions with contact information (SP 800-63B-4 §4.6 — mandatory
/// content, which is why the command refuses to run without a contact). That the message asks for
/// nothing, carries no link and cannot be replied to (FFIEC 2021 §10; ASVS 2.2.3: nothing sensitive
/// in a notification). That it was ADDRESSED to the email held on the account — never "sent",
/// because nothing here sends.
/// </para>
/// <para>
/// WHAT IT DELIBERATELY DOES NOT SAY. The PIN, the password, an account number, the holder's name,
/// any URL, any "press here to invalidate" — no PIN revocation path exists, so a button would be a
/// promise. And NOT "sign out to end every session", though the reason differs by kind and until
/// 2026-09-04 only the first was written down. For an ENROLMENT the actor proved the account
/// password (T8), so they sign back in and the sentence would be false comfort. For a CHANGE they
/// proved a session and the current PIN and not the password, so signing them out helps until the
/// session they hold expires — but the notice still does not say it, because the reader cannot end
/// another session from here and the remedy that does work is in the change notice already: the PIN
/// is removed, and setting a new one costs the password.
/// </para>
/// </remarks>
public static class NoticeRenderer
{
    public const string ServiceName = "AzureBank";

    public static RenderedNotice Render(SubscriberNotice notice, string contact, DateTime nowUtc)
    {
        var reference = notice.Id.ToString("N");

        return notice.Event switch
        {
            SecurityEvents.PinEnrolled => new RenderedNotice(
                MessageId: reference,
                Subject: $"{ServiceName}: a transfer PIN was set on your account",
                Body: PinEnrolled(notice, contact, reference),
                FileName: $"{reference}.eml"),

            SecurityEvents.PinChanged => new RenderedNotice(
                MessageId: reference,
                Subject: $"{ServiceName}: the transfer PIN on your account was changed",
                Body: PinChanged(notice, contact, reference),
                FileName: $"{reference}.eml"),

            // A notice this build cannot render is a finding, not a blank message: the row stays
            // owed and the operator sees the event name.
            _ => throw new InvalidOperationException(
                $"No notice text exists for event '{notice.Event}'; the row stays owed."),
        };
    }

    /// <summary>
    /// The change notice (ADR-0047). Deliberately NOT the enrolment's words: a change costs the
    /// CURRENT PIN and never the password, so "for the first time" and "your account password was
    /// proved" would both be false, and the second one would tell a reader whose PIN was watched
    /// that their password had been used. The remedy differs too — re-enrolling costs the password,
    /// which a PIN-only attacker does not have — so the recovery sentence is stronger here than in
    /// the enrolment notice, and says so without naming a procedure the reader cannot run.
    /// </summary>
    private static string PinChanged(SubscriberNotice notice, string contact, string reference)
    {
        var when = notice.OccurredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return string.Join('\n',
            $"{ServiceName} — security notice",
            "",
            $"On {when} UTC the 6-digit transfer PIN on your account was changed.",
            "",
            "That PIN is what authorises withdrawals and transfers from your account. Whoever made",
            "this change proved the PIN that was in place before it. Your account password was not",
            "used and has not changed.",
            "",
            "If this was you, nothing further is needed.",
            "",
            "IF THIS WAS NOT YOU: someone knows the PIN you had been using. Do not use the account",
            "for transfers or withdrawals, and contact",
            $"{contact} quoting reference {reference}.",
            "",
            "Ask for that PIN to be removed. Setting a new one costs your account password, which",
            "this change did not.",
            "",
            "This message asks you for nothing. It contains no link, and you will never be asked for",
            "your password or PIN by message. It cannot be replied to.",
            "",
            "It was addressed to the email address held on your account. If that address is not",
            $"yours, contact {contact}.",
            "",
            $"Reference: {reference}",
            "");
    }

    private static string PinEnrolled(SubscriberNotice notice, string contact, string reference)
    {
        var when = notice.OccurredAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

        return string.Join('\n',
            $"{ServiceName} — security notice",
            "",
            $"On {when} UTC a 6-digit transfer PIN was set on your account for the first time.",
            "",
            "That PIN is what authorises withdrawals and transfers from your account. Your account",
            "password was proved in the same request.",
            "",
            "If this was you, nothing further is needed.",
            "",
            "IF THIS WAS NOT YOU: do not use the account for transfers or withdrawals, and contact",
            $"{contact} quoting reference {reference}.",
            "",
            "This message asks you for nothing. It contains no link, and you will never be asked for",
            "your password or PIN by message. It cannot be replied to.",
            "",
            "It was addressed to the email address held on your account. If that address is not",
            $"yours, contact {contact}.",
            "",
            $"Reference: {reference}",
            "");
    }
}
