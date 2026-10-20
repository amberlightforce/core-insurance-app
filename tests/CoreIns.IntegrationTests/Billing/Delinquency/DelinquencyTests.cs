using System.Globalization;
using System.Net;
using CoreIns.IntegrationTests.Reinsurance.Registry;
using CoreIns.Modules.Billing;
using CoreIns.Modules.Billing.Commands;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Services;
using CoreIns.Modules.Compliance.Contracts;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Delinquency;

/// <summary>A scripted DOC: records the requested letters and answers with a document id. Delivery proof is sent by the test.</summary>
internal sealed class FakeNoticeDocuments : IDocumentRequestService
{
    public List<DocumentRequestSpec> Requests { get; } = [];

    public Task<DocumentRequestOutcome> RequestAsync(DocumentRequestSpec spec, CancellationToken cancellationToken = default)
    {
        Requests.Add(spec);
        return Task.FromResult(new DocumentRequestOutcome(new DocumentId(Guid.NewGuid()), DocumentNumber.Parse("DOC-2026-000001"), "Rendered", spec.Medium ?? "REGISTERED_POST", false));
    }
}

/// <summary>A scripted CMP clock service: records the calls BIL makes; the test sends ClockElapsed itself.</summary>
internal sealed class FakeNonpayClocks : IComplianceClockService
{
    public List<ClockStartRequest> Started { get; } = [];

    public List<ClockStopRequest> Stopped { get; } = [];

    public List<ClockPauseRequest> Paused { get; } = [];

    public List<ClockResumeRequest> Resumed { get; } = [];

    public Guid LastInstance { get; private set; }

    public Task<ClockStartResponse> StartAsync(ClockStartRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        Started.Add(request);
        LastInstance = Guid.NewGuid();
        return Task.FromResult(new ClockStartResponse
        {
            InstanceId = LastInstance,
            Status = ClockStartResponse.StatusValue.Running,
            Deadline = request.StartTime?.Plus(TimeSpan.FromDays(31)),
            ValueVersion = 1,
        });
    }

    public Task<ClockStopResponse> StopAsync(ClockStopRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        Stopped.Add(request);
        return Task.FromResult(new ClockStopResponse { InstanceState = ClockStopResponse.InstanceStateValue.Met });
    }

    public Task<ClockPauseResponse> PauseAsync(ClockPauseRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        Paused.Add(request);
        return Task.FromResult(new ClockPauseResponse { InstanceId = request.InstanceId!.Value, Status = ClockPauseResponse.StatusValue.Paused, ValueVersion = 2 });
    }

    public Task<ClockResumeResponse> ResumeAsync(ClockResumeRequest request, CommandOptions options, CancellationToken cancellationToken = default)
    {
        Resumed.Add(request);
        return Task.FromResult(new ClockResumeResponse { InstanceId = request.InstanceId!.Value, Status = ClockResumeResponse.StatusValue.Running, ValueVersion = 3 });
    }

    public Task<ClockCancelResponse> CancelAsync(ClockCancelRequest request, CommandOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ClockGetResponse> GetAsync(string id, string? subject = null, string? code = null, string? status = null, string? deadlineRange = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<System.Text.Json.JsonElement> ImportAsync(ClockImportRequest request, CommandOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ClockQueryPage> QueryAsync(
        string? cursor = null, int? limit = null, Guid? id = null, string? subject = null, string? code = null, string? status = null, string? deadlineRange = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>
/// SL6-BIL-DELINQ main paths against fake DOC and CMP services (their real ones are not wired together here): notice, clock,
/// cancellation request, cure, returned letter, tolerance, arrangement and hold, and the overdue sweep. Amounts are illustrative.
/// </summary>
public sealed class DelinquencyTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly FakeNoticeDocuments _documents = new();
    private readonly FakeNonpayClocks _clocks = new();
    private readonly MutableClock _time = new(Instant.FromDateTimeOffset(DateTimeOffset.UtcNow));
    private BillingSlice _slice = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await StartAsync();

    public async ValueTask DisposeAsync() => await _slice.DisposeAsync();

    private async Task StartAsync(Action<BillingOptions>? configure = null)
    {
        if (_slice is not null)
        {
            await _slice.DisposeAsync();
        }

        _slice = new BillingSlice(database, services =>
        {
            services.RemoveAll<IDocumentRequestService>();
            services.AddSingleton<IDocumentRequestService>(_documents);
            services.RemoveAll<IComplianceClockService>();
            services.AddSingleton<IComplianceClockService>(_clocks);
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(_time);
            if (configure is not null)
            {
                services.Configure(configure);
            }
        });
        await _slice.SeedAsync();
    }

    private static object Money(decimal amount) => new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" };

    private sealed record Billed(BoundPolicy Policy, string AccountId, string InvoiceId);

    private async Task<Billed> BilledAsync()
    {
        var policy = await _slice.BindAsync();
        await _slice.DrainAsync();
        var invoice = await _slice.InvoiceOfAsync(policy.PolicyId);
        return new Billed(policy, invoice.Text("invoice.billingAccountId"), invoice.Text("invoice.invoiceId"));
    }

    /// <summary>Runs a command handler in its own scope, with the request context of the legal entity (as an event handler would).</summary>
    private async Task<TResult> RunAsync<TCommand, TResult>(Billed billed, TCommand command)
        where TCommand : ICommand<TResult>
    {
        var envelope = (await _slice.EnvelopesAsync(billed.AccountId)).First();
        await using var scope = _slice.Policy.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.Service("delinquency-test");
        context.LegalEntity = envelope.LegalEntity;
        context.Jurisdiction = envelope.Jurisdiction;
        context.ConfigurationHash = envelope.ConfigurationHash;
        var result = await scope.ServiceProvider.GetRequiredService<ICommandHandler<TCommand, TResult>>().HandleAsync(command, Ct);
        result.IsSuccess.ShouldBeTrue(result.Error?.ToString());
        return result.Value;
    }

    private Task<int> OpenAsync(Billed b) =>
        RunAsync<OpenDelinquency, int>(b, new OpenDelinquency(new PolicyTermId(Guid.Parse(b.Policy.TermId)), DelinquencyTrigger.PaymentReturn));

    private Task<string> StateAsync(Billed b) => _slice.ScalarAsync<string>($"SELECT state FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'");

    private Task<Guid> CaseIdAsync(Billed b) => _slice.ScalarAsync<Guid>($"SELECT case_id FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'");

    private async Task<long> EventsAsync(Billed b, string type) =>
        await _slice.ScalarAsync<long>($"SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '{b.AccountId}' AND event_type = '{type}'");

    private async Task<DocumentId> DeliverAsync(Billed b, BusinessDate date)
    {
        var document = new DocumentId(await _slice.ScalarAsync<Guid>($"SELECT notice_document_id FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'"));
        (await RunAsync<NoticeDelivered, int>(b, new NoticeDelivered(document, date, "REGISTERED_POST", "REGISTERED_POST_PROOF:test"))).ShouldBe(1);
        return document;
    }

    [Fact]
    public async Task A_returned_payment_opens_a_case_the_proven_notice_starts_the_clock_and_the_elapse_requests_cancellation()
    {
        var b = await BilledAsync();

        (await OpenAsync(b)).ShouldBe(1);
        (await OpenAsync(b)).ShouldBe(1); // idempotent: still one case
        (await StateAsync(b)).ShouldBe("NOTICE_REQUESTED");
        (await EventsAsync(b, "DelinquencyStarted")).ShouldBe(1);
        _documents.Requests.Count.ShouldBe(1);
        _documents.Requests[0].TemplateKey.ShouldBe("DT-NONPAY-NOTICE");
        _documents.Requests[0].Medium.ShouldBe("REGISTERED_POST");
        _clocks.Started.Count.ShouldBe(0); // rendering is not proof (D-SL6-03)

        var notification = new BusinessDate(2026, 11, 3);
        await DeliverAsync(b, notification);
        await DeliverAsync(b, notification); // redelivery changes nothing
        (await StateAsync(b)).ShouldBe("CLOCK_RUNNING");
        _clocks.Started.Count.ShouldBe(1);
        _clocks.Started[0].ClockCode.Value.ShouldBe("BIL_NONPAY_NOTICE");
        _clocks.Started[0].SubjectRef.Type.ShouldBe("DelinquencyCase");
        _clocks.Started[0].StartTime.ShouldBe(notification.StartOfDayIn(TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")));
        (await EventsAsync(b, "NonPaymentNoticeSent")).ShouldBe(1);

        var clockId = _clocks.LastInstance;
        (await RunAsync<NonpayClockElapsed, int>(b, new NonpayClockElapsed(clockId))).ShouldBe(1);
        (await RunAsync<NonpayClockElapsed, int>(b, new NonpayClockElapsed(clockId))).ShouldBe(1); // idempotent on the clock
        (await StateAsync(b)).ShouldBe("CANCELLATION_REQUESTED");
        (await EventsAsync(b, "CancellationForNonPaymentRequested")).ShouldBe(1);
        var caseId = await CaseIdAsync(b);
        var cancellation = (await _slice.EnvelopesAsync(b.AccountId, "CancellationForNonPaymentRequested")).Single();
        cancellation.BusinessKeys["requestId"].ShouldBe(caseId.ToString());
        cancellation.BusinessKeys["policyTermId"].ShouldBe(b.Policy.TermId);
        cancellation.BusinessKeys["clockInstanceId"].ShouldBe(clockId.ToString());
    }

    [Fact]
    public async Task A_payment_that_clears_the_arrears_cures_the_case_and_stops_the_clock()
    {
        var b = await BilledAsync();
        (await OpenAsync(b)).ShouldBe(1);
        await DeliverAsync(b, new BusinessDate(2026, 11, 3));

        var (response, taken) = await _slice.PostAsync("/api/bil/v1/payments/take",
            new { billingAccountId = b.AccountId, amount = Money(b.Policy.Total), method = "CASHIER", invoiceId = b.InvoiceId });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, taken?.ToJsonString());

        (await StateAsync(b)).ShouldBe("RESOLVED");
        (await _slice.ScalarAsync<string>($"SELECT outcome FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'")).ShouldBe("PAID");
        _clocks.Stopped.Count.ShouldBe(1);
        _clocks.Stopped[0].InstanceId.ShouldBe(_clocks.LastInstance);
        _clocks.Stopped[0].Outcome.ShouldBe(ClockStopRequest.OutcomeValue.Met);
        (await EventsAsync(b, "DelinquencyResolved")).ShouldBe(1);

        // A late ClockElapsed (race) finds a resolved case and requests nothing.
        await RunAsync<NonpayClockElapsed, int>(b, new NonpayClockElapsed(_clocks.LastInstance));
        (await EventsAsync(b, "CancellationForNonPaymentRequested")).ShouldBe(0);
    }

    [Fact]
    public async Task A_returned_letter_starts_no_clock_and_records_a_work_request()
    {
        var b = await BilledAsync();
        (await OpenAsync(b)).ShouldBe(1);
        var document = new DocumentId(await _slice.ScalarAsync<Guid>($"SELECT notice_document_id FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'"));

        await RunAsync<NoticeFailed, int>(b, new NoticeFailed(document, "RETURNED_TO_SENDER"));

        (await StateAsync(b)).ShouldBe("NOTICE_RETURNED");
        _clocks.Started.Count.ShouldBe(0);
        (await _slice.ScalarAsync<long>("SELECT count(*) FROM bil.intake_exception WHERE kind = 'WRK-BIL-NONPAY-NOTICE-RETURNED'")).ShouldBe(1);
        (await EventsAsync(b, "NonPaymentNoticeSent")).ShouldBe(0);
    }

    [Fact]
    public async Task Arrears_within_the_tolerance_resolve_the_case_when_the_clock_elapses()
    {
        await StartAsync(o => o.DelinquencyTolerance = 100_000m);
        var b = await BilledAsync();
        (await OpenAsync(b)).ShouldBe(1);
        await DeliverAsync(b, new BusinessDate(2026, 11, 3));

        await RunAsync<NonpayClockElapsed, int>(b, new NonpayClockElapsed(_clocks.LastInstance));

        (await StateAsync(b)).ShouldBe("RESOLVED");
        (await _slice.ScalarAsync<string>($"SELECT outcome FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'")).ShouldBe("TOLERANCE");
        (await EventsAsync(b, "CancellationForNonPaymentRequested")).ShouldBe(0);
        (await EventsAsync(b, "DelinquencyResolved")).ShouldBe(1);
    }

    [Fact]
    public async Task An_arrangement_and_a_hold_pause_the_clock_and_block_the_cancellation()
    {
        var b = await BilledAsync();
        (await OpenAsync(b)).ShouldBe(1);
        await DeliverAsync(b, new BusinessDate(2026, 11, 3));
        var caseId = await CaseIdAsync(b);

        var (contact, contacted) = await _slice.PostAsync("/api/bil/v1/delinquencies/record-contact",
            new { data = new { delinquencyId = caseId, channel = "PHONE", outcome = "SPOKE_TO_PAYER", note = "will pay" } });
        contact.StatusCode.ShouldBe(HttpStatusCode.OK, contacted?.ToJsonString());
        contacted!["process"]!["contacts"]!.AsArray().Count.ShouldBe(1);

        var (arranged, arrangedBody) = await _slice.PostAsync("/api/bil/v1/delinquencies/arrange",
            new { data = new { delinquencyId = caseId, promiseDate = "2026-12-01", note = "promise" } });
        arranged.StatusCode.ShouldBe(HttpStatusCode.OK, arrangedBody?.ToJsonString());
        arrangedBody.Text("process.state").ShouldBe("ARRANGED");
        _clocks.Paused.Count.ShouldBe(1);

        // An elapse while paused/arranged requests nothing; a person decides in the PSR queue.
        await RunAsync<NonpayClockElapsed, int>(b, new NonpayClockElapsed(_clocks.LastInstance));
        (await EventsAsync(b, "CancellationForNonPaymentRequested")).ShouldBe(0);

        var (resumed, resumedBody) = await _slice.PostAsync("/api/bil/v1/delinquencies/release-hold", new { data = new { delinquencyId = caseId, release = true } });
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK, resumedBody?.ToJsonString());
        resumedBody.Text("process.state").ShouldBe("CLOCK_RUNNING");
        _clocks.Resumed.Count.ShouldBe(1);

        var (held, heldBody) = await _slice.PostAsync("/api/bil/v1/delinquencies/release-hold", new { data = new { delinquencyId = caseId, reason = "vulnerable customer" } });
        held.StatusCode.ShouldBe(HttpStatusCode.OK, heldBody?.ToJsonString());
        heldBody.Text("process.state").ShouldBe("ON_HOLD");
        _clocks.Paused.Count.ShouldBe(2);

        var (got, gotBody) = await _slice.GetAsync($"/api/bil/v1/delinquencies/{caseId}");
        got.StatusCode.ShouldBe(HttpStatusCode.OK, gotBody?.ToJsonString());
        gotBody.Text("process.state").ShouldBe("ON_HOLD");
        var (listed, page) = await _slice.GetAsync($"/api/bil/v1/delinquencies?billingAccountId={b.AccountId}");
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        page!["items"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_sweep_opens_a_case_for_an_invoice_overdue_past_the_grace_period_only()
    {
        var b = await BilledAsync();

        (await RunAsync<SweepDelinquency, int>(b, new SweepDelinquency())).ShouldBe(0); // due today: not overdue
        _time.Now += TimeSpan.FromDays(3);
        (await RunAsync<SweepDelinquency, int>(b, new SweepDelinquency())).ShouldBe(0); // inside the 5-day grace
        _time.Now += TimeSpan.FromDays(4);
        (await RunAsync<SweepDelinquency, int>(b, new SweepDelinquency())).ShouldBeGreaterThanOrEqualTo(1); // the sweep is database-wide: other tests' overdue invoices may open cases too
        (await RunAsync<SweepDelinquency, int>(b, new SweepDelinquency())).ShouldBe(0); // already open

        (await StateAsync(b)).ShouldBe("NOTICE_REQUESTED");
        (await _slice.ScalarAsync<string>($"SELECT \"trigger\" FROM bil.delinquency_case WHERE term_id = '{b.Policy.TermId}'")).ShouldBe("OVERDUE");
        _documents.Requests.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task The_scheduled_runner_opens_the_overdue_case_as_a_service_actor_in_the_stamp_entity()
    {
        var b = await BilledAsync();
        var runner = _slice.Policy.Factory.Services.GetRequiredService<DelinquencySweepRunner>();

        (await runner.RunAsync(Ct)).ShouldBe(0); // not overdue yet
        _time.Now += TimeSpan.FromDays(7);
        (await runner.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        (await StateAsync(b)).ShouldBe("NOTICE_REQUESTED");
        (await runner.RunAsync(Ct)).ShouldBe(0); // idempotent
    }
}
