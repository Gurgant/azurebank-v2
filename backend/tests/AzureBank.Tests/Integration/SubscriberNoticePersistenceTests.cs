using System.Net;
using System.Net.Http.Json;
using AzureBank.Infrastructure.Data;
using AzureBank.Shared.Constants;
using AzureBank.Shared.DTOs.Auth;
using AzureBank.Shared.Entities;
using AzureBank.Tests.Fixtures;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureBank.Tests.Integration;

/// <summary>
/// Drives a PIN enrolment through the real endpoint and asserts that the OWED-NOTICE ROW EXISTS
/// (ADR-0045) — the table, never a mock of the writer.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="AuditTrailPersistenceTests"/>, for the same reason it was written:
/// the enrolment audit row once shipped added AFTER <c>UserManager.UpdateAsync</c> had saved, so
/// the endpoint answered 200, the unit test that checked the writer was CALLED stayed green, and the
/// table held nothing. A notice row added in the same wrong place would vanish the same way, and the
/// only assertion that can tell is one that reads the table after driving the real host.
/// </para>
/// <para>
/// What these three CANNOT prove is rollback: the InMemory provider has no transactions. Both
/// directions of D1 for this row are proved on SQL Server by <c>SubscriberNoticeSqlServerTests</c>.
/// </para>
/// </remarks>
public class SubscriberNoticePersistenceTests : IntegrationTestBase
{
    public SubscriberNoticePersistenceTests(CustomWebApplicationFactory factory) : base(factory) { }

    [Fact]
    public async Task EnrollingAPin_WritesTheNoticeRow_InTheSameSaveAsTheAuditRow()
    {
        var (token, userId, _) = await RegisterTestUserAsync();

        await SetPinAsync(token);

        var notices = await NoticesForAsync(userId);
        notices.Should().HaveCount(
            1, "exactly one notice is owed for one enrolment — zero means the row was added and never "
               + "saved, more than one means it was written twice");

        var notice = notices[0];
        notice.Event.Should().Be(SecurityEvents.PinEnrolled);
        notice.DeliveredAt.Should().BeNull("nothing in the API renders a notice; it is owed until the operator runs notify");
        notice.DeliveryReceipt.Should().BeNull();

        /*
          TWO CLOCKS, so a window rather than equality: the notice reads DateTime.UtcNow in
          AuthService and the audit row reads AuditService's clock, milliseconds apart in the same
          request. What the window pins is that the notice is dated by the enrolment it rode, not by
          some later run.
        */
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();
        var audit = await context.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.ActorUserId == userId && e.Event == SecurityEvents.PinEnrolled);
        notice.OccurredAt.Should().BeCloseTo(audit.OccurredAt, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ChangingAPin_OwesNoNotice_BecauseOnlyAnEnrolmentAddsOne()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        await SetPinAsync(token, pin: "123456");

        // A change costs the current PIN, not the password (ADR-0040), and writes no notice: NIST
        // SP 800-63B-4 §4.1.2.1 says "added"; whether a CHANGE should notify is a separate decision
        // recorded in ADR-0045, and this test is what pins "once per account" now that no unique
        // index does.
        var change = await Client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "999999", CurrentPin = "123456" }, JsonOptions);
        change.StatusCode.Should().Be(HttpStatusCode.OK);

        (await NoticesForAsync(userId)).Should().HaveCount(1, "the change path adds nothing");
    }

    [Fact]
    public async Task ARefusedEnrolment_OwesNoNotice()
    {
        var (token, userId, _) = await RegisterTestUserAsync();
        SetAuthHeader(token);

        // 401 and 422 as AuthEndpointTests measure them through this same host.
        var wrong = await Client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "424242", Password = "NotThePassword1!" }, JsonOptions);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var none = await Client.PostAsJsonAsync(
            "/api/auth/pin", new SetPinRequest { Pin = "424242" }, JsonOptions);
        none.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        /*
          HONEST ABOUT WHAT THIS PROVES. Both refusals throw before any save, so on InMemory the row
          is absent whether the Add sits above or below the refusal — the throw unwinds either way.
          This documents that a refused enrolment owes nothing; the ordering that keeps it true under
          a FAILED save is proved with an injected fault on SQL Server.
        */
        (await NoticesForAsync(userId)).Should().BeEmpty("no enrolment happened, so nothing is owed");
    }

    private async Task<List<SubscriberNotice>> NoticesForAsync(Guid userId)
    {
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AzureBankDbContext>();

        // Filtered by USER, never counted globally: the factory is shared across the class.
        return await context.SubscriberNotices
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .ToListAsync();
    }
}
