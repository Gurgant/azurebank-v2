extern alias seeder;

using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using ConnectionTarget = seeder::AzureBank.Seeder.Commands.ConnectionTarget;
using ConnectionTargetKind = seeder::AzureBank.Seeder.Commands.ConnectionTargetKind;

namespace AzureBank.Tests.Unit.Tools;

/// <summary>
/// What every Seeder command asks of its connection string before it opens anything: is there one,
/// can it be read, does it name a database, and does it name an Azure SQL server. The answer comes
/// from the string alone.
/// </summary>
/// <remarks>
/// <para>
/// The Azure rule goes by the server's name: the five suffixes SqlClient itself treats as Azure SQL
/// (<c>.database.windows.net</c>, <c>.database.cloudapi.de</c>, <c>.database.usgovcloudapi.net</c>,
/// <c>.database.chinacloudapi.cn</c>, <c>.database.fabric.microsoft.com</c>). <c>seed</c> and
/// <c>reset</c> refuse such a name; a private alias or an IP address is not recognised, and the
/// Seeder's README says so.
/// </para>
/// <para>
/// No name here resolves: <c>not_a_server</c> holds an underscore, which an Azure SQL server name
/// cannot.
/// </para>
/// </remarks>
public class ConnectionTargetTests
{
    private const string Rest = ";Database=x;User Id=u;Password=not-a-secret";

    [Theory]
    [InlineData("Server=not_a_server.database.windows.net", "not_a_server.database.windows.net")]
    [InlineData("Server=tcp:not_a_server.database.windows.net,1433", "not_a_server.database.windows.net")]
    [InlineData("Server=TCP:NOT_A_SERVER.DATABASE.WINDOWS.NET,1433", "NOT_A_SERVER.DATABASE.WINDOWS.NET")]
    [InlineData("Server=not_a_server.database.windows.net.", "not_a_server.database.windows.net")]
    [InlineData("Server=not_a_server.database.windows.net..,1433", "not_a_server.database.windows.net")]
    [InlineData("Server= tcp:not_a_server.database.windows.net , 1433 ", "not_a_server.database.windows.net")]
    [InlineData("Server=not_a_server.privatelink.database.windows.net", "not_a_server.privatelink.database.windows.net")]
    [InlineData("Server=tcp:not_a_server.public.0a1b2c3d4e5f.database.windows.net,3342", "not_a_server.public.0a1b2c3d4e5f.database.windows.net")]
    [InlineData("Server=not_a_server.database.cloudapi.de", "not_a_server.database.cloudapi.de")]
    [InlineData("Server=not_a_server.database.usgovcloudapi.net", "not_a_server.database.usgovcloudapi.net")]
    [InlineData("Server=not_a_server.database.chinacloudapi.cn", "not_a_server.database.chinacloudapi.cn")]
    [InlineData("Server=not_a_server.database.fabric.microsoft.com", "not_a_server.database.fabric.microsoft.com")]
    [InlineData("Data Source=not_a_server.database.windows.net", "not_a_server.database.windows.net")]
    [InlineData("Address=not_a_server.database.windows.net", "not_a_server.database.windows.net")]
    [InlineData("Addr=not_a_server.database.windows.net", "not_a_server.database.windows.net")]
    [InlineData("Network Address=not_a_server.database.windows.net", "not_a_server.database.windows.net")]
    public void AnAzureSqlName_IsRecognised_HoweverItIsWritten(string server, string expectedServer)
    {
        var target = ConnectionTarget.Read(server + Rest);

        using var all = new AssertionScope();
        target.Kind.Should().Be(ConnectionTargetKind.Named);
        target.Server.Should().Be(expectedServer, "the refusal names the server it refused");
        target.IsAzureSql.Should().BeTrue();
    }

    [Theory]
    [InlineData(@"Server=(localdb)\MSSQLLocalDB", "(localdb)")]
    [InlineData("Server=localhost,1433", "localhost")]
    [InlineData("Server=sqlserver,1433", "sqlserver")]
    [InlineData("Server=127.0.0.1,14330", "127.0.0.1")]
    [InlineData("Server=.", ".")]
    [InlineData(@"Server=.\SQLEXPRESS", ".")]
    [InlineData("Server=database.windows.net.example.com", "database.windows.net.example.com")]
    [InlineData("Server=not_a_server.windows.net", "not_a_server.windows.net")]
    [InlineData("Server=database.windows.net", "database.windows.net")]
    [InlineData("Server=xdatabase.windows.net", "xdatabase.windows.net")]
    public void AnyOtherName_IsNotAzureSql(string server, string expectedServer)
    {
        var target = ConnectionTarget.Read(server + Rest);

        using var all = new AssertionScope();
        target.Kind.Should().Be(ConnectionTargetKind.Named);
        target.Server.Should().Be(expectedServer);
        target.IsAzureSql.Should().BeFalse();
    }

    [Fact]
    public void TheLastServerKeywordWins_AsItDoesForSqlClient()
    {
        // SqlClient opens the last one written, under any of its names, so the rule reads that one.
        var first = ConnectionTarget.Read("Server=localhost;Data Source=not_a_server.database.windows.net" + Rest);
        var second = ConnectionTarget.Read("Server=not_a_server.database.windows.net;Addr=localhost" + Rest);

        using var all = new AssertionScope();
        first.IsAzureSql.Should().BeTrue();
        second.IsAzureSql.Should().BeFalse();
        second.Server.Should().Be("localhost");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoString_IsMissing(string? connectionString)
    {
        var target = ConnectionTarget.Read(connectionString);

        using var all = new AssertionScope();
        target.Kind.Should().Be(ConnectionTargetKind.Missing);
        target.IsAzureSql.Should().BeFalse();
        target.Server.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Server=localhost,1433;User Id=u;Password=not-a-secret", "localhost", false)]
    [InlineData("Server=localhost,1433;Database=;User Id=u;Password=not-a-secret", "localhost", false)]
    [InlineData("Server=localhost,1433;Initial Catalog=   ;User Id=u;Password=not-a-secret", "localhost", false)]
    [InlineData("Server=tcp:not_a_server.database.windows.net,1433;User Id=u;Password=not-a-secret", "not_a_server.database.windows.net", true)]
    public void AStringThatNamesNoDatabase_IsItsOwnKind_AndItsServerIsStillRead(
        string connectionString, string expectedServer, bool expectedAzureSql)
    {
        // SqlClient accepts such a string, and the server then opens the login's default database.
        // The server is still read: a caller that only asks "is this Azure SQL" must get the same
        // answer with or without a database in the string.
        var target = ConnectionTarget.Read(connectionString);

        using var all = new AssertionScope();
        target.Kind.Should().Be(ConnectionTargetKind.NoDatabase);
        target.Server.Should().Be(expectedServer);
        target.IsAzureSql.Should().Be(expectedAzureSql);
    }

    [Theory]
    // A password holding an unquoted ';' leaves a tail the parser reads as a keyword.
    [InlineData("Server=x;Database=d;User Id=u;Password=aaa;FRAGMENT=bbb")]
    [InlineData("Server=x;Connect Timeout=soon")]
    [InlineData("Database=d;User Id=u;Password=p")]
    [InlineData("Server=;Database=d")]
    [InlineData("Server=   ;Database=d")]
    public void AStringTheParserRefuses_OrOneThatNamesNoServer_IsUnreadable(string connectionString)
    {
        var target = ConnectionTarget.Read(connectionString);

        using var all = new AssertionScope();
        target.Kind.Should().Be(ConnectionTargetKind.Unreadable);
        target.IsAzureSql.Should().BeFalse();
        target.Server.Should().BeEmpty("nothing of an unreadable string is repeated");
    }
}
