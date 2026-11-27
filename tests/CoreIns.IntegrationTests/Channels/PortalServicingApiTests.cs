using System.Net;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using CoreIns.Testing.Contracts.Fakes.Party;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>
/// BO-CHN-03: customer self-service commands. POL and PTY are the generated sandbox doubles (their calls are recorded), the matrix is the dev
/// baseline: owner and household act, another customer, staff and an unlinked identity get 404, REFER performs nothing and raises a ticket,
/// DENY is 403, a stale contact version is refused.
/// </summary>
public sealed class PortalServicingApiTests(PostgresFixture database) : PortalEngagementHost(database), IClassFixture<PostgresFixture>
{
    private readonly FakePolicyPolicyChangeService _changes = new();
    private readonly FakePolicyJobService _jobs = new();
    private readonly FakePolicyRenewalService _renewals = new();
    private readonly FakePartyLocationService _locations = new();
    private readonly FakePartyVulnerabilityService _vulnerability = new();
    private readonly FakePartyPartyService _partyService = new();
    private readonly JobId _job = JobId.New();
    private readonly Guid _address = Guid.NewGuid();
    private readonly Guid _phone = Guid.NewGuid();
    private decimal _quotedTotal = 20m;
    private bool _vulnerable;

    protected override void ConfigureExtra(IServiceCollection services, FakePolicyPolicyService policies)
    {
        var changeCreated = _changes.Canned<PolicyChangeCreateResponse>("pol.PolicyChange.create");
        _changes.Setup("pol.PolicyChange.create", _ => changeCreated with { JobId = JobId.New() });
        var quote = _jobs.Canned<JobQuoteResponse>("pol.Job.quote");
        _jobs.Setup("pol.Job.quote", call => quote with
        {
            JobId = ((JobQuoteRequest)call.Arguments[0]!).JobId, VersionNo = 1, Referred = false, Bindable = true,
            ServicingPreview = quote.ServicingPreview! with { TotalChange = Money.Of(_quotedTotal, "EUR"), PremiumChange = Money.Of(_quotedTotal, "EUR"), TaxChange = Money.Of(0m, "EUR") },
        });
        var draft = _jobs.Canned<JobUpdateDraftResponse>("pol.Job.updateDraft");
        _jobs.Setup("pol.Job.updateDraft", call => draft with { JobId = ((JobUpdateDraftRequest)call.Arguments[0]!).JobId, VersionNo = 1 });
        var renewalJob = _jobs.Canned<JobGetResponse>("pol.Job.get");
        _jobs.Setup("pol.Job.get", call =>
        {
            var id = (string)call.Arguments[0]!;
            // The renewal job belongs to Alice's policy; any other id is a job of Carol's policy (and not a renewal).
            return id == _job.Value.ToString("D")
                ? renewalJob with { Job = renewalJob.Job with { JobId = _job, JobType = "RENEWAL", PolicyId = PolicyAlice } }
                : renewalJob with { Job = renewalJob.Job with { JobType = "RENEWAL", PolicyId = PolicyCarol } };
        });

        var party = _partyService.Canned<PartyGetResponse>("pty.Party.get");
        var address = party.Party.Addresses[0];
        var contact = party.Party.ContactPoints[0];
        _partyService.Setup("pty.Party.get", _ => party with
        {
            Party = party.Party with
            {
                RecordVersion = 3,
                Addresses = [address with { AddressId = _address, ValidTo = null }],
                ContactPoints = [contact with { ContactPointId = _phone, Type = ContactPointType.Mobile, Value = "+30 691 234 5678" }],
            },
        });
        var added = _locations.Canned<LocationUpdateResponse>("pty.Location.update");
        _locations.Setup("pty.Location.update", call => added with { LocationId = Guid.NewGuid(), RecordVersion = 4 });
        _vulnerability.Setup("pty.Vulnerability.query", call => new VulnerabilityQueryPage
        {
            Items = [new VulnerabilityQueryItem { PartyId = (PartyId)call.Arguments[0]!, Flag = _vulnerable, RuleCodes = [] }],
            NextCursor = null,
            Limit = 10,
        });

        services.RemoveAll<IPolicyPolicyChangeService>();
        services.AddSingleton<IPolicyPolicyChangeService>(_changes);
        services.RemoveAll<IPolicyJobService>();
        services.AddSingleton<IPolicyJobService>(_jobs);
        services.RemoveAll<IPolicyRenewalService>();
        services.AddSingleton<IPolicyRenewalService>(_renewals);
        services.RemoveAll<IPartyLocationService>();
        services.AddSingleton<IPartyLocationService>(_locations);
        services.RemoveAll<IPartyVulnerabilityService>();
        services.AddSingleton<IPartyVulnerabilityService>(_vulnerability);
        services.RemoveAll<IPartyPartyService>();
        services.AddSingleton<IPartyPartyService>(_partyService);
    }

    private static object Preview(PolicyId policy) => new { policyId = policy.Value, instructions = new[] { new { op = "SET_COVERAGES", coverages = Array.Empty<object>() } } };

    [Fact]
    public async Task Preview_then_confirm_binds_exactly_the_previewed_version_once()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);

        var preview = await PostAsync("/api/chn/v1/changes/preview", a, Preview(PolicyAlice));
        preview.Response.StatusCode.ShouldBe(HttpStatusCode.OK, preview.Text);
        preview.Body!["outcome"]!.GetValue<string>().ShouldBe("ALLOW");
        preview.Body["versionNo"]!.GetValue<int>().ShouldBe(1);
        var changeId = preview.Body["changeId"]!.GetValue<string>();
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(0);

        // Without the explicit acknowledgement nothing is bound.
        (await PostAsync("/api/chn/v1/changes/confirm", a, new { changeId, acknowledged = false })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(0);

        var confirm = await PostAsync("/api/chn/v1/changes/confirm", a, new { changeId, acknowledged = true });
        confirm.Response.StatusCode.ShouldBe(HttpStatusCode.OK, confirm.Text);
        confirm.Body!["outcome"]!.GetValue<string>().ShouldBe("CONFIRMED");
        var bind = (JobBindRequest)_jobs.CallsTo("pol.Job.bind").Single().Arguments[0]!;
        bind.JobId.ShouldBe(new JobId(Guid.Parse(preview.Body["jobId"]!.GetValue<string>())));
        bind.VersionNo.ShouldBe(1);

        // A second confirm (new key) returns the first result and binds nothing more.
        (await PostAsync("/api/chn/v1/changes/confirm", a, new { changeId, acknowledged = true })).Body!["outcome"]!.GetValue<string>().ShouldBe("CONFIRMED");
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Scope_owner_household_other_customer_staff_and_unlinked()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        var b = "cust-b-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, Alice);
        await LinkAsync(b, Bob);
        await LinkAsync(c, Carol);

        (await PostAsync("/api/chn/v1/changes/preview", b, Preview(PolicyAlice))).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var other = await PostAsync("/api/chn/v1/changes/preview", c, Preview(PolicyAlice));
        other.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/changes/preview", "stranger-" + Id(), Preview(PolicyAlice))).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/changes/preview", "staff-1", Preview(PolicyAlice), "Staff.Underwriter")).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Carol cannot confirm Alice's change either.
        var mine = await PostAsync("/api/chn/v1/changes/preview", a, Preview(PolicyAlice));
        var changeId = mine.Body!["changeId"]!.GetValue<string>();
        (await PostAsync("/api/chn/v1/changes/confirm", c, new { changeId, acknowledged = true })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/changes/refer", c, new { changeId })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/portal/cancellations/request", c, new { policyId = PolicyAlice.Value, reasonCode = "PRICE" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_large_change_is_referred_and_nothing_is_bound()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);
        _quotedTotal = 900m; // over the baseline limit of EUR 500

        var preview = await PostAsync("/api/chn/v1/changes/preview", a, Preview(PolicyAlice));
        preview.Body!["outcome"]!.GetValue<string>().ShouldBe("REFER");
        var confirm = await PostAsync("/api/chn/v1/changes/confirm", a, new { changeId = preview.Body["changeId"]!.GetValue<string>(), acknowledged = true });
        confirm.Response.StatusCode.ShouldBe(HttpStatusCode.OK, confirm.Text);
        confirm.Body!["outcome"]!.GetValue<string>().ShouldBe("REFERRED");
        confirm.Body["ticketId"].ShouldNotBeNull();
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(0);
    }

    [Fact]
    public async Task Refer_hands_a_previewed_change_to_staff_and_it_can_no_longer_be_bound()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);
        var changeId = (await PostAsync("/api/chn/v1/changes/preview", a, Preview(PolicyAlice))).Body!["changeId"]!.GetValue<string>();

        var refer = await PostAsync("/api/chn/v1/changes/refer", a, new { changeId });
        refer.Response.StatusCode.ShouldBe(HttpStatusCode.OK, refer.Text);
        refer.Body!["outcome"]!.GetValue<string>().ShouldBe("REFERRED");
        refer.Body["ticketId"].ShouldNotBeNull();
        (await PostAsync("/api/chn/v1/changes/confirm", a, new { changeId, acknowledged = true })).Body!["outcome"]!.GetValue<string>().ShouldBe("REFERRED");
        _jobs.CallsTo("pol.Job.bind").Count.ShouldBe(0);
    }

    [Fact]
    public async Task Cancellation_is_a_request_for_staff_never_a_cancellation()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);

        var request = await PostAsync("/api/chn/v1/portal/cancellations/request", a, new { policyId = PolicyAlice.Value, reasonCode = "PRICE" });
        request.Response.StatusCode.ShouldBe(HttpStatusCode.OK, request.Text);
        request.Body!["outcome"]!.GetValue<string>().ShouldBe("REQUESTED");
        request.Body["ticketId"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Renewal_decline_is_performed_for_an_ordinary_customer_and_referred_for_a_vulnerable_one()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);
        var body = new { policyId = PolicyAlice.Value, jobId = _job.Value, reasonCode = "PRICE" };

        var declined = await PostAsync("/api/chn/v1/portal/renewals/decline", a, body);
        declined.Response.StatusCode.ShouldBe(HttpStatusCode.OK, declined.Text);
        declined.Body!["outcome"]!.GetValue<string>().ShouldBe("DECLINED");
        _renewals.CallsTo("pol.Renewal.decline").Count.ShouldBe(1);

        _vulnerable = true;
        var referred = await PostAsync("/api/chn/v1/portal/renewals/decline", a, body);
        referred.Body!["outcome"]!.GetValue<string>().ShouldBe("REFERRED");
        referred.Body["ticketId"].ShouldNotBeNull();
        _renewals.CallsTo("pol.Renewal.decline").Count.ShouldBe(1);

        // A job of someone else's policy is a 404 even for the own policy id; accept needs the acknowledgement.
        (await PostAsync("/api/chn/v1/portal/renewals/decline", a, new { policyId = PolicyAlice.Value, jobId = Guid.NewGuid(), reasonCode = "PRICE" })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PostAsync("/api/chn/v1/portal/renewals/accept", a, new { policyId = PolicyAlice.Value, jobId = _job.Value, acknowledged = false })).Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var accepted = await PostAsync("/api/chn/v1/portal/renewals/accept", a, new { policyId = PolicyAlice.Value, jobId = _job.Value, acknowledged = true });
        accepted.Response.StatusCode.ShouldBe(HttpStatusCode.OK, accepted.Text);
        accepted.Body!["outcome"]!.GetValue<string>().ShouldBe("ACCEPTED");
        (await PostAsync("/api/chn/v1/portal/renewals/accept", "cust-c-" + Id(), new { policyId = PolicyAlice.Value, jobId = _job.Value, acknowledged = true })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Contact_update_round_trip_with_stale_detection_and_own_party_only()
    {
        await ActivateMatrixAsync();
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);

        var view = await GetAsync("/api/chn/v1/portal/contact", a);
        view.Response.StatusCode.ShouldBe(HttpStatusCode.OK, view.Text);
        var version = view.Body!["recordVersion"]!.GetValue<int>();
        view.Body["contactPoints"]![0]!["contactPointId"]!.GetValue<string>().ShouldBe(_phone.ToString("D"));

        var replace = await PostAsync("/api/chn/v1/portal/contact/update", a, new
        {
            action = "REPLACE", kind = "CONTACT_POINT", locationId = _phone, expectedRecordVersion = version,
            contactPoint = new { type = "MOBILE", value = "+30 698 765 4321", primary = true },
        });
        replace.Response.StatusCode.ShouldBe(HttpStatusCode.OK, replace.Text);
        replace.Body!["outcome"]!.GetValue<string>().ShouldBe("APPLIED");
        replace.Body["locationId"]!.GetValue<string>().ShouldNotBe(_phone.ToString("D"));
        var call = _locations.CallsTo("pty.Location.update").Single();
        call.Arguments[0].ShouldBe(_phone.ToString("D"));
        ((LocationUpdateRequest)call.Arguments[1]!).ExpectedRecordVersion.ShouldBe(version);
        ((LocationUpdateRequest)call.Arguments[1]!).ContactPoint!.Value.ShouldBe("+30 698 765 4321");

        // Another party's location id is a 404 and PTY is never called for it.
        (await PostAsync("/api/chn/v1/portal/contact/update", a, new { action = "REMOVE", kind = "CONTACT_POINT", locationId = Guid.NewGuid(), expectedRecordVersion = version })).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        _locations.CallsTo("pty.Location.remove").Count.ShouldBe(0);

        // A stale version is PTY's refusal, passed through as 409.
        _locations.Fail("pty.Location.update", new DomainException(DomainError.Of(ModuleCode.PTY, "STALE", "The record changed.")));
        var stale = await PostAsync("/api/chn/v1/portal/contact/update", a, new
        {
            action = "REPLACE", kind = "CONTACT_POINT", locationId = _phone, expectedRecordVersion = version - 1,
            contactPoint = new { type = "MOBILE", value = "+30 691 111 2222" },
        });
        stale.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict, stale.Text);

        // Unlinked and staff callers never reach another party's data.
        (await GetAsync("/api/chn/v1/portal/contact", "stranger-" + Id())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

/// <summary>BO-CHN-03 with no matrix in force (a database of its own): every customer command is DENY, 403 CHN-ERR-NOT-PERMITTED, and the owners are never called.</summary>
public sealed class PortalServicingDenyTests(PostgresFixture database) : PortalEngagementHost(database), IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Without_a_matrix_in_force_every_command_is_DENY_403()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, Alice);

        var denied = await PostAsync("/api/chn/v1/changes/preview", a, new { policyId = PolicyAlice.Value, instructions = new[] { new { op = "SET_COVERAGES" } } });
        denied.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, denied.Text);
        denied.Text.ShouldContain("NOT-PERMITTED");
        (await PostAsync("/api/chn/v1/portal/contact/update", a, new { action = "REMOVE", kind = "CONTACT_POINT", locationId = Guid.NewGuid(), expectedRecordVersion = 3 })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync("/api/chn/v1/portal/cancellations/request", a, new { policyId = PolicyAlice.Value, reasonCode = "PRICE" })).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
