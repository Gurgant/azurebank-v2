using System.Runtime.CompilerServices;

namespace AzureBank.Bff.Tests;

/// <summary>
/// The service credential every BFF test host starts with (ADR-0055). The BFF refuses to start
/// without one, and these tests build their hosts in eighteen places, so the key is put in the
/// process environment once, before any of them: <c>ServiceCredential__BffKey</c> is how a
/// deployment supplies it too. NOT a real secret.
/// </summary>
internal static class TestServiceCredential
{
    internal const string Key = "bff-tests-only-service-credential-0123456789abcdef";

    [ModuleInitializer]
    internal static void Supply() =>
        Environment.SetEnvironmentVariable("ServiceCredential__BffKey", Key);
}
