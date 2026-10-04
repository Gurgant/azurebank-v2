using System.Reflection;
using Microsoft.Data.SqlClient;

namespace AzureBank.Tests.Fixtures;

/// <summary>
/// SQL Server errors as SqlClient reports them, for a test that needs one the server did not send.
/// </summary>
/// <remarks>
/// <see cref="SqlException"/> has no public constructor. These are built the way SqlClient builds
/// its own, through its internal factory, by reflection, and carry the number, the class and the
/// message a test gives them; the rest of an error (its state, server, procedure and line) is
/// filler. That is enough for code that reads the number and the class: EF's list of the errors
/// it runs work again for, the handler of the outage 503, the wait <c>migrate</c> runs. A test
/// about the server's own words makes the real error.
/// </remarks>
internal static class SqlErrors
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>A <see cref="SqlException"/> carrying one error of the given number and class.</summary>
    internal static SqlException Of(int number, byte errorClass, string? message = null) =>
        Carrying(errorClass, [(number, message ?? $"error {number}")]);

    /// <summary>A <see cref="SqlException"/> carrying one error per number, all of one class.</summary>
    internal static SqlException OfEach(byte errorClass, params int[] numbers) =>
        Carrying(errorClass, numbers.Select(number => (number, $"error {number}")));

    private static SqlException Carrying(byte errorClass, IEnumerable<(int Number, string Message)> errors)
    {
        var collection = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        var add = typeof(SqlErrorCollection).GetMethod("Add", Any)!;
        var ctor = typeof(SqlError).GetConstructor(
            Any, [typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int), typeof(Exception)])!;

        foreach (var (number, message) in errors)
        {
            add.Invoke(collection, [ctor.Invoke([number, (byte)0, errorClass, "server", message, "", 0, null])]);
        }

        var create = typeof(SqlException).GetMethod(
            "CreateException", Any, [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)create.Invoke(null, [collection, "16.0"])!;
    }
}
