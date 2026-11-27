using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Channels.Services;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Party;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>
/// BO-CHN-01: customer identity link (staff link/unlink over HTTP, audited, fail closed) and the CHN scope service. PTY accounts, PTY party
/// and POL policies are the generated sandbox doubles, so the test controls who holds, belongs to and owns what.
/// </summary>
public sealed class PortalScopeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncDisposable
{
    private readonly PartyId _alice = PartyId.New();
    private readonly PartyId _bob = PartyId.New();   // member of Alice's household
    private readonly PartyId _carol = PartyId.New(); // another customer
    private readonly PolicyId _policyAlice = PolicyId.New();
    private readonly PolicyId _policyBob = PolicyId.New();
    private readonly PolicyId _policyCarol = PolicyId.New();
    private readonly FakePartyAccountService _accounts = new();
    private readonly FakePolicyPolicyService _policies = new();
    private readonly ApiHostFactory _host = new(database.AppConnectionString);
    private readonly PartyId _missingParty = PartyId.New();
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
        var number = AccountNumber.Parse("ACC-1");
        _accounts.Setup("pty.Account.search", call =>
        {
            var party = (PartyId?)call.Arguments[2];
            var held = party == _alice || party == _bob;
            return new AccountSearchPage
            {
                Items = held
                    ? [new AccountSearchItem
                    {
                        AccountId = accountId, AccountNumber = number, AccountType = AccountSearchItem.AccountTypeValue.Household, Status = AccountSearchItem.StatusValue.Active,
                        HolderPartyId = _alice, MemberCount = 1,
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
        var item = cannedSearch.Items.Count > 0 ? cannedSearch.Items[0] : null;
        PolicySearchItem Item(PolicyId id, PartyId holder, string no) => (item ?? new PolicySearchItem
        {
            PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), ProductCode = "MOTOR-GR", InsuredPartyId = holder.Value,
        }) with { PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), InsuredPartyId = holder.Value };
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
            return cannedGet with { Policy = cannedGet.Policy with { PolicyId = found.PolicyId, PolicyholderPartyId = new PartyId(found.InsuredPartyId) } };
        });

        var parties = new FakePartyPartyService();
        var cannedParty = parties.Canned<PartyGetResponse>("pty.Party.get");
        parties.Setup("pty.Party.get", call => (string)call.Arguments[0]! == _missingParty.Value.ToString("D")
            ? throw new CoreIns.Platform.Errors.DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "No such party."))
            : cannedParty);
        _factory = _host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPartyAccountService>();
            services.AddSingleton<IPartyAccountService>(_accounts);
            services.RemoveAll<IPolicyPolicyService>();
            services.AddSingleton<IPolicyPolicyService>(_policies);
            services.RemoveAll<IPartyPartyService>();
            services.AddSingleton<IPartyPartyService>(parties);
        }));
        return _factory;
    }

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string path, string roles, string user, object? body = null)
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
        return (response, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    private Task<(HttpResponseMessage Response, JsonNode? Body)> LinkAsync(string subject, PartyId party, string roles = "Staff.Underwriter", string staff = "staff-1") =>
        SendAsync(HttpMethod.Post, "/api/chn/v1/portal-identities/link", roles, staff, new { subject, partyId = party.Value, verification = "ID checked at the counter (test)" });

    private AsyncServiceScope CustomerScope(string subject, string roles = "Customer.Portal")
    {
        var scope = Factory().Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User(subject);
        context.Roles = [.. roles.Split(',')];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        return scope;
    }

    private static string Id() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task A_customer_sees_own_and_household_policies_only_and_another_customer_gets_nothing()
    {
        var a = "cust-a-" + Id();
        var c = "cust-c-" + Id();
        (await LinkAsync(a, _alice)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkAsync(c, _carol)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scopeA = CustomerScope(a))
        {
            var scope = scopeA.ServiceProvider.GetRequiredService<PortalScope>();
            var mine = await scope.MyPoliciesAsync(TestContext.Current.CancellationToken);
            mine.Select(p => p.PolicyId).ShouldBe([_policyAlice, _policyBob], ignoreOrder: true);
            (await scope.OwnsPolicyAsync(_policyAlice, TestContext.Current.CancellationToken)).ShouldBeTrue();
            (await scope.OwnsPolicyAsync(_policyBob, TestContext.Current.CancellationToken)).ShouldBeTrue("a household member's policy is in scope");
            (await scope.OwnsPolicyAsync(_policyCarol, TestContext.Current.CancellationToken)).ShouldBeFalse("another customer's policy is not found");
            (await scope.OwnsPolicyAsync(PolicyId.New(), TestContext.Current.CancellationToken)).ShouldBeFalse();
        }

        await using var scopeC = CustomerScope(c);
        var scopeCService = scopeC.ServiceProvider.GetRequiredService<PortalScope>();
        (await scopeCService.MyPoliciesAsync(TestContext.Current.CancellationToken)).Select(p => p.PolicyId).ShouldBe([_policyCarol]);
        (await scopeCService.OwnsPolicyAsync(_policyAlice, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task An_unlinked_identity_a_stranger_and_staff_have_no_portal_scope()
    {
        var a = "cust-a-" + Id();
        (await LinkAsync(a, PartyId.New())).Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var stranger = CustomerScope("nobody-" + Id()))
        {
            var scope = stranger.ServiceProvider.GetRequiredService<PortalScope>();
            (await scope.ResolveAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
            (await scope.MyPoliciesAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
        }

        // A staff member is never a portal customer, even with a link row of the same subject (staff are governed by permissions).
        await using (var staff = CustomerScope(a, "Staff.Underwriter,Customer.Portal"))
        {
            (await staff.ServiceProvider.GetRequiredService<PortalScope>().ResolveAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
            PortalScope.OwnerFilter(staff.ServiceProvider.GetRequiredService<RequestContext>()).ShouldBeNull();
        }

        // The portal role without a staff role is restricted to the actor's own records.
        await using var customer = CustomerScope(a);
        PortalScope.OwnerFilter(customer.ServiceProvider.GetRequiredService<RequestContext>()).ShouldBe($"USER:{a}");

        (await SendAsync(HttpMethod.Post, "/api/chn/v1/portal-identities/unlink", "Staff.Underwriter", "staff-1", new { subject = a, reason = "customer left" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var after = CustomerScope(a);
        (await after.ServiceProvider.GetRequiredService<PortalScope>().ResolveAsync(TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Linking_is_staff_only_one_to_one_not_self_service_and_relink_needs_an_unlink()
    {
        var a = "cust-a-" + Id();
        var b = "cust-b-" + Id();

        // A customer cannot link themselves, and staff cannot link their own sign-in.
        (await LinkAsync(a, _alice, roles: "Customer.Portal", staff: a)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var self = await LinkAsync("staff-self", _alice, staff: "staff-self");
        self.Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        self.Body!.ToJsonString().ShouldContain("SELF-LINK");

        // An unknown party fails closed.
        var unknown = await LinkAsync(a, _missingParty);
        unknown.Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await LinkAsync(a, _alice)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LinkAsync(a, _alice)).Response.StatusCode.ShouldBe(HttpStatusCode.OK); // same link again: no-op

        // One identity, one party; one party, one identity.
        var other = await LinkAsync(a, _carol);
        other.Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        other.Body!.ToJsonString().ShouldContain("IDENTITY-LINKED");
        (await LinkAsync(b, _alice)).Response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Unlink, then relink elsewhere: the history stays (audited), the live link moves.
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/portal-identities/unlink", "Staff.Billing", "staff-2", new { subject = a, reason = "wrong person" })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Get, $"/api/chn/v1/portal-identities/{a}", "Staff.Underwriter", "staff-1")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await LinkAsync(a, _carol)).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var got = await SendAsync(HttpMethod.Get, $"/api/chn/v1/portal-identities/{a}", "Staff.Underwriter", "staff-1");
        got.Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        got.Body!["identity"]!["partyId"]!.GetValue<string>().ShouldBe(_carol.Value.ToString("D"));

        // A customer cannot read or manage links either.
        (await SendAsync(HttpMethod.Get, $"/api/chn/v1/portal-identities/{a}", "Customer.Portal", a)).Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
