namespace AzureBank.Shared.Validation;

/// <summary>
/// Marks the AzureTag of a PAYEE in a request body: the handle a transfer, or the authorisation
/// for one, names. It is checked exactly as <see cref="AzureTagAttribute"/> checks a handle, but the
/// value it sees has already been lower-cased by <see cref="LowerCasingJsonConverter"/> when the
/// body was read, so <c>Jane_02ga</c> is the handle <c>jane_02ga</c> and everything else is refused
/// as before. What a NEW handle may be (registration, rename) does not change: those keep
/// <see cref="AzureTagAttribute"/> and <see cref="AzureTagQueryAttribute"/>.
///
/// A separate type only so the published contract can say the two apart: the document describes a
/// payee's handle as the wire carries it, in any letter case.
/// </summary>
public sealed class PayeeAzureTagAttribute : AzureTagAttribute
{
}
