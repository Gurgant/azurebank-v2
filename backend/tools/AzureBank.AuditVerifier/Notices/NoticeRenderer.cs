using System.Globalization;
using AzureBank.Shared.Constants;
using AzureBank.Shared.Entities;

namespace AzureBank.AuditVerifier.Notices;

/// <summary>
/// Turns an owed-notice row into the words the account holder reads (ADR-0045).
/// </summary>
/// <remarks>
/// <para>
/// IT RECEIVES NO ADDRESS, NO USER, NO PIN AND NO HASH, and that is the design rather than a
/// convenience: a renderer cannot leak what it never receives. The three inputs are the row, the
/// contact the operator typed, and the clock.
/// </para>
/// <para>
/// WHAT THE NOTICE SAYS, and where each sentence comes from. The service name and the date of the
/// event (NIST SP 800-63A-4 §3.10). What was added and what it authorises, so the reader can judge
/// urgency. Repudiation instructions with contact information (SP 800-63B-4 §4.6 — mandatory
/// content, which is why the command refuses to run without a contact). That the message asks for
/// nothing, carries no link and cannot be replied to (FFIEC 2021 §10; ASVS 2.2.3: nothing sensitive
/// in a notification). That it was ADDRESSED to the email held on the account — never "sent",
/// because nothing here sends.
/// </para>
/// <para>
/// WHAT IT DELIBERATELY DOES NOT SAY. The PIN, the password, an account number, the holder's name,
/// any URL, any "press here to invalidate" — no PIN revocation path exists, so a button would be a
/// promise. And NOT "sign out to end every session": the attacker this control is for proved the
/// account password to enrol (T8), so they sign back in; a sentence that made the reader feel safe
/// would be the most harmful line in the message.
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

            // A notice this build cannot render is a finding, not a blank message: the row stays
            // owed and the operator sees the event name.
            _ => throw new InvalidOperationException(
                $"No notice text exists for event '{notice.Event}'; the row stays owed."),
        };
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
