namespace AzureBank.Infrastructure.Data;

/// <summary>
/// The verification key ring cannot mean what the configuration says, so no chain was built.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ IT EXISTS TO KEEP A CONFIGURATION PROBLEM FROM BEING REPORTED AS SOMETHING ELSE, and it was
/// added because it already had been. The ring's rules are enforced in <see cref="AuditChain"/>'s
/// constructor, which is the only place that covers both composition roots — but a constructor throw
/// surfaces wherever the type happens to be resolved, and that is not the same place in every
/// caller. Measured on the operator verifier with one short retired key: <c>verify</c> answered 3
/// with prose, because it resolves the chain inside its try; <c>anchor</c> and <c>export</c> answered
/// <b>4</b> with an unhandled stack trace, because they resolve it one line above theirs.
/// </para>
/// <para>
/// Exit 4 is that tool's code for "the command line was wrong", and the command line was right. The
/// runbook records the SAME defect from an earlier release, with the same two verbs, and closes with
/// *"Both now answer 3, like `verify`."* — so the key ring re-opened an incident the page documents
/// as fixed. A plain <see cref="InvalidOperationException"/> could not be caught narrowly enough to
/// prevent that without also swallowing failures that are genuinely about the store.
/// </para>
/// <para>
/// It is deliberately NOT thrown for anything a walk finds. A row that cannot be verified is a
/// verdict, not an exception; this type means the verifier was never in a position to give one.
/// </para>
/// </remarks>
public sealed class AuditKeyRingException : InvalidOperationException
{
    /// <inheritdoc cref="AuditKeyRingException"/>
    public AuditKeyRingException(string message)
        : base(message)
    {
    }

    /// <inheritdoc cref="AuditKeyRingException"/>
    public AuditKeyRingException()
    {
    }

    /// <inheritdoc cref="AuditKeyRingException"/>
    public AuditKeyRingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
