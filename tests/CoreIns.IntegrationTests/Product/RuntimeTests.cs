using System.Net;
using System.Text.Json.Nodes;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>BO-PFC-05: availability, describe and policy-draft validation over HTTP, and keyset paging of the product list.</summary>
public sealed class RuntimeTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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

    private Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, object body, string roles = Staff) =>
        SendAsync(_client, HttpMethod.Post, path, body, roles, withKey: false);

    private static bool Offers(JsonNode? body, string product) =>
        body!["offerings"]!.AsArray().Any(o => o!["product"]!.GetValue<string>() == product);

    private static List<string> Rules(JsonNode? body) =>
        [.. body!["errors"]!.AsArray().Select(e => e!["rule"]!.GetValue<string>())];

    [Fact]
    public async Task REQ_PFC_011_availability_lists_only_published_versions_whose_window_covers_the_date_on_the_channel()
    {
        await ImportAsync(_client, Seed("MOTOR-R1"));
        var abstractBase = Seed("MOTOR-R1A");
        abstractBase["isAbstract"] = true;
        await ImportAsync(_client, abstractBase);

        var (ok, body) = await PostAsync("/api/pfc/v1/availability/check?validAt=2026-10-07", new { channel = "WEB_DIRECT", product = "MOTOR-R1" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("available").ShouldBe("true");
        body.Text("offerings.0.product").ShouldBe("MOTOR-R1");
        body.Text("offerings.0.version").ShouldBe("1.0");
        body.Text("offerings.0.artefactHash").ShouldMatch("^[0-9a-f]{64}$");

        // Before the window opens, on a channel the version is not sold on, and for an abstract base: nothing.
        var (before, beforeBody) = await PostAsync("/api/pfc/v1/availability/check?validAt=2025-12-31", new { channel = "WEB_DIRECT", product = "MOTOR-R1" });
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        beforeBody.Text("available").ShouldBe("false");
        beforeBody.Text("reason").ShouldBe("NO-VERSION-IN-WINDOW");
        var (_, wrongChannel) = await PostAsync("/api/pfc/v1/availability/check?validAt=2026-10-07", new { channel = "BANK_BRANCH", product = "MOTOR-R1" });
        wrongChannel.Text("available").ShouldBe("false");
        var (_, all) = await PostAsync("/api/pfc/v1/availability/check?validAt=2026-10-07", new { channel = "STAFF", transactionType = "Renewal" });
        Offers(all, "MOTOR-R1").ShouldBeTrue();
        Offers(all, "MOTOR-R1A").ShouldBeFalse();
        var (_, otherType) = await PostAsync("/api/pfc/v1/availability/check?validAt=2026-10-07", new { channel = "STAFF", product = "MOTOR-R1", customerType = "NO-SUCH-TYPE" });
        otherType.Text("available").ShouldBe("false");
    }

    [Fact]
    public async Task REQ_PFC_222_describe_returns_the_published_version_in_force_by_product_or_by_hash_and_refuses_when_none_is()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-R2"));
        var hash = import.Text("artefactHash");

        var (ok, body) = await PostAsync("/api/pfc/v1/products/describe?validAt=2026-10-07", new { product = "MOTOR-R2", channel = "WEB_DIRECT" });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("product").ShouldBe("MOTOR-R2");
        body.Text("artefactHash").ShouldBe(hash);
        body.Text("version").ShouldBe("1.0");
        body.Text("name.en").ShouldNotBeNullOrEmpty();
        body.Text("name.el").ShouldNotBeNullOrEmpty();
        body!["coverages"]!.AsArray().Count.ShouldBe(3);
        body["elements"]!.AsArray().Count.ShouldBe(3);
        body["orderedSteps"]!.AsArray().Count.ShouldBe(3);
        body.Text("windows.newBusiness.from").ShouldBe("2026-01-01");

        var (byHash, hashBody) = await PostAsync("/api/pfc/v1/products/describe", new { hash });
        byHash.StatusCode.ShouldBe(HttpStatusCode.OK, hashBody?.ToJsonString());
        hashBody.Text("product").ShouldBe("MOTOR-R2");

        var (before, beforeBody) = await PostAsync("/api/pfc/v1/products/describe?validAt=2025-12-31", new { product = "MOTOR-R2", channel = "WEB_DIRECT" });
        before.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        beforeBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");
        var (unknownHash, _) = await PostAsync("/api/pfc/v1/products/describe", new { hash = new string('a', 64) });
        unknownHash.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    private static object Vehicle(string fuel = "PETROL") => new
    {
        element = "vehicle",
        id = "v1",
        parent = "pl",
        fields = new Dictionary<string, string>
        {
            ["registrationNumber"] = "ABC1234",
            ["make"] = "Toyota",
            ["model"] = "Yaris",
            ["firstRegistrationYear"] = "2020",
            ["engineCapacityCc"] = "1500",
            ["powerKw"] = "74",
            ["fuelType"] = fuel,
            ["usage"] = "PRIVATE",
            ["garagingPostcode"] = "11527",
            ["ownerType"] = "PERSON",
        },
    };

    private static object Mtpl() =>
        new { coverage = "MTPL", terms = new Dictionary<string, string> { ["BI_PER_PERSON"] = "BI-1300K", ["PD_PER_ACCIDENT"] = "PD-1300K" } };

    private static object Draft(object vehicle, object[]? coverages = null, Dictionary<string, string>? answers = null) => new
    {
        elements = new object[]
        {
            new { element = "policyLine", id = "pl", fields = new Dictionary<string, string>() },
            vehicle,
            new { element = "driver", id = "d1", parent = "pl", fields = new Dictionary<string, string> { ["dateOfBirth"] = "1985-04-12", ["licenceIssueDate"] = "2005-06-01" } },
        },
        coverages = coverages ?? [Mtpl()],
        questionSet = answers is null ? null : "MOTOR-RISK",
        answers,
    };

    [Fact]
    public async Task REQ_PFC_224_225_draft_validation_accepts_a_complete_draft_and_returns_field_errors_for_bad_choices_cardinality_and_mtpl()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-R3"));
        var hash = import.Text("artefactHash");
        const string path = "/api/pfc/v1/policy-drafts/validate";

        var (ok, valid) = await PostAsync(path, new { hash, draft = Draft(Vehicle(), answers: new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "NO" }) });
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, valid?.ToJsonString());
        valid.Text("valid").ShouldBe("true", valid?.ToJsonString());

        // An invalid CHOICE field value is a field error with a path and a Greek and English message.
        var (_, badChoice) = await PostAsync(path, new { hash, draft = Draft(Vehicle("STEAM")) });
        badChoice.Text("valid").ShouldBe("false");
        var error = badChoice!["errors"]!.AsArray().Single()!;
        error["rule"]!.GetValue<string>().ShouldBe("PFC-VAL-CHOICE");
        error["path"]!.GetValue<string>().ShouldBe("elements[1].fields.fuelType");
        error["level"]!.GetValue<string>().ShouldBe("FIELD");
        error["message"]!["el"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        // MTPL is mandatory; an unknown option and a missing statutory limit are refused.
        var (_, noMtpl) = await PostAsync(path, new { hash, draft = Draft(Vehicle(), coverages: []) });
        Rules(noMtpl).ShouldContain("PFC-VAL-MTPL-REQUIRED");
        var (_, badTerm) = await PostAsync(path, new
        {
            hash,
            draft = Draft(Vehicle(), coverages: [new { coverage = "MTPL", terms = new Dictionary<string, string> { ["BI_PER_PERSON"] = "BI-9" } }]),
        });
        var rules = Rules(badTerm);
        rules.ShouldContain("PFC-VAL-TERM-OPTION");
        rules.ShouldContain("PFC-VAL-MTPL-MINIMUM");

        // Cardinality: the policy line allows exactly one vehicle.
        var twoVehicles = new
        {
            elements = new object[]
            {
                new { element = "policyLine", id = "pl", fields = new Dictionary<string, string>() },
                Vehicle(),
                new { element = "vehicle", id = "v2", parent = "pl", fields = new Dictionary<string, string>() },
            },
            coverages = Array.Empty<object>(),
        };
        var (_, many) = await PostAsync(path, new { hash, draft = twoVehicles });
        Rules(many).ShouldContain("PFC-VAL-CARDINALITY");

        // A question answer outside the allowed choices, and a knock-out answer.
        var (_, badAnswer) = await PostAsync(path, new { hash, draft = Draft(Vehicle(), answers: new Dictionary<string, string> { ["Q-USAGE"] = "SPACE" }) });
        badAnswer!["errors"]!.AsArray().Select(e => e!["path"]!.GetValue<string>()).ShouldContain("answers.Q-USAGE");
        var (_, knock) = await PostAsync(path, new { hash, draft = Draft(Vehicle(), answers: new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "YES" }) });
        knock.Text("valid").ShouldBe("false");

        var (unknown, _) = await PostAsync(path, new { hash = new string('b', 64), draft = Draft(Vehicle()) });
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task NX_UI_14b_the_product_list_is_served_in_keyset_pages_ordered_by_code()
    {
        foreach (var code in new[] { "MOTOR-P1", "MOTOR-P2", "MOTOR-P3" })
        {
            await ImportAsync(_client, Seed(code));
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var url = "/api/pfc/v1/products?limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var (response, body) = await GetAsync(_client, url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
            body!["items"]!.AsArray().Count.ShouldBe(1);
            seen.Add(body.Text("items.0.code"));
            body.Text("items.0.versions.0.version").ShouldBe("1.0");
            cursor = body["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                break;
            }
        }

        seen.ShouldBe([.. seen.Order(StringComparer.Ordinal)]);
        seen.ShouldContain("MOTOR-P1");
        seen.ShouldContain("MOTOR-P3");
        seen.Distinct().Count().ShouldBe(seen.Count);

        var (bad, _) = await GetAsync(_client, "/api/pfc/v1/products?limit=500");
        bad.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        var (badCursor, _) = await GetAsync(_client, "/api/pfc/v1/products?cursor=%25%25%25");
        badCursor.StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }
}
