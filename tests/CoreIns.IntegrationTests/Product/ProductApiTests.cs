using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Product.Domain;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.Platform.Contracts;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static CoreIns.IntegrationTests.Party.PartyApi;
using static CoreIns.IntegrationTests.Product.ProductApi;

namespace CoreIns.IntegrationTests.Product;

/// <summary>
/// The SL-PFC slice over HTTP on a real PostgreSQL 17: the Motor Private Car definition loaded through the import path,
/// resolved by date, and read through the catalogue, charge-type and question-set operations (W2-PFC-01..04 subset).
/// </summary>
public sealed class ProductApiTests(PostgresFixture database) : IClassFixture<PostgresFixture>, IAsyncLifetime
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
    public async Task REQ_PFC_031_032_162_193_the_seed_is_imported_locked_and_hashed_deterministically_and_importing_it_again_changes_nothing()
    {
        var key = Guid.NewGuid();
        var (created, body) = await ImportAsync(_client, Seed("MOTOR-T1"), key: key);

        created.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("product").ShouldBe("MOTOR-T1");
        body.Text("version").ShouldBe("1.0");
        body.Text("status").ShouldBe("LOCKED");
        body.Text("created").ShouldBe("true");
        var hash = body.Text("artefactHash");
        hash.ShouldMatch("^[0-9a-f]{64}$");

        // REQ-PFC-193/194: the hash is the SHA-256 of the canonical JSON; the same source compiles to the same bytes.
        var local = ArtefactCompiler.Compile(Modules_ParseSeed("MOTOR-T1"));
        local.IsSuccess.ShouldBeTrue();
        local.Value.Hash.Value.ShouldBe(hash);
        local.Value.CanonicalJson.ShouldNotContain('\n');

        // Same document, new key: nothing changes (200, created=false). Same key: the stored response is replayed.
        var (again, againBody) = await ImportAsync(_client, Seed("MOTOR-T1"));
        again.StatusCode.ShouldBe(HttpStatusCode.OK, againBody?.ToJsonString());
        againBody.Text("created").ShouldBe("false");
        againBody.Text("artefactHash").ShouldBe(hash);
        var (replay, replayBody) = await ImportAsync(_client, Seed("MOTOR-T1"), key: key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replayBody.Text("artefactHash").ShouldBe(hash);

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pfc.product WHERE code = 'MOTOR-T1'")).ShouldBe(1);
        (await ScalarAsync<string>(dataSource, "SELECT status || '/' || lifecycle_substate FROM pfc.product_version v JOIN pfc.product p USING (product_id) WHERE p.code = 'MOTOR-T1'"))
            .ShouldBe("LOCKED/ACTIVE");
        (await ScalarAsync<long>(dataSource, $"SELECT count(*) FROM pfc.artifact WHERE artefact_hash = '{hash}'")).ShouldBe(1);

        // Outbox event and audit record commit with the import (REQ-PFC-162).
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ProductVersionPublished' AND payload->>'productCode' = 'MOTOR-T1'")).ShouldBe(1);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM plt.audit_event WHERE operation = 'pfc.ProductVersion.import' AND outcome = 'Succeeded' AND object_id = 'MOTOR-T1@1.0'")).ShouldBe(2); // the import and the no-op re-import with a new key (the replay with the same key is not audited again)
    }

    [Fact]
    public async Task REQ_PFC_001_167_221_the_version_in_force_on_the_term_start_date_is_resolved_with_manifest_and_resolution_hash()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T2"));
        var hash = import.Text("artefactHash");

        var (ok, body) = await ResolveAsync(_client, "MOTOR-T2", "WEB_DIRECT", "2026-10-07");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("version").ShouldBe("1.0");
        body.Text("artefactHash").ShouldBe(hash);
        body.Text("resolutionManifest.artefactHash").ShouldBe(hash);
        body.Text("resolutionManifest.paymentPlanVersions").ShouldContain("INST-12-DD");
        body.Text("resolutionManifest.uwRuleSetVersions.PRE_QUOTE").ShouldBe("UW-MOTOR-GR-Q");
        body.Text("resolutionHash").ShouldMatch("^[0-9a-f]{64}$");

        // Same inputs, same resolution hash; another date or channel gives another one.
        var (_, same) = await ResolveAsync(_client, "MOTOR-T2", "WEB_DIRECT", "2026-10-07");
        same.Text("resolutionHash").ShouldBe(body.Text("resolutionHash"));
        var (_, otherDate) = await ResolveAsync(_client, "MOTOR-T2", "WEB_DIRECT", "2026-10-08");
        otherDate.Text("resolutionHash").ShouldNotBe(body.Text("resolutionHash"));

        var (renewal, renewalBody) = await ResolveAsync(_client, "MOTOR-T2", "STAFF", "2026-12-01", "Renewal");
        renewal.StatusCode.ShouldBe(HttpStatusCode.OK, renewalBody?.ToJsonString());

        var (beforeWindow, beforeBody) = await ResolveAsync(_client, "MOTOR-T2", "WEB_DIRECT", "2025-12-31");
        beforeWindow.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        beforeBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");

        var (badChannel, badChannelBody) = await ResolveAsync(_client, "MOTOR-T2", "BANK_BRANCH", "2026-10-07");
        badChannel.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        badChannelBody.Text("code").ShouldBe("PFC-ERR-NO-VERSION");

        var (unknown, unknownBody) = await ResolveAsync(_client, "NO-SUCH-PRODUCT", "WEB_DIRECT", "2026-10-07");
        unknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        unknownBody.Text("code").ShouldBe("PFC-ERR-UNKNOWN-PRODUCT");

        // Another legal entity's product is "unknown", never an error that reveals it (REQ-PTY-035 pattern).
        var (otherEntity, otherBody) = await ResolveAsync(_client, "MOTOR-T2", "WEB_DIRECT", "2026-10-07", legalEntity: "GR-OTHER");
        otherEntity.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        otherBody.Text("code").ShouldBe("PFC-ERR-UNKNOWN-PRODUCT");
    }

    [Fact]
    public async Task REQ_PFC_033_164_166_169_167_a_successor_closes_the_predecessors_window_and_old_terms_keep_resolving_to_the_old_artefact()
    {
        var (_, first) = await ImportAsync(_client, Seed("MOTOR-T3"));
        var firstHash = first.Text("artefactHash");

        var second = Seed("MOTOR-T3");
        second["version"] = "2.0";
        second["windows"]!["newBusiness"]!["from"] = "2027-01-01";
        second["windows"]!["renewal"]!["from"] = "2027-01-01";
        second["product"]!["name"]!["en"] = "Motor Private Car (2027)";
        var (created, secondBody) = await ImportAsync(_client, second);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, secondBody?.ToJsonString());
        var secondHash = secondBody.Text("artefactHash");
        secondHash.ShouldNotBe(firstHash);

        // REQ-PFC-169: publishing 2.0 does not change what a term starting before 2027 resolves to.
        var (_, before) = await ResolveAsync(_client, "MOTOR-T3", "WEB_DIRECT", "2026-12-31");
        before.Text("version").ShouldBe("1.0");
        before.Text("artefactHash").ShouldBe(firstHash);
        var (_, after) = await ResolveAsync(_client, "MOTOR-T3", "WEB_DIRECT", "2027-01-01");
        after.Text("version").ShouldBe("2.0");
        after.Text("artefactHash").ShouldBe(secondHash);

        // The old artefact stays retrievable by its hash (REQ-PFC-181, REQ-PFC-227).
        var (old, oldBody) = await GetAsync(_client, $"/api/pfc/v1/artifacts/{firstHash}");
        old.StatusCode.ShouldBe(HttpStatusCode.OK);
        oldBody.Text("canonicalJsonArtefact.version").ShouldBe("1.0");

        // BR-PFC-001: a third version whose window intersects a Locked one is refused.
        var third = Seed("MOTOR-T3");
        third["version"] = "3.0";
        third["windows"]!["newBusiness"]!["from"] = "2026-06-01";
        third["windows"]!["renewal"]!["from"] = "2026-06-01";
        third["product"]!["name"]!["en"] = "Motor Private Car (clash)";
        var (clash, clashBody) = await ImportAsync(_client, third);
        clash.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        clashBody.Text("code").ShouldBe("PFC-ERR-WINDOW-OVERLAP");

        // REQ-PFC-164: a Locked version is immutable; a changed definition under the same number is refused.
        var changed = Seed("MOTOR-T3");
        changed["product"]!["name"]!["en"] = "Edited in place";
        var (exists, existsBody) = await ImportAsync(_client, changed);
        exists.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        existsBody.Text("code").ShouldBe("PFC-ERR-VERSION-EXISTS");
    }

    [Fact]
    public async Task REQ_PFC_003_067_073_085_088_the_catalogue_serves_mtpl_as_required_with_limits_bound_to_mkt_final_keys()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T4"));
        var hash = import.Text("artefactHash");

        var (ok, body) = await GetAsync(_client, $"/api/pfc/v1/catalogue/{hash}?scope=coverages");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        body.Text("product").ShouldBe("MOTOR-T4");
        body!["elements"].ShouldBeNull();
        body["coverages"]!.AsArray().Select(c => c!["code"]!.GetValue<string>()).ShouldBe(["MTPL", "OWN-DAMAGE", "WINDSCREEN"]);
        body.Text("coverages.0.existence").ShouldBe("REQUIRED");
        body.Text("coverages.0.removal").ShouldBe("BLOCK");
        body.Text("coverages.0.regulatory.SII_LOB").ShouldBe("4");
        body.Text("coverages.0.regulatory.IPT_CLASS").ShouldBe("GR-OTHER-15");
        body.Text("coverages.0.terms.0.code").ShouldBe("BI_PER_PERSON");
        body.Text("coverages.0.terms.0.aggregationBasis").ShouldBe("PER_INJURED_PERSON");
        body.Text("coverages.0.terms.0.options.0.value").ShouldBe("1300000");
        body.Text("coverages.0.terms.0.finalBinding.key").ShouldBe("gr.mtpl.min_bi_per_person");
        body.Text("coverages.0.terms.0.finalBinding.legalStatus").ShouldBe("UNVERIFIED");
        body.Text("coverages.0.terms.1.finalBinding.key").ShouldBe("gr.mtpl.min_pd_per_accident");
        body.Text("coverages.1.existence").ShouldBe("ELECTABLE");
        body.Text("coverages.1.terms.1.options.0.illustrative").ShouldBe("true");

        // The statutory VALUE is MKT data: the catalogue holds the key and does not invent the value (resolver not bound yet).
        body["finals"]!.AsArray().Count.ShouldBe(2);
        body["finals"]![0]!["binding"]!["valueAtEffectiveDate"].ShouldBeNull();

        var (elements, elementsBody) = await GetAsync(_client, $"/api/pfc/v1/catalogue/{hash}?scope=elements");
        elements.StatusCode.ShouldBe(HttpStatusCode.OK);
        elementsBody!["elements"]!.AsArray().Select(e => e!["code"]!.GetValue<string>()).ShouldBe(["policyLine", "vehicle", "driver"]);
        elementsBody["coverages"].ShouldBeNull();

        var (item, itemBody) = await GetAsync(_client, $"/api/pfc/v1/catalogue/get-item?hash={hash}&scope=coverage&code=OWN-DAMAGE");
        item.StatusCode.ShouldBe(HttpStatusCode.OK);
        itemBody.Text("coverage.code").ShouldBe("OWN-DAMAGE");
        var (field, fieldBody) = await GetAsync(_client, $"/api/pfc/v1/catalogue/get-item?hash={hash}&scope=element&code=vehicle");
        field.StatusCode.ShouldBe(HttpStatusCode.OK);
        fieldBody.Text("element.fields.0.code").ShouldBe("registrationNumber");
        fieldBody.Text("element.fields.0.piiClass").ShouldBe("P2");

        var (missing, missingBody) = await GetAsync(_client, $"/api/pfc/v1/catalogue/get-item?hash={hash}&scope=coverage&code=NOPE");
        missing.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        missingBody.Text("code").ShouldBe("PFC-ERR-UNKNOWN-ITEM");
        var (unknownHash, unknownBody) = await GetAsync(_client, $"/api/pfc/v1/catalogue/{new string('0', 64)}");
        unknownHash.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknownBody.Text("code").ShouldBe("PFC-ERR-UNKNOWN-HASH");
    }

    [Fact]
    public async Task REQ_PFC_004_113_119_123_124_125_244_250_the_charge_type_catalogue_carries_premium_tax_and_levy_charges_without_rates()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T5"));
        var hash = import.Text("artefactHash");

        var (ok, body) = await GetAsync(_client, $"/api/pfc/v1/charge-types?hash={hash}");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK, body?.ToJsonString());
        var items = body!["items"]!.AsArray().Select(i => i!.AsObject()).ToDictionary(i => i["code"]!.GetValue<string>());
        items.Keys.ShouldBe(["PREM-MTPL", "PREM-OD", "PREM-WINDSCREEN", "GR-IPT", "GR-AUXF-PH", "GR-AUXF-INS", "GR-STAMP-AUXF-PH"]);
        body["nextCursor"].ShouldBeNull();

        // REQ-PFC-250: written premium follows the category.
        foreach (var code in new[] { "PREM-MTPL", "PREM-OD", "PREM-WINDSCREEN" })
        {
            items[code]["writtenPremium"]!.GetValue<bool>().ShouldBeTrue(code);
            items[code]["computedBy"]!.GetValue<string>().ShouldBe("RATING");
        }

        foreach (var code in new[] { "GR-IPT", "GR-AUXF-PH", "GR-AUXF-INS", "GR-STAMP-AUXF-PH" })
        {
            items[code]["writtenPremium"]!.GetValue<bool>().ShouldBeFalse(code);
            items[code]["computedBy"]!.GetValue<string>().ShouldBe("TAX_CALCULATOR");
            items[code]["cancellationTreatment"]!.GetValue<string>().ShouldBe("PACK_TAX_TREATMENT");
        }

        // REQ-PFC-119/125: the insurer share of the levy is accrued, not billed; the policyholder share is billed.
        items["GR-AUXF-INS"]["billingTreatment"]!.GetValue<string>().ShouldBe("ACCRUED_NOT_BILLED");
        items["GR-AUXF-PH"]["billingTreatment"]!.GetValue<string>().ShouldBe("BILLED");
        items["GR-AUXF-PH"]["beneficiary"]!.GetValue<string>().ShouldBe("GUARANTEE_FUND");
        items["GR-AUXF-PH"]["category"]!.GetValue<string>().ShouldBe("LEVY");
        items["GR-AUXF-PH"]["baseChargeCodes"]![0]!.GetValue<string>().ShouldBe("PREM-MTPL");
        items["GR-STAMP-AUXF-PH"]["baseChargeCodes"]![0]!.GetValue<string>().ShouldBe("GR-AUXF-PH");

        // REQ-PFC-123: rates are MKT pack data; the product references configuration keys only.
        items["GR-AUXF-PH"]["configKeys"]![1]!.GetValue<string>().ShouldBe("tax.levy.auxfund.split.policyholder_share");
        items["GR-AUXF-INS"]["configKeys"]![1]!.GetValue<string>().ShouldBe("tax.levy.auxfund.split.insurer_share");
        body.ToJsonString().ShouldNotContain("\"rate\"");

        var (levies, leviesBody) = await GetAsync(_client, $"/api/pfc/v1/charge-types?hash={hash}&filters=category=LEVY");
        leviesBody!["items"]!.AsArray().Count.ShouldBe(2);
        levies.StatusCode.ShouldBe(HttpStatusCode.OK);

        var (_, page1) = await GetAsync(_client, $"/api/pfc/v1/charge-types?hash={hash}&limit=3");
        page1!["items"]!.AsArray().Count.ShouldBe(3);
        var cursor = page1.Text("nextCursor");
        var (_, page2) = await GetAsync(_client, $"/api/pfc/v1/charge-types?hash={hash}&limit=5&cursor={cursor}");
        page2!["items"]!.AsArray().Select(i => i!["code"]!.GetValue<string>()).ShouldBe(["GR-IPT", "GR-AUXF-PH", "GR-AUXF-INS", "GR-STAMP-AUXF-PH"]);
        page2["nextCursor"].ShouldBeNull();
    }

    [Fact]
    public async Task REQ_PFC_006_109_the_question_set_is_served_and_evaluated_with_visibility_knock_out_and_referral()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T6"));
        var hash = import.Text("artefactHash");

        var (get, set) = await GetAsync(_client, $"/api/pfc/v1/question-sets/{hash}?set=MOTOR-RISK");
        get.StatusCode.ShouldBe(HttpStatusCode.OK, set?.ToJsonString());
        set.Text("questionSet.type").ShouldBe("UNDERWRITING");
        set!["questionSet"]!["questions"]!.AsArray().Count.ShouldBe(4);

        async Task<JsonNode?> Evaluate(object answers, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var (response, body) = await SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/question-sets/evaluate", new { hash, set = "MOTOR-RISK", answers }, Staff, withKey: false);
            response.StatusCode.ShouldBe(expected, body?.ToJsonString());
            return body;
        }

        // Nothing answered: the always-visible required questions are missing; the conditional one is hidden.
        var empty = await Evaluate(new { });
        empty!["complete"]!.GetValue<bool>().ShouldBeFalse();
        empty["missingRequired"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["Q-USAGE", "Q-HIRE-REWARD"]);
        empty.Text("questions.1.visible").ShouldBe("false");

        // Private use, no hire: complete, no outcome.
        var clean = await Evaluate(new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "NO" });
        clean!["complete"]!.GetValue<bool>().ShouldBeTrue();
        clean["knockOuts"]!.AsArray().Count.ShouldBe(0);
        clean["referrals"]!.AsArray().Count.ShouldBe(0);

        // Business use: refers, and the conditional question becomes visible and required (REQ-PFC-061).
        var business = await Evaluate(new Dictionary<string, string> { ["Q-USAGE"] = "BUSINESS", ["Q-HIRE-REWARD"] = "NO" });
        business.Text("referrals.0.question").ShouldBe("Q-USAGE");
        business.Text("referrals.0.reasonKey").ShouldBe("pfc.question.usage.business");
        business.Text("questions.1.visible").ShouldBe("true");
        business.Text("questions.1.required").ShouldBe("true");
        business!["missingRequired"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["Q-BUSINESS-USE"]);

        // Hire or reward: knock-out.
        var hire = await Evaluate(new Dictionary<string, string> { ["Q-USAGE"] = "PRIVATE", ["Q-HIRE-REWARD"] = "YES" });
        hire.Text("knockOuts.0.question").ShouldBe("Q-HIRE-REWARD");

        // An answer that is not allowed, or a question that is not in the set, is refused.
        var bad = await Evaluate(new Dictionary<string, string> { ["Q-USAGE"] = "SPACESHIP" }, HttpStatusCode.UnprocessableEntity);
        bad.Text("code").ShouldBe("PFC-ERR-UNKNOWN-ITEM");
        await Evaluate(new Dictionary<string, string> { ["Q-NOPE"] = "1" }, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task REQ_PFC_088_085_080_062_lint_refuses_definitions_that_break_the_statutory_or_structural_rules()
    {
        async Task<JsonNode?> Refused(JsonObject definition, string field, string code)
        {
            var (response, body) = await ImportAsync(_client, definition);
            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body?.ToJsonString());
            body.Text("code").ShouldBe("PFC-ERR-INVALID-DEFINITION");
            body!["errors"]!.AsArray().Any(e => e!["code"]!.GetValue<string>() == code && e["field"]!.GetValue<string>().StartsWith(field, StringComparison.Ordinal))
                .ShouldBeTrue(body.ToJsonString());
            return body;
        }

        // Greece: MTPL electable breaks PFC-LINT-STATUTORY-001 (and a required coverage that can be removed breaks REQUIRED-REMOVABLE).
        var electable = Seed("MOTOR-T7A");
        electable["coverages"]![0]!["existence"] = "ELECTABLE";
        await Refused(electable, "coverages[0]", "PFC-LINT-STATUTORY-001");

        var removable = Seed("MOTOR-T7B");
        removable["coverages"]![0]!["removal"] = "ALLOWED";
        await Refused(removable, "coverages[0]", "PFC-LINT-STATUTORY-001");

        // MTPL limit not bound to the final key.
        var unbound = Seed("MOTOR-T7C");
        unbound["coverages"]![0]!["terms"]![0]!.AsObject().Remove("finalBinding");
        await Refused(unbound, "coverages[0].terms", "PFC-LINT-STATUTORY-001");

        // REQ-PFC-080: an option-list term with no options.
        var noOptions = Seed("MOTOR-T7D");
        noOptions["coverages"]![1]!["terms"]![1]!["options"] = new JsonArray();
        await Refused(noOptions, "coverages[1].terms[1].options", "PFC-LINT-OPTIONS");

        // REQ-PFC-250: written premium cannot be overridden.
        var written = Seed("MOTOR-T7E");
        written["chargeTypes"]![3]!["writtenPremium"] = true;
        await Refused(written, "chargeTypes[3].writtenPremium", "PFC-LINT-WRITTEN-PREMIUM");

        // REQ-PFC-116: a tax charge without PACK_TAX_TREATMENT; an orphaned reference.
        var tax = Seed("MOTOR-T7F");
        tax["chargeTypes"]![3]!["cancellationTreatment"] = "PRO_RATA";
        await Refused(tax, "chargeTypes[3]", "PFC-LINT-TAX-SHAPE");
        var orphan = Seed("MOTOR-T7G");
        orphan["chargeTypes"]![4]!["baseChargeCodes"] = new JsonArray("PREM-NOPE");
        await Refused(orphan, "chargeTypes[4]", "PFC-LINT-REF-001");

        // A question maps to a field that does not exist; a condition refers forward.
        var question = Seed("MOTOR-T7H");
        question["questionSets"]![0]!["questions"]![0]!["mapsToField"] = "vehicle.nope";
        await Refused(question, "questionSets[0].questions[0].mapsToField", "PFC-LINT-QUESTION-FIELD");

        // Another legal entity's definition is refused outright.
        var foreign = Seed("MOTOR-T7I");
        foreign["legalEntity"] = "GR-OTHER";
        await Refused(foreign, "legalEntity", "PFC-LINT-LEGAL-ENTITY");

        await using var dataSource = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(dataSource, "SELECT count(*) FROM pfc.product WHERE code LIKE 'MOTOR-T7%'")).ShouldBe(0);
    }

    [Fact]
    public async Task Permissions_import_needs_the_admin_role_reads_need_a_staff_role_and_anonymous_is_refused()
    {
        var (staffImport, _) = await ImportAsync(_client, Seed("MOTOR-T8"), roles: Staff);
        staffImport.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var (anonymous, _) = await SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/product-versions/resolve", new { jurisdiction = "GR", legalEntity = "GR-TEST", product = "X", channel = "STAFF", transactionType = "NewBusiness" }, roles: "", withKey: false);
        anonymous.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        using var bare = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/pfc/v1/charge-types?hash={new string('a', 64)}", UriKind.Relative));
        (await _client.SendAsync(bare, TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reads_never_store_a_result_even_when_the_caller_sends_an_Idempotency_Key_and_the_app_role_cannot_delete_or_change_artefacts()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T9"));
        var hash = import.Text("artefactHash");

        var key = Guid.NewGuid();
        var (response, _) = await SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/product-versions/resolve?validAt=2026-10-07",
            new { jurisdiction = "GR", legalEntity = "GR-TEST", product = "MOTOR-T9", channel = "STAFF", transactionType = "NewBusiness" }, Staff, key);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var (evaluate, _) = await SendAsync(_client, HttpMethod.Post, "/api/pfc/v1/question-sets/evaluate",
            new { hash, set = "MOTOR-RISK", answers = new Dictionary<string, string>() }, Staff, key);
        evaluate.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var superuser = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(superuser, $"SELECT count(*) FROM plt.idempotency_record WHERE idempotency_key = '{key}'")).ShouldBe(0);

        await using var app = NpgsqlDataSource.Create(database.AppConnectionString);
        async Task<bool> Can(string table, string privilege)
        {
            await using var command = app.CreateCommand($"SELECT has_table_privilege('app', '{table}', '{privilege}')");
            return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }

        foreach (var table in new[] { "product", "product_version", "artifact" })
        {
            (await Can($"pfc.{table}", "INSERT")).ShouldBeTrue(table);
            (await Can($"pfc.{table}", "DELETE")).ShouldBeFalse(table);
        }

        // REQ-PFC-197: even with UPDATE on the schema, a compiled artefact cannot be changed (trigger).
        await using var update = app.CreateCommand($"UPDATE pfc.artifact SET canonical_json = '{{}}' WHERE artefact_hash = '{hash}'");
        var failure = await Should.ThrowAsync<PostgresException>(async () => await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        failure.MessageText.ShouldContain("write-once");
    }

    [Fact]
    public async Task The_in_process_contracts_resolve_and_read_with_the_callers_key_without_storing_it()
    {
        var (_, import) = await ImportAsync(_client, Seed("MOTOR-T10"));
        var hash = Sha256Hash.Parse(import.Text("artefactHash"));

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RequestContext>();
        context.Actor = ActorRef.User("in-process-test");
        context.Roles = ["Staff.Underwriter"];
        context.LegalEntity = LegalEntityCode.Parse("GR-TEST");
        context.Jurisdiction = Jurisdiction.Parse("GR");
        context.ConfigurationHash = ConfigurationHash.Parse(new string('a', 64));
        var key = IdempotencyKey.New();
        using var _ = context.Use(key, false);
        var ct = TestContext.Current.CancellationToken;

        var resolved = await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Product.Contracts.IProductProductVersionService>()
            .ResolveAsync(
                new CoreIns.Modules.Product.Contracts.Api.ProductVersionResolveRequest
                {
                    Jurisdiction = "GR", LegalEntity = "GR-TEST", Product = "MOTOR-T10", Channel = "WEB_DIRECT",
                    TransactionType = CoreIns.Modules.Product.Contracts.Api.ProductVersionResolveRequest.TransactionTypeValue.NewBusiness,
                },
                ValidAt.From(new BusinessDate(2026, 10, 7)), cancellationToken: ct);
        resolved.ArtefactHash.ShouldBe(hash);
        resolved.Version.ToString().ShouldBe("1.0");

        var catalogue = await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Product.Contracts.IProductCatalogueService>()
            .GetAsync(hash.Value, "coverages", cancellationToken: ct);
        catalogue.Coverages!.Select(c => c.Code).ShouldBe(["MTPL", "OWN-DAMAGE", "WINDSCREEN"]);

        var charges = await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Product.Contracts.IProductChargeTypeService>()
            .ListAsync(hash: hash, cancellationToken: ct);
        charges.Items.Count.ShouldBe(7);

        var questions = await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Product.Contracts.IProductQuestionSetService>()
            .GetAsync(hash.Value, "MOTOR-RISK", ct);
        questions.QuestionSet.Questions.Count.ShouldBe(4);

        var unknown = await Should.ThrowAsync<CoreIns.Platform.Errors.DomainException>(async () =>
            await scope.ServiceProvider.GetRequiredService<CoreIns.Modules.Product.Contracts.IProductCatalogueService>().GetAsync(new string('0', 64), cancellationToken: ct));
        unknown.Error.Code.ToString().ShouldBe("PFC-ERR-UNKNOWN-HASH");

        await using var superuser = NpgsqlDataSource.Create(database.SuperuserConnectionString);
        (await ScalarAsync<long>(superuser, $"SELECT count(*) FROM plt.idempotency_record WHERE idempotency_key = '{key}'")).ShouldBe(0);
    }

    private static CoreIns.Modules.Product.Contracts.Api.ProductArtefact Modules_ParseSeed(string code) =>
        System.Text.Json.JsonSerializer.Deserialize<CoreIns.Modules.Product.Contracts.Api.ProductArtefact>(Seed(code).ToJsonString(), SharedKernelJson.Options)!;

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
