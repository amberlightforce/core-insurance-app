using System.Net;
using CoreIns.Modules.Party.Contracts;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Party;

/// <summary>Minimal intermediary and producer code (W2-PTY-04 subset) over HTTP and through the in-process contracts.</summary>
public sealed class IntermediaryApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private ApiHostFactory _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new ApiHostFactory(database.AppConnectionString);
        _client = _factory.CreateClient();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task An_intermediary_gets_a_producer_code_that_validates_only_once_active()
    {
        var (createdParty, party) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/parties", new
        {
            partyType = "ORGANISATION",
            organisation = new { legalName = "Ασφαλιστική Πράκτορες Αιγαίου Ο.Ε.", tradeName = "Αιγαίο" },
            identifiers = new[] { new { scheme = "AFM", value = "094014201" } },
        });
        createdParty.StatusCode.ShouldBe(HttpStatusCode.Created, party?.ToJsonString());
        party.Text("party.names.1.organisationName").ShouldBe("Asfalistiki Praktores Aigaiou O.E.");

        var (created, body) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/intermediaries", new
        {
            partyId = party.Text("party.partyId"),
            intermediaryType = "INSURANCE_AGENT",
            register = new
            {
                registerName = "Special register (synthetic)", chamber = "Chamber (synthetic)", registerNumber = "SYN-0001",
                registrationCategory = "AGENT", registrationDate = "2020-01-15", registerStatus = "ACTIVE",
            },
            authorities = new { collectPremium = true, issueCoverNotes = false, bindWithinAuthority = true, serviceOnly = false },
            validFrom = "2026-01-01",
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("intermediary.status").ShouldBe("ONBOARDING");
        var code = body.Text("intermediary.producerCode");
        code.ShouldMatch("^PC[0-9]{6}$");
        var id = body.Text("intermediary.intermediaryId");

        var (validation, invalid) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-codes/validate?validAt=2026-10-07",
            new { producerCode = code, product = "MOTOR_PRIVATE_CAR", transactionType = "NEW_BUSINESS" }, withKey: false);
        validation.StatusCode.ShouldBe(HttpStatusCode.OK, invalid?.ToJsonString());
        invalid.Text("valid").ShouldBe("false");
        invalid.Text("reasons.0").ShouldBe("INTERMEDIARY_NOT_ACTIVE");

        var (stale, staleBody) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 7 });
        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        staleBody.Text("code").ShouldBe("PTY-ERR-STALE");

        var (activated, active) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 1 });
        activated.StatusCode.ShouldBe(HttpStatusCode.OK, active?.ToJsonString());
        active.Text("intermediary.status").ShouldBe("ACTIVE");
        active.Text("intermediary.recordVersion").ShouldBe("2");

        var (again, againBody) = await SendAsync(_client, HttpMethod.Patch, $"/api/pty/v1/intermediaries/{id}", new { status = "ACTIVE", expectedRecordVersion = 2 });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        againBody.Text("code").ShouldBe("PTY-ERR-INVALID-STATE-TRANSITION");

        // In-process contract (what POL's bind gate calls), before the code's validity and on a valid date.
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
            context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
            context.Jurisdiction = Jurisdiction.Parse("GR");
            var service = scope.ServiceProvider.GetRequiredService<IPartyProducerCodeService>();
            var request = new ProducerCodeValidateRequest { ProducerCode = code, Product = "MOTOR_PRIVATE_CAR", TransactionType = "NEW_BUSINESS" };
            var valid = await service.ValidateAsync(request, ValidAt.From(new CoreIns.SharedKernel.BusinessDate(2026, 10, 7)), TestContext.Current.CancellationToken);
            valid.Valid.ShouldBeTrue();
            valid.RegisterNumber.ShouldBe("SYN-0001");
            valid.Authorities!.BindWithinAuthority.ShouldBe(true);
            var early = await service.ValidateAsync(request, ValidAt.From(new CoreIns.SharedKernel.BusinessDate(2025, 12, 31)), TestContext.Current.CancellationToken);
            early.Reasons.ShouldBe(["CODE_NOT_ACTIVE"]);
        }

        var (search, page) = await SendAsync(_client, HttpMethod.Post, "/api/pty/v1/producer-codes/search", new { name = "Αιγαιου" }, withKey: false);
        search.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Text("items.0.producerCode").ShouldBe(code);
        page.Text("items.0.intermediaryStatus").ShouldBe("ACTIVE");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        await using var events = dataSource.CreateCommand($"SELECT string_agg(event_type, ',' ORDER BY aggregate_sequence) FROM plt.outbox_message WHERE aggregate_id = '{id}'");
        ((string)(await events.ExecuteScalarAsync(TestContext.Current.CancellationToken))!).ShouldBe("ProducerCodeChanged,IntermediaryStatusChanged");
    }

    [Fact]
    public async Task The_in_process_party_contract_creates_with_the_callers_key_and_reads_back()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("in-process-test");
        context.Roles = ["Staff.Underwriter"];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var parties = scope.ServiceProvider.GetRequiredService<IPartyPartyService>();
        var options = CommandOptions.New();
        var request = new PartyCreateRequest
        {
            PartyType = PartyType.Person,
            Person = new PersonInput { GivenNames = "Ιωάννης", FamilyName = "Μπακογιάννης", BirthDate = new CoreIns.SharedKernel.BusinessDate(1990, 2, 28) },
        };

        var created = await parties.CreateAsync(request, options, TestContext.Current.CancellationToken);
        var replay = await parties.CreateAsync(request, options, TestContext.Current.CancellationToken);
        replay.Party.PartyId.ShouldBe(created.Party.PartyId);

        var read = await parties.GetAsync(created.Party.PartyId.Value.ToString(), revealPurpose: "RATING", cancellationToken: TestContext.Current.CancellationToken);
        read.Party.BirthDate.ShouldBe(new CoreIns.SharedKernel.BusinessDate(1990, 2, 28));
        var found = await parties.SearchByCriteriaAsync(new PartySearchCriteria { Name = "Bakogiannis" }, cancellationToken: TestContext.Current.CancellationToken);
        found.Items.Select(i => i.PartyId).ShouldContain(created.Party.PartyId);
        await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(() =>
            parties.UpdateAsync(created.Party.PartyId.Value.ToString(), new PartyUpdateRequest(), CommandOptions.New(), TestContext.Current.CancellationToken));
    }
}
