using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Billing;
using CoreIns.Testing.Contracts.Fakes.Party;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>
/// BO-CHN-02: the portal read API (me, dashboard, policies, policy detail). PTY, POL and BIL are the generated sandbox doubles, so the test
/// controls who holds and owns what. Covers scope, 404-not-403 for foreign policies, field minimisation and paging.
/// </summary>
public sealed class PortalReadApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly PartyId _alice = PartyId.New();
    private readonly PartyId _bob = PartyId.New();   // member of Alice's household
    private readonly PartyId _carol = PartyId.New(); // another customer
    private readonly PolicyId _policyAlice = PolicyId.New();
    private readonly PolicyId _policyBob = PolicyId.New();
    private readonly PolicyId _policyCarol = PolicyId.New();
    private readonly FakePartyAccountService _accounts = new();
    private readonly FakePolicyPolicyService _policies = new();
    private readonly FakeBillingInvoiceService _invoices = new();
    private readonly FakePolicyJobService _jobs = new();
    private readonly Guid _renewalJob = Guid.NewGuid();
    private readonly ApiHostFactory _host = new(database.AppConnectionString);
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _host.DisposeAsync();
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        var accountId = AccountId.New();
        _accounts.Setup("pty.Account.search", call =>
        {
            var party = (PartyId?)call.Arguments[2];
            var held = party == _alice || party == _bob;
            return new AccountSearchPage
            {
                Items = held
                    ? [new AccountSearchItem
                    {
                        AccountId = accountId, AccountNumber = AccountNumber.Parse("ACC-1"), AccountType = AccountSearchItem.AccountTypeValue.Household,
                        Status = AccountSearchItem.StatusValue.Active, HolderPartyId = _alice, MemberCount = 1,
                    }]
                    : [],
                NextCursor = null,
                Limit = 100,
            };
        });
        var canned = _accounts.Canned<AccountGetResponse>("pty.Account.get");
        _accounts.Setup("pty.Account.get", _ => new AccountGetResponse
        {
            Account = canned.Account with
            {
                AccountId = accountId,
                HolderPartyId = _alice,
                Members = [new AccountMemberView { PartyId = _bob, MemberRole = "SPOUSE", AddedOn = new BusinessDate(new DateOnly(2026, 1, 1)) }],
            },
        });

        var cannedSearch = _policies.Canned<PolicySearchPage>("pol.Policy.searchByCriteria");
        var template = cannedSearch.Items.Count > 0 ? cannedSearch.Items[0] : null;
        var end = Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(30));
        var start = Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(-335));
        PolicySearchItem Item(PolicyId id, PartyId holder, string no) => (template ?? new PolicySearchItem
        {
            PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), ProductCode = "MOTOR-GR", InsuredPartyId = holder.Value,
        }) with { PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), InsuredPartyId = holder.Value, Status = TermStateCode.InForce, TermPeriod = InstantRange.Of(start, end) };
        var all = new[] { Item(_policyAlice, _alice, "POL-A"), Item(_policyBob, _bob, "POL-B"), Item(_policyCarol, _carol, "POL-C") };
        _policies.Setup("pol.Policy.searchByCriteria", call =>
        {
            var criteria = (PolicySearchCriteria)call.Arguments[0]!;
            return new PolicySearchPage { Items = [.. all.Where(p => p.InsuredPartyId == criteria.InsuredPartyId)], NextCursor = null, Limit = 100 };
        });
        var cannedGet = _policies.Canned<PolicyGetResponse>("pol.Policy.get");
        _policies.Setup("pol.Policy.get", call =>
        {
            var found = all.SingleOrDefault(p => p.PolicyId.Value.ToString("D") == (string)call.Arguments[0]!)
                ?? throw new CoreIns.Platform.Errors.DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.POL, "NOT-FOUND", "No such policy."));
            return cannedGet with
            {
                Policy = cannedGet.Policy with { PolicyId = found.PolicyId, PolicyNumber = found.PolicyNumber, PolicyholderPartyId = new PartyId(found.InsuredPartyId), Status = TermStateCode.InForce },
                RiskTree = new RiskTree
                {
                    Vehicles = [new Vehicle { Plate = "IKA1234", Make = "Toyota", Model = "Yaris", Vin = "VIN-SECRET-123", Value = Money.Of(9999m, "EUR") }],
                    Drivers = [],
                    Coverages = [new CoverageSelection { CoverageCode = "MTPL", Selected = true }],
                    QuestionSets = [],
                },
                Charges =
                [
                    new ChargeLine { ElementLocator = "x", CoverageCode = "MTPL", ChargeType = "PREMIUM", ChargeCategory = "NET", AnnualRate = 0.42m, Amount = Money.Of(100m, "EUR"), TreatmentRuleId = "RULE-INTERNAL-77" },
                    new ChargeLine { ElementLocator = "x", CoverageCode = "MTPL", ChargeType = "PREMIUM", ChargeCategory = "NET", AnnualRate = 0.1m, Amount = Money.Of(20m, "EUR") },
                    new ChargeLine { ElementLocator = "x", CoverageCode = "MTPL", ChargeType = "LEVY", ChargeCategory = "TAX", AnnualRate = 0.1m, Amount = Money.Of(12m, "EUR") },
                ],
            };
        });

        var cannedInvoices = _invoices.Canned<InvoiceListPage>("bil.Invoice.list");
        var invoiceTemplate = cannedInvoices.Items[0];
        var billingAccount = BillingAccountId.New();
        static Guid Derive(PolicyId policy, byte tag) { var bytes = policy.Value.ToByteArray(); bytes[15] = tag; return new Guid(bytes); }
        _invoices.Setup("bil.Invoice.list", call =>
        {
            // Discovery asks per policy; the portal then lists once per billing account (all three policies share one account).
            var account = (BillingAccountId?)call.Arguments[2];
            var policy = (PolicyId?)call.Arguments[3];
            PolicyId[] policyIds = policy is { } one ? [one] : [_policyAlice, _policyBob, _policyCarol];
            if (policy is null && account != billingAccount)
            {
                return new InvoiceListPage { Items = [], NextCursor = null, Limit = 100 };
            }

            return new InvoiceListPage
            {
                Items =
                [
                    .. policyIds.SelectMany(id => new[]
                    {
                        invoiceTemplate with
                        {
                            Invoice = invoiceTemplate.Invoice with
                            {
                                InvoiceId = new InvoiceId(Derive(id, 1)), BillingAccountId = billingAccount, PolicyId = id, State = InvoiceView.StateValue.Due,
                                Total = Money.Of(50m, "EUR"), Paid = Money.Of(0m, "EUR"), Open = Money.Of(50m, "EUR"), DueDate = new BusinessDate(new DateOnly(2026, 1, 15)),
                            },
                        },
                        invoiceTemplate with
                        {
                            Invoice = invoiceTemplate.Invoice with
                            {
                                InvoiceId = new InvoiceId(Derive(id, 2)), BillingAccountId = billingAccount, PolicyId = id, State = InvoiceView.StateValue.Paid,
                                Total = Money.Of(40m, "EUR"), Paid = Money.Of(40m, "EUR"), Open = Money.Of(0m, "EUR"),
                            },
                        },
                    }),
                ],
                NextCursor = null,
                Limit = 100,
            };
        });
        var billingAccounts = new FakeBillingBillingAccountService();
        var cannedAccount = billingAccounts.Canned<BillingAccountGetResponse>("bil.BillingAccount.get");
        billingAccounts.Setup("bil.BillingAccount.get", _ => cannedAccount with { Account = cannedAccount.Account with { BillingAccountId = billingAccount, PayerPartyId = _alice } });

        // Alice's own policy has an offered renewal job; Bob's (household) policy has none yet.
        _jobs.Setup("pol.Job.list", call =>
        {
            var offered = call.Arguments[3] as string == _policyAlice.Value.ToString("D") && call.Arguments[5] as string == "QUOTED";
            return new JobListPage
            {
                Items = offered ? [new JobListItem { Jobs = System.Text.Json.JsonSerializer.SerializeToElement(new { jobId = _renewalJob, jobType = "RENEWAL", state = "QUOTED" }) }] : [],
                NextCursor = null,
                Limit = 50,
            };
        });
        var parties = new FakePartyPartyService();
        var cannedParty = parties.Canned<PartyGetResponse>("pty.Party.get");
        parties.Setup("pty.Party.get", _ => cannedParty);
        _factory = _host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPartyAccountService>();
            services.AddSingleton<IPartyAccountService>(_accounts);
            services.RemoveAll<IPolicyPolicyService>();
            services.AddSingleton<IPolicyPolicyService>(_policies);
            services.RemoveAll<IPartyPartyService>();
            services.AddSingleton<IPartyPartyService>(parties);
            services.RemoveAll<IBillingInvoiceService>();
            services.AddSingleton<IBillingInvoiceService>(_invoices);
            services.RemoveAll<IPolicyJobService>();
            services.AddSingleton<IPolicyJobService>(_jobs);
            services.RemoveAll<IBillingBillingAccountService>();
            services.AddSingleton<IBillingBillingAccountService>(billingAccounts);
        }));
        return _factory;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, string roles, string user, object? body = null)
    {
        _client ??= Factory().CreateClient();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(TestAuthHandler.RolesHeader, roles);
        request.Headers.Add(TestAuthHandler.UserHeader, user);
        if (method != HttpMethod.Get)
        {
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response, text.Length == 0 ? null : JsonNode.Parse(text), text);
    }

    private async Task LinkAsync(string subject, PartyId party) =>
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/portal-identities/link", "Staff.Underwriter", "staff-1", new { subject, partyId = party.Value, verification = "ID checked at the counter (test)" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

    private Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> GetAsync(string path, string user, string roles = "Customer.Portal") =>
        SendAsync(HttpMethod.Get, path, roles, user);

    private static string Id() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Owner_sees_own_and_household_policies_and_me_lists_scope_ids()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, _alice);

        var me = await GetAsync("/api/chn/v1/portal/me", a);
        me.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        me.Body!["partyId"]!.GetValue<string>().ShouldBe(_alice.Value.ToString("D"));
        me.Body["householdSize"]!.GetValue<int>().ShouldBe(2);
        me.Body["accountIds"]!.AsArray().Count.ShouldBe(1);

        var list = await GetAsync("/api/chn/v1/portal/policies", a);
        list.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = list.Body!["items"]!.AsArray();
        items.Select(i => i!["policyId"]!.GetValue<string>()).ShouldBe(new[] { _policyBob.Value.ToString("D"), _policyAlice.Value.ToString("D") }, ignoreOrder: true);
        items.Single(i => i!["policyId"]!.GetValue<string>() == _policyAlice.Value.ToString("D"))!["ownership"]!.GetValue<string>().ShouldBe("OWN");
        items.Single(i => i!["policyId"]!.GetValue<string>() == _policyBob.Value.ToString("D"))!["ownership"]!.GetValue<string>().ShouldBe("HOUSEHOLD");
        list.Text.ShouldNotContain(_policyCarol.Value.ToString("D"));
    }

    [Fact]
    public async Task Policy_list_pages_with_a_cursor()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, _alice);

        var first = await GetAsync("/api/chn/v1/portal/policies?limit=1", a);
        first.Body!["items"]!.AsArray().Count.ShouldBe(1);
        var cursor = first.Body["nextCursor"]!.GetValue<string>();
        var second = await GetAsync($"/api/chn/v1/portal/policies?limit=1&cursor={cursor}", a);
        second.Body!["items"]!.AsArray().Count.ShouldBe(1);
        second.Body["nextCursor"].ShouldBeNull();
        second.Body["items"]![0]!["policyId"]!.GetValue<string>().ShouldNotBe(first.Body["items"]![0]!["policyId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Another_customer_gets_404_for_the_same_policy_ids_and_never_sees_them_listed()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        await LinkAsync(a, _alice);
        await LinkAsync(c, _carol);

        (await GetAsync($"/api/chn/v1/portal/policies/{_policyAlice.Value:D}", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/policies/{_policyBob.Value:D}", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/policies/{PolicyId.New().Value:D}", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync("/api/chn/v1/portal/policies/not-a-guid", c)).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await GetAsync($"/api/chn/v1/portal/policies/{_policyAlice.Value:D}", a)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync($"/api/chn/v1/portal/policies/{_policyBob.Value:D}", a)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cList = await GetAsync("/api/chn/v1/portal/policies", c);
        cList.Body!["items"]!.AsArray().Select(i => i!["policyId"]!.GetValue<string>()).ShouldBe([_policyCarol.Value.ToString("D")]);
    }

    [Fact]
    public async Task Policy_detail_exposes_customer_fields_only()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, _alice);

        var detail = await GetAsync($"/api/chn/v1/portal/policies/{_policyAlice.Value:D}", a);
        detail.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        detail.Body!["policyNumber"]!.GetValue<string>().ShouldBe("POL-A");
        detail.Body["status"]!.GetValue<string>().ShouldBe("InForce");
        detail.Body["renewalJobId"]!.GetValue<string>().ShouldBe(_renewalJob.ToString("D"));
        detail.Body["coverages"]!.AsArray()[0]!["coverageCode"]!.GetValue<string>().ShouldBe("MTPL");
        var vehicle = detail.Body["vehicles"]!.AsArray()[0]!;
        vehicle["plate"]!.GetValue<string>().ShouldBe("IKA1234");
        var premium = detail.Body["premium"]!.AsArray();
        premium.Count.ShouldBe(2);
        premium.Single(p => p!["category"]!.GetValue<string>() == "NET")!["amount"]!["amount"]!.GetValue<string>().ShouldBe("120");

        // Field minimisation: no VIN or vehicle value, no rates or rule ids, no raw risk tree, no transactions, no party ids.
        foreach (var forbidden in new[] { "VIN-SECRET-123", "9999", "RULE-INTERNAL-77", "annualRate", "treatment", "riskTree", "transactions", "policyholderPartyId", "recordedAt", "artefactHash", "producerCode" })
        {
            detail.Text.ShouldNotContain(forbidden);
        }
    }

    [Fact]
    public async Task Dashboard_shows_renewals_due_and_unpaid_invoices_for_the_scope_only()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, _alice);

        var dash = await GetAsync("/api/chn/v1/portal/dashboard", a);
        dash.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        dash.Body!["policyCount"]!.GetValue<int>().ShouldBe(2);
        dash.Body["activePolicyCount"]!.GetValue<int>().ShouldBe(2);
        dash.Body["renewalsDue"]!.AsArray().Count.ShouldBe(2);
        var due = dash.Body["renewalsDue"]!.AsArray()[0]!["daysLeft"]!.GetValue<int>();
        due.ShouldBeInRange(29, 31);
        // The renewal job id is exposed only for the policy that has an offered renewal.
        var renewals = dash.Body["renewalsDue"]!.AsArray();
        renewals.Count(r => r!["renewalJobId"] is not null).ShouldBe(1);
        renewals.Single(r => r!["policyId"]!.GetValue<string>() == _policyAlice.Value.ToString("D"))!["renewalJobId"]!.GetValue<string>().ShouldBe(_renewalJob.ToString("D"));
        var unpaid = dash.Body["unpaidInvoices"]!.AsArray();
        unpaid.Count.ShouldBe(2, "one open invoice per policy in scope; the paid one is left out");
        unpaid[0]!["overdue"]!.GetValue<bool>().ShouldBeTrue();
        dash.Text.ShouldNotContain(_policyCarol.Value.ToString("D"));
    }

    [Fact]
    public async Task Unlinked_subject_and_staff_get_nothing_from_the_portal_endpoints()
    {
        var a = "cust-a-" + Id();
        await LinkAsync(a, _alice);
        var paths = new[]
        {
            "/api/chn/v1/portal/me", "/api/chn/v1/portal/dashboard", "/api/chn/v1/portal/policies", $"/api/chn/v1/portal/policies/{_policyAlice.Value:D}",
        };
        foreach (var path in paths)
        {
            // Portal role but no live link: 404.
            (await GetAsync(path, "stranger-" + Id())).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);

            // Staff persona (even carrying a linked subject name): not granted the portal operations, so 403, never data.
            var staff = await GetAsync(path, a, "Staff.Underwriter");
            ((int)staff.Response.StatusCode).ShouldBeOneOf(403, 404);
            staff.Text.ShouldNotContain("POL-A");

            // Staff that also carry the portal role still get 404 (staff are governed by permissions, not ownership).
            (await GetAsync(path, a, "Staff.Underwriter,Customer.Portal")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound, path);
        }
    }
}
