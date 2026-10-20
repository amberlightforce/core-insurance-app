using System.Net;
using CoreIns.Modules.Billing.Contracts.Events;
using CoreIns.Modules.Policy.Commands.Cancellation.NonPayment;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Policy.Cancellation;

/// <summary>
/// SL6-POL-NONPAY: <c>bil.CancellationForNonPaymentRequested</c> / <c>DelinquencyResolved</c> → POL, on PostgreSQL 17 with the real Host. The clock is
/// manual (bind at day 0). Amounts are ILLUSTRATIVE TEST DATA. Main paths: exactly once, vulnerable customer → exception only (then decided),
/// cure after scheduling → rescinded.
/// </summary>
public sealed class NonPaymentCancellationTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Manager = "Staff.UnderwritingManager";
    private static readonly TimeZoneInfo Athens = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
    private static readonly string[] HandlingRules = ["NO_NONPAY_CANCEL"];
    private readonly FakeVulnerability _vulnerability = new();
    private CancellationSlice _slice = null!;
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _slice = new CancellationSlice(database.AppConnectionString, settings: new Dictionary<string, string?> { ["Policy:LockWaitSeconds"] = "1" });
        await _slice.SeedAsync();
        _factory = _slice.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IVulnerabilityCheck>(_vulnerability)));
    }

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    [Fact]
    public async Task REQ_POL_210_the_request_cancels_pro_rata_exactly_once()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(40));
        var request = RequestFor(policy, Today());

        await DeliverAsync(request);
        await DeliverAsync(request); // the second delivery (POL X-12)

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe(1);
        (await ScalarAsync<string>(data, $"SELECT status FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe("EXECUTED");
        (await ScalarAsync<string>(data, $"SELECT cancellation_source || '/' || reason_code FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'"))
            .ShouldBe("NonPayment/NON_PAYMENT");

        // Effective at the start of the requested day (Athens); the credit is the pro-rata share of 430.00 from then to the term end.
        var effective = Today().StartOfDayIn(Athens);
        (await ScalarAsync<DateTime>(data, $"SELECT cancelled_at FROM pol.policy_term WHERE term_id = '{policy.TermId}' AND recorded_to IS NULL"))
            .ShouldBe(effective.ToDateTimeOffset().UtcDateTime);
        var credit = await ScalarAsync<decimal>(data, $"SELECT sum(amount) FROM pol.charge_line WHERE term_id = '{policy.TermId}' AND charge_type = 'PREM-MTPL' AND amount < 0");
        credit.ShouldBeLessThan(0m);
        credit.ShouldBeGreaterThan(-430m);
    }

    [Fact]
    public async Task REQ_POL_210_a_vulnerable_customer_raises_a_psr_exception_and_staff_decide()
    {
        _vulnerability.Vulnerable = true;
        var held = await _slice.BindAsync();
        var proceeded = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(40));
        await DeliverAsync(RequestFor(held, Today()));
        await DeliverAsync(RequestFor(proceeded, Today()));

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id IN ('{held.PolicyId}', '{proceeded.PolicyId}') AND job_type = 'CANCELLATION'")).ShouldBe(0);

        var (listed, page) = await SendAsync(_factory.CreateClient(), HttpMethod.Get, "/api/pol/v1/psr-exceptions?status=PENDING&limit=200", roles: Manager);
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var heldItem = page!["items"]!.AsArray().Single(i => i.Text("termId") == held.TermId);
        heldItem.Text("reason").ShouldBe("VULNERABLE_CUSTOMER");
        var proceedItem = page["items"]!.AsArray().Single(i => i.Text("termId") == proceeded.TermId);

        var client = _factory.CreateClient();
        var hold = new { exceptionId = heldItem.Text("exceptionId"), decision = "HOLD", note = "Customer is in hospital; call first." };
        (await SendAsync(client, HttpMethod.Post, "/api/pol/v1/psr-exceptions/decide", hold)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden); // the underwriter may not decide
        var (held1, heldView) = await SendAsync(client, HttpMethod.Post, "/api/pol/v1/psr-exceptions/decide", hold, roles: Manager);
        held1.StatusCode.ShouldBe(HttpStatusCode.OK, heldView?.ToJsonString());
        heldView.Text("status").ShouldBe("HOLD");
        (await SendAsync(client, HttpMethod.Post, "/api/pol/v1/psr-exceptions/decide", hold, roles: Manager)).Body.Text("code").ShouldBe("POL-ERR-ILLEGAL-TRANSITION");

        var proceed = new { exceptionId = proceedItem.Text("exceptionId"), decision = "PROCEED", note = "Checked with the customer; proceed." };
        var (done, view) = await SendAsync(client, HttpMethod.Post, "/api/pol/v1/psr-exceptions/decide", proceed, roles: Manager);
        done.StatusCode.ShouldBe(HttpStatusCode.OK, view?.ToJsonString());
        view.Text("status").ShouldBe("PROCEED");
        view.Text("jobId").ShouldNotBeNull();

        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{held.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{proceeded.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(1);
        _vulnerability.Vulnerable = false;
    }

    [Fact]
    public async Task NX_HD_02_the_real_check_cancels_an_unflagged_party_and_never_a_party_flagged_in_pty()
    {
        // No IVulnerabilityCheck override: the real PartyVulnerabilityCheck asks PTY (pty.Vulnerability.query) for the policyholder.
        var realCheck = _slice.Factory;
        var clear = await _slice.BindAsync();
        var flagged = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(40));

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        var flaggedParty = await ScalarAsync<Guid>(data, $"SELECT policyholder_party_id FROM pol.policy WHERE policy_id = '{flagged.PolicyId}'");
        var (recorded, recordedBody) = await SendAsync(
            _slice.Client, HttpMethod.Post, "/api/pty/v1/vulnerabilities/record", new { partyId = flaggedParty, ruleCodes = HandlingRules });
        recorded.StatusCode.ShouldBe(HttpStatusCode.OK, recordedBody?.ToJsonString());

        await DeliverAsync(RequestFor(clear, Today()), realCheck);
        await DeliverAsync(RequestFor(flagged, Today()), realCheck);

        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{clear.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(1);
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{flagged.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
        (await ScalarAsync<string>(data, $"SELECT reason FROM pol.psr_exception WHERE term_id = '{flagged.TermId}'")).ShouldBe("VULNERABLE_CUSTOMER");

        // Clearing the flag works.
        var (removed, removedBody) = await SendAsync(_slice.Client, HttpMethod.Post, "/api/pty/v1/vulnerabilities/remove", new { partyId = flaggedParty });
        removed.StatusCode.ShouldBe(HttpStatusCode.OK, removedBody?.ToJsonString());
        removedBody.Text("flag").ShouldBe("false");
    }

    [Fact]
    public async Task REQ_POL_211_a_cure_before_the_effective_date_rescinds_the_scheduled_cancellation()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(40));
        var request = RequestFor(policy, new BusinessDate(Today().Value.AddDays(2)));
        await DeliverAsync(request);

        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<string>(data, $"SELECT status FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe("SCHEDULED");

        await DeliverAsync(new DelinquencyResolvedV1 { ProcessId = request.RequestId, TermId = request.TermId, Outcome = DelinquencyResolvedV1.OutcomeValue.Paid }, DelinquencyResolvedHandler.Name);
        (await ScalarAsync<string>(data, $"SELECT status FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe("RESCINDED");

        // The effective moment passes: the sweep finds nothing to run.
        await _slice.AdvanceAsync(TimeSpan.FromDays(3));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NonPaymentSweepRunner>().RunAsync(Ct);
        }

        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(0);
    }

    [Fact]
    public async Task REQ_POL_210_a_scheduled_cancellation_runs_when_its_moment_comes()
    {
        var policy = await _slice.BindAsync();
        await _slice.AdvanceAsync(TimeSpan.FromDays(40));
        await DeliverAsync(RequestFor(policy, new BusinessDate(Today().Value.AddDays(1))));
        await using var data = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<string>(data, $"SELECT status FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe("SCHEDULED");

        await _slice.AdvanceAsync(TimeSpan.FromDays(1));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<NonPaymentSweepRunner>().RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        }

        (await ScalarAsync<string>(data, $"SELECT status FROM pol.nonpay_cancellation WHERE policy_id = '{policy.PolicyId}'")).ShouldBe("EXECUTED");
        (await ScalarAsync<long>(data, $"SELECT count(*) FROM pol.job WHERE policy_id = '{policy.PolicyId}' AND job_type = 'CANCELLATION'")).ShouldBe(1);
    }

    private BusinessDate Today() => _slice.Clock.Now.ToBusinessDate(Athens);

    private static CancellationForNonPaymentRequestedV1 RequestFor(BoundPolicy policy, BusinessDate effectiveDate) =>
        new()
        {
            RequestId = Guid.CreateVersion7(),
            TermId = new PolicyTermId(Guid.Parse(policy.TermId)),
            OverdueAmount = new Money(120.00m, Currency.FromCode("EUR")),
            NoticeDocumentId = DocumentId.New(),
            ProofRef = "DOC:registered-post-proof",
            ClockInstanceId = Guid.CreateVersion7(),
            RequestedEffectiveDate = effectiveDate,
            Motor = true,
        };

    private Task DeliverAsync(CancellationForNonPaymentRequestedV1 payload, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null) =>
        DeliverAsync(payload, CancellationForNonPaymentRequestedHandler.Name, factory);

    private async Task DeliverAsync<T>(T payload, string handlerName, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? factory = null)
        where T : class, CoreIns.Platform.Contracts.Events.IEventPayload<T>
    {
        await using var scope = (factory ?? _factory).Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<RequestContext>();
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.Actor = ActorRef.Service("test-bil");
        context.ConfigurationHash = ConfigurationHash.Parse((await services.GetRequiredService<CoreIns.Modules.Market.Contracts.IMarketConfigurationService>().CurrentHashAsync()).Hash!.Value.Value);
        var session = services.GetRequiredService<DbSession>();
        await using var transaction = await session.BeginTransactionAsync(Ct);
        var keys = BusinessKeys.Empty.With("billingAccountId", Guid.NewGuid().ToString());
        string aggregate;
        switch (payload)
        {
            case CancellationForNonPaymentRequestedV1 r:
                keys = keys.With("requestId", r.RequestId.ToString()).With("policyTermId", r.TermId.Value.ToString())
                    .With("noticeDocumentId", r.NoticeDocumentId.Value.ToString()).With("clockInstanceId", r.ClockInstanceId.ToString());
                aggregate = r.RequestId.ToString();
                break;
            case DelinquencyResolvedV1 d:
                keys = keys.With("processId", d.ProcessId.ToString()).With("policyTermId", d.TermId.Value.ToString());
                aggregate = d.ProcessId.ToString();
                break;
            default:
                throw new InvalidOperationException();
        }

        var envelope = services.GetRequiredService<IEventPublisher>().Publish(new OutgoingEvent(EventDescriptor.From(T.Descriptor), "BillingAccount", aggregate, payload, keys));
        await services.GetRequiredService<EventHandlerRegistry>().Find(handlerName)!.Invoke(services, envelope, Ct);
        await transaction.CommitAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class FakeVulnerability : IVulnerabilityCheck
    {
        public bool Vulnerable { get; set; }

        public Task<bool> IsVulnerableAsync(PartyId party, CancellationToken cancellationToken) => Task.FromResult(Vulnerable);
    }
}

/// <summary>NX-HD-10: which cancellation refusals are retried and which are dead-lettered at once.</summary>
public sealed class NonPaymentRefusalClassificationTests
{
    [Fact]
    public void A_business_refusal_is_permanent_and_an_unavailable_dependency_is_transient()
    {
        CancellationForNonPaymentRequestedHandler.IsTransient(DomainError.Of(ModuleCode.POL, "DEPENDENCY-UNAVAILABLE")).ShouldBeTrue();
        CancellationForNonPaymentRequestedHandler.IsTransient(DomainError.Of(ModuleCode.POL, "ILLEGAL-TRANSITION")).ShouldBeFalse();
        CancellationForNonPaymentRequestedHandler.IsTransient(DomainError.Of(ModuleCode.POL, "GATE-FAILED")).ShouldBeFalse();
    }
}
