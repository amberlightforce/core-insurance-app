using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Claims.Contracts;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Testing.Contracts.Fakes.Billing;
using CoreIns.Testing.Contracts.Fakes.Claims;
using CoreIns.Testing.Contracts.Fakes.Documents;
using CoreIns.Testing.Contracts.Fakes.Party;
using CoreIns.Testing.Contracts.Fakes.Policy;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CoreIns.IntegrationTests.Channels;

/// <summary>In-memory stand-in for DOC's content SPI: the bytes of a few documents.</summary>
internal sealed class FakeDocumentContent : IDocumentContentService
{
    public Dictionary<Guid, DocumentContent> Items { get; } = [];

    public Task<DocumentContent?> ReadAsync(DocumentId documentId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Items.TryGetValue(documentId.Value, out var found) ? found : null);
}

/// <summary>
/// Shared host for the BO-CHN-04 tests. PTY, POL, BIL, DOC and CLM are the generated sandbox doubles, so the test controls who holds and owns
/// what: Alice and Bob share a household, Carol is another customer. Alice's household has policies A and B, Carol has C; all three policies
/// bill to one account paid by Alice, and every policy has documents addressed to various parties.
/// </summary>
public abstract class PortalEngagementHost(PostgresFixture database) : IAsyncDisposable
{
    protected PartyId Alice { get; } = PartyId.New();
    protected PartyId Bob { get; } = PartyId.New();
    protected PartyId Carol { get; } = PartyId.New();
    protected PolicyId PolicyAlice { get; } = PolicyId.New();
    protected PolicyId PolicyBob { get; } = PolicyId.New();
    protected PolicyId PolicyCarol { get; } = PolicyId.New();
    protected BillingAccountId BillingAccount { get; } = BillingAccountId.New();
    protected Guid DocAliceLetter { get; } = Guid.NewGuid();
    protected Guid DocCarolLetterOnAlicePolicy { get; } = Guid.NewGuid();
    protected Guid DocInternal { get; } = Guid.NewGuid();
    protected Guid DocFailed { get; } = Guid.NewGuid();
    protected Guid DocBobLetter { get; } = Guid.NewGuid();
    protected Guid DocCarolLetter { get; } = Guid.NewGuid();
    internal FakeDocumentContent Content { get; } = new();
    private readonly ApiHostFactory _host = new(database.AppConnectionString);
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _host.DisposeAsync();
    }

    protected Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        var accountId = AccountId.New();
        var accounts = new FakePartyAccountService();
        accounts.Setup("pty.Account.search", call =>
        {
            var party = (PartyId?)call.Arguments[2];
            var held = party == Alice || party == Bob;
            return new AccountSearchPage
            {
                Items = held
                    ? [new AccountSearchItem
                    {
                        AccountId = accountId, AccountNumber = AccountNumber.Parse("ACC-1"), AccountType = AccountSearchItem.AccountTypeValue.Household,
                        Status = AccountSearchItem.StatusValue.Active, HolderPartyId = Alice, MemberCount = 1,
                    }]
                    : [],
                NextCursor = null,
                Limit = 100,
            };
        });
        var cannedAccount = accounts.Canned<AccountGetResponse>("pty.Account.get");
        accounts.Setup("pty.Account.get", _ => new AccountGetResponse
        {
            Account = cannedAccount.Account with
            {
                AccountId = accountId, HolderPartyId = Alice,
                Members = [new AccountMemberView { PartyId = Bob, MemberRole = "SPOUSE", AddedOn = new BusinessDate(new DateOnly(2026, 1, 1)) }],
            },
        });

        var policies = new FakePolicyPolicyService();
        var cannedSearch = policies.Canned<PolicySearchPage>("pol.Policy.searchByCriteria");
        var template = cannedSearch.Items.Count > 0 ? cannedSearch.Items[0] : null;
        var end = Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(30));
        var start = Instant.FromUtcDateTime(DateTime.UtcNow.AddDays(-335));
        PolicySearchItem Item(PolicyId id, PartyId holder, string no) => (template ?? new PolicySearchItem
        {
            PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), ProductCode = "MOTOR-GR", InsuredPartyId = holder.Value,
        }) with { PolicyId = id, PolicyNumber = PolicyNumber.Parse(no), InsuredPartyId = holder.Value, Status = TermStateCode.InForce, TermPeriod = InstantRange.Of(start, end) };
        var all = new[] { Item(PolicyAlice, Alice, "POL-A"), Item(PolicyBob, Bob, "POL-B"), Item(PolicyCarol, Carol, "POL-C") };
        policies.Setup("pol.Policy.searchByCriteria", call =>
        {
            var criteria = (PolicySearchCriteria)call.Arguments[0]!;
            return new PolicySearchPage { Items = [.. all.Where(p => p.InsuredPartyId == criteria.InsuredPartyId)], NextCursor = null, Limit = 100 };
        });
        var cannedGet = policies.Canned<PolicyGetResponse>("pol.Policy.get");
        policies.Setup("pol.Policy.get", call =>
        {
            var found = all.SingleOrDefault(p => p.PolicyId.Value.ToString("D") == (string)call.Arguments[0]!)
                ?? throw new CoreIns.Platform.Errors.DomainException(CoreIns.SharedKernel.Results.DomainError.Of(ModuleCode.POL, "NOT-FOUND", "No such policy."));
            return cannedGet with
            {
                Policy = cannedGet.Policy with { PolicyId = found.PolicyId, PolicyNumber = found.PolicyNumber, PolicyholderPartyId = new PartyId(found.InsuredPartyId), Status = TermStateCode.InForce },
            };
        });

        var invoices = new FakeBillingInvoiceService();
        var invoiceTemplate = invoices.Canned<InvoiceListPage>("bil.Invoice.list").Items[0];
        invoices.Setup("bil.Invoice.list", call =>
        {
            var account = (BillingAccountId?)call.Arguments[2];
            var policy = (PolicyId?)call.Arguments[3];
            PolicyId[] ids = policy is { } one ? [one] : [PolicyAlice, PolicyBob, PolicyCarol];
            if (policy is null && account != BillingAccount)
            {
                return new InvoiceListPage { Items = [], NextCursor = null, Limit = 100 };
            }

            return new InvoiceListPage
            {
                Items =
                [
                    .. ids.SelectMany(id => new[]
                    {
                        Invoice(invoiceTemplate, id, 1, InvoiceView.StateValue.Due, 50m, 0m, "INV-DUE-" + PolicyTag(id), new DateOnly(2026, 1, 15)),
                        Invoice(invoiceTemplate, id, 2, InvoiceView.StateValue.Paid, 40m, 40m, "INV-PAID-" + PolicyTag(id), new DateOnly(2025, 6, 1)),
                    }),
                ],
                NextCursor = null,
                Limit = 100,
            };
        });
        var billingAccounts = new FakeBillingBillingAccountService();
        var cannedBillingAccount = billingAccounts.Canned<BillingAccountGetResponse>("bil.BillingAccount.get");
        billingAccounts.Setup("bil.BillingAccount.get", _ => cannedBillingAccount with { Account = cannedBillingAccount.Account with { BillingAccountId = BillingAccount, PayerPartyId = Alice } });
        var refunds = new FakeBillingRefundService();
        var refundTemplate = refunds.Canned<RefundListPage>("bil.Refund.list").Items[0];
        refunds.Setup("bil.Refund.list", call => new RefundListPage
        {
            Items = [refundTemplate with { Refund = refundTemplate.Refund with { BillingAccountId = (BillingAccountId)call.Arguments[2]!, Amount = Money.Of(12.5m, "EUR") } }],
            NextCursor = null,
            Limit = 100,
        });

        var documents = new FakeDocumentsDocumentService();
        documents.Setup("doc.Document.listForObject", call =>
        {
            var objectRef = (string)call.Arguments[3]!;
            DocumentSearchItem[] items = objectRef.EndsWith(PolicyAlice.Value.ToString("D"), StringComparison.Ordinal)
                ? [Doc(DocAliceLetter, "DT-RENEWAL-OFFER", Alice, "Rendered"), Doc(DocCarolLetterOnAlicePolicy, "DT-CANCELLATION-NOTICE", Carol, "Rendered"),
                   Doc(DocInternal, "DT-INTERNAL-UW-NOTE", Alice, "Rendered"), Doc(DocFailed, "DT-REFUND-ADVICE", Alice, "Failed")]
                : objectRef.EndsWith(PolicyBob.Value.ToString("D"), StringComparison.Ordinal)
                    ? [Doc(DocBobLetter, "DT-RENEWAL-OFFER", Bob, "Rendered")]
                    : objectRef.EndsWith(PolicyCarol.Value.ToString("D"), StringComparison.Ordinal) ? [Doc(DocCarolLetter, "DT-RENEWAL-OFFER", Carol, "Rendered")] : [];
            return new DocumentListForObjectPage { Items = items, NextCursor = null, Limit = 200 };
        });
        foreach (var (id, party) in new[] { (DocAliceLetter, Alice), (DocCarolLetterOnAlicePolicy, Carol), (DocInternal, Alice), (DocBobLetter, Bob), (DocCarolLetter, Carol) })
        {
            Content.Items[id] = new DocumentContent(new DocumentId(id), "DOC-" + id.ToString("N")[..6], party, "text/html", System.Text.Encoding.UTF8.GetBytes("<p>letter " + id.ToString("N")[..6] + "</p>"), new string('a', 64));
        }

        var claims = new FakeClaimsClaimService();
        var cannedClaim = claims.Canned<ClaimGetResponse>("clm.Claim.get");
        claims.Setup("clm.Claim.get", _ => cannedClaim with { Claim = cannedClaim.Claim with { Summary = cannedClaim.Claim.Summary with { InsuredPartyId = Alice } } });

        var parties = new FakePartyPartyService();
        var cannedParty = parties.Canned<PartyGetResponse>("pty.Party.get");
        parties.Setup("pty.Party.get", _ => cannedParty);
        _factory = _host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPartyAccountService>();
            services.AddSingleton<IPartyAccountService>(accounts);
            services.RemoveAll<IPolicyPolicyService>();
            services.AddSingleton<IPolicyPolicyService>(policies);
            services.RemoveAll<IPartyPartyService>();
            services.AddSingleton<IPartyPartyService>(parties);
            services.RemoveAll<IBillingInvoiceService>();
            services.AddSingleton<IBillingInvoiceService>(invoices);
            services.RemoveAll<IBillingBillingAccountService>();
            services.AddSingleton<IBillingBillingAccountService>(billingAccounts);
            services.RemoveAll<IBillingRefundService>();
            services.AddSingleton<IBillingRefundService>(refunds);
            services.RemoveAll<IDocumentsDocumentService>();
            services.AddSingleton<IDocumentsDocumentService>(documents);
            services.RemoveAll<IDocumentContentService>();
            services.AddSingleton<IDocumentContentService>(Content);
            services.RemoveAll<IClaimsClaimService>();
            services.AddSingleton<IClaimsClaimService>(claims);
            ConfigureExtra(services, policies);
        }));
        return _factory;
    }

    /// <summary>Lets a subclass replace or add doubles (BO-CHN-03 adds the POL change/renewal and PTY location doubles).</summary>
    protected virtual void ConfigureExtra(IServiceCollection services, FakePolicyPolicyService policies)
    {
    }

    protected static string PolicyTag(PolicyId id) => id.Value.ToString("N")[..6];

    private InvoiceListItem Invoice(InvoiceListItem template, PolicyId policy, byte tag, InvoiceView.StateValue state, decimal total, decimal paid, string number, DateOnly due)
    {
        var bytes = policy.Value.ToByteArray();
        bytes[15] = tag;
        return template with
        {
            Invoice = template.Invoice with
            {
                InvoiceId = new InvoiceId(new Guid(bytes)), BillingAccountId = BillingAccount, InvoiceNumber = InvoiceNumber.Parse(number), PolicyId = policy, State = state,
                Total = Money.Of(total, "EUR"), Paid = Money.Of(paid, "EUR"), Open = Money.Of(total - paid, "EUR"), DueDate = new BusinessDate(due),
            },
        };
    }

    private static DocumentSearchItem Doc(Guid id, string type, PartyId recipient, string status) => new()
    {
        DocumentId = new DocumentId(id), DocumentNumber = "DOC-" + id.ToString("N")[..6], DocumentType = type, RequestNo = "REQ-" + id.ToString("N")[..6], Subject = "POL/Policy/x",
        RecipientPartyId = recipient, Language = "el", Status = status, Medium = "EMAIL", ArchiveId = Guid.NewGuid(), ContentHash = new string('a', 64),
        CreatedAt = "2026-01-02T10:00:00+00:00", Deliveries = [],
    };

    protected static string InvoiceIdOf(PolicyId policy, byte tag)
    {
        var bytes = policy.Value.ToByteArray();
        bytes[15] = tag;
        return new Guid(bytes).ToString("D");
    }

    protected async Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, string roles, string user, object? body = null)
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
        var text = response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json" or null
            ? await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
            : "<binary>";
        return (response, text.Length == 0 || text == "<binary>" ? null : JsonNode.Parse(text), text);
    }

    protected async Task LinkAsync(string subject, PartyId party) =>
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/portal-identities/link", "Staff.Underwriter", "staff-1", new { subject, partyId = party.Value, verification = "ID checked at the counter (test)" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.OK);

    protected Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> GetAsync(string path, string user, string roles = "Customer.Portal") => SendAsync(HttpMethod.Get, path, roles, user);

    protected Task<(HttpResponseMessage Response, JsonNode? Body, string Text)> PostAsync(string path, string user, object? body = null, string roles = "Customer.Portal") =>
        SendAsync(HttpMethod.Post, path, roles, user, body ?? new { });

    /// <summary>Puts a matrix in force that the baseline of the legal entity gives (customers may pay and download on the web).</summary>
    protected async Task ActivateMatrixAsync()
    {
        var created = await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/create-draft", "Staff.Underwriter", "maker-1", new { scenarios = Array.Empty<object>() });
        created.Response.StatusCode.ShouldBe(HttpStatusCode.OK, created.Text);
        var id = created.Body!["version"]!["matrix"]!["matrixId"]!.GetValue<string>();
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/submit", "Staff.Underwriter", "maker-1", new { matrixId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(HttpMethod.Post, "/api/chn/v1/permission-matrices/schedule", "Staff.UnderwritingManager", "checker-1", new { matrixId = id })).Response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    protected static string Id() => Guid.NewGuid().ToString("N")[..8];
}
