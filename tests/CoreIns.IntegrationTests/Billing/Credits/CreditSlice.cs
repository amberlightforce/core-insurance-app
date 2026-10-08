using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Events;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using static CoreIns.IntegrationTests.Bil.BillingSlice;
using static CoreIns.IntegrationTests.Party.PartyApi;

namespace CoreIns.IntegrationTests.Bil.Credits;

/// <summary>
/// A scripted <c>TaxCalculator.treatment</c> (MKT SPI 4): the Greece default rows of D-SL3-05 as the fake of the MKT work
/// package serves them (ordinary cancellation and endorsement credit KEEP_NOT_REDUCED, new business and endorsement debit
/// APPLY, every row PendingOpinion = provisional), and RULE_MISSING for anything else. Illustrative test data.
/// </summary>
internal sealed class FakeTaxCalculator : ITaxCalculator
{
    public const string Version = "0.1.0";

    /// <summary>Overrides the action served for a transaction kind (null = the default rows).</summary>
    public Func<TaxTreatmentRequest, TreatmentAction?>? Override { get; set; }

    public int Calls { get; private set; }

    public static string RuleIdOf(TreatmentAction action, TaxTransactionKind kind) => action switch
    {
        TreatmentAction.Apply when kind == TaxTransactionKind.NewBusiness => "GR-TRT-IPT-NEW-BUSINESS",
        TreatmentAction.Apply => "GR-TRT-IPT-ENDORSEMENT-DEBIT",
        TreatmentAction.KeepNotReduced when kind == TaxTransactionKind.Cancellation => "GR-TRT-IPT-CANCEL-POLICYHOLDER",
        TreatmentAction.KeepNotReduced => "GR-TRT-IPT-ENDORSEMENT-CREDIT",
        _ => "GR-TRT-IPT-OTHER",
    };

    public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        var action = Override?.Invoke(request) ?? request.TransactionKind switch
        {
            TaxTransactionKind.NewBusiness or TaxTransactionKind.EndorsementDebit when request.Category == TaxCategory.Tax => TreatmentAction.Apply,
            TaxTransactionKind.EndorsementCredit or TaxTransactionKind.Cancellation when request.Category == TaxCategory.Tax
                && (request.TransactionKind == TaxTransactionKind.EndorsementCredit || request.CancellationSource == "Policyholder") => TreatmentAction.KeepNotReduced,
            _ => throw new SpiException(new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"), "no treatment row"),
        };
        var (credit, liability, document) = action switch
        {
            TreatmentAction.Apply => (CustomerCredit.None, AuthorityLiability.NotReduce, FiscalDocumentTreatment.None),
            TreatmentAction.KeepNotReduced => (CustomerCredit.None, AuthorityLiability.NotReduce, FiscalDocumentTreatment.None),
            TreatmentAction.ReduceProRata => (CustomerCredit.ProRata, AuthorityLiability.Reduce, FiscalDocumentTreatment.None),
            _ => (CustomerCredit.Full, AuthorityLiability.Reduce, FiscalDocumentTreatment.CreditNote),
        };
        return ValueTask.FromResult(new TaxTreatmentResult
        {
            ChargeType = request.ChargeType,
            Action = action,
            CustomerCredit = credit,
            AuthorityLiability = liability,
            FiscalDocument = document,
            RuleId = RuleIdOf(action, request.TransactionKind),
            RuleVersion = Version,
            LegalStatus = TreatmentLegalStatus.Pending,
            LegalSourceRef = "test",
        });
    }
}

/// <summary>A bound and billed term, and the hand-made servicing events a test sends BIL for it.</summary>
internal sealed class CreditScenario
{
    public required BillingSlice Slice { get; init; }

    public required BoundPolicy Policy { get; init; }

    public required string AccountId { get; init; }

    public required string InvoiceId { get; init; }

    public required List<EventEnvelope> Deltas { get; init; }

    public required EventEnvelope Bound { get; init; }

    public const string TaxCategory = "TAX";

    /// <summary>Bind, run the outbox (BIL bills the ANNUAL invoice) and read the invoice.</summary>
    public static async Task<CreditScenario> BilledAsync(BillingSlice slice)
    {
        var policy = await slice.BindAsync();
        await slice.DrainAsync();
        var invoice = await slice.InvoiceOfAsync(policy.PolicyId);
        var envelopes = await slice.EnvelopesAsync(policy.PolicyId);
        return new CreditScenario
        {
            Slice = slice,
            Policy = policy,
            AccountId = invoice.Text("invoice.billingAccountId"),
            InvoiceId = invoice.Text("invoice.invoiceId"),
            Deltas = [.. envelopes.Where(e => e.EventType.Value == "ChargeDeltaEmitted")],
            Bound = envelopes.Single(e => e.EventType.Value == "PolicyBound"),
        };
    }

    public static string CategoryOf(EventEnvelope delta) => delta.Payload["chargeCategory"]!.GetValue<string>();

    public static decimal AmountOf(EventEnvelope delta) => decimal.Parse(delta.Payload["netAmount"]!["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    /// <summary>
    /// A servicing set that mirrors the bind's lines: every non-tax line scaled by <paramref name="factor"/> (negative = credit),
    /// every tax line carrying <paramref name="taxAmount"/>, all under one new transaction. Returns the envelopes and the
    /// premium/fee total of the non-tax lines.
    /// </summary>
    public (List<EventEnvelope> Set, decimal NonTaxTotal, Guid TransactionId) ServicingSet(
        string kind, string? source, decimal factor, decimal taxAmount = 0m, string? taxRuleId = null, bool taxProvisional = true, Guid? term = null, Func<JsonObject, bool>? choose = null)
    {
        var transaction = Guid.CreateVersion7();
        var lines = Deltas.Where(d => choose?.Invoke((JsonObject)d.Payload) ?? true).ToList();
        var set = new List<EventEnvelope>();
        var total = 0m;
        var action = kind switch
        {
            "CANCELLATION" or "ENDORSEMENT_CREDIT" => TreatmentAction.KeepNotReduced,
            _ => TreatmentAction.Apply,
        };
        var taxKind = Enum.Parse<TaxTransactionKind>(string.Concat(kind.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant())));
        for (var i = 0; i < lines.Count; i++)
        {
            var payload = (JsonObject)lines[i].Payload.DeepClone();
            var isTax = CategoryOf(lines[i]) == TaxCategory;
            var amount = isTax ? taxAmount : Math.Round(AmountOf(lines[i]) * factor, 2, MidpointRounding.AwayFromZero);
            if (!isTax)
            {
                total += amount;
            }

            payload["chargeId"] = Guid.CreateVersion7().ToString();
            payload["transactionId"] = transaction.ToString();
            if (term is { } newTerm)
            {
                payload["termId"] = newTerm.ToString();
            }

            payload["netAmount"] = new JsonObject { ["amount"] = amount.ToString("0.00", CultureInfo.InvariantCulture), ["currency"] = "EUR" };
            payload["correlationKey"] = "test-" + transaction;
            payload["transactionKind"] = kind;
            if (source is not null)
            {
                payload["cancellationSource"] = source;
            }

            if (isTax)
            {
                payload["treatmentRuleId"] = taxRuleId ?? FakeTaxCalculator.RuleIdOf(action, taxKind);
                payload["treatmentRuleVersion"] = FakeTaxCalculator.Version;
                payload["legalStatus"] = "PendingOpinion";
                payload["provisional"] = taxProvisional;
            }

            var keys = lines[i].BusinessKeys.With("chargeId", payload["chargeId"]!.GetValue<string>()).With("transactionId", transaction.ToString());
            if (term is { } t)
            {
                keys = keys.With("policyTermId", t.ToString());
            }

            set.Add(lines[i] with
            {
                EventId = EventId.New(),
                Payload = payload,
                BusinessKeys = keys,
                Set = new EventSet(transaction, lines.Count, i + 1),
            });
        }

        return (set, total, transaction);
    }

    public async Task DeliverAsync(IEnumerable<EventEnvelope> envelopes, bool replay = false)
    {
        foreach (var envelope in envelopes)
        {
            (await Slice.InvokeAsync("BIL.ChargeDeltaEmitted.Intake", envelope, replay)).ShouldBeTrue();
        }
    }

    /// <summary>A PolicyCancelled envelope for the term (effective today).</summary>
    public EventEnvelope Cancelled(Guid transaction, string source = "Policyholder")
    {
        var payload = new JsonObject
        {
            ["transactionId"] = transaction.ToString(),
            ["termId"] = Policy.TermId,
            ["source"] = source,
            ["reason"] = "customer request",
            ["effectiveDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["refundMethod"] = "ProRata",
            ["kind"] = "STANDARD",
        };
        return Bound with
        {
            EventId = EventId.New(),
            EventType = EventTypeName.Parse("PolicyCancelled"),
            Payload = payload,
            BusinessKeys = Bound.BusinessKeys.With("transactionId", transaction.ToString()),
        };
    }

    public async Task<decimal> PayAsync(decimal amount)
    {
        var (response, body) = await Slice.PostAsync("/api/bil/v1/payments/take", new
        {
            billingAccountId = AccountId,
            amount = new { amount = amount.ToString(CultureInfo.InvariantCulture), currency = "EUR" },
            method = "BANK_TRANSFER",
            bankReference = "TRF-" + Guid.NewGuid().ToString("N")[..8],
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, body?.ToJsonString());
        body.Text("allocationOutcome").ShouldBe("ALLOCATED", body?.ToJsonString());
        return amount;
    }

    /// <summary>The invoices of the policy (INVOICE and CREDIT_NOTE), oldest first, each with its items.</summary>
    public async Task<List<JsonNode>> DocumentsAsync()
    {
        var (response, page) = await Slice.GetAsync($"/api/bil/v1/invoices?policyId={Policy.PolicyId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, page?.ToJsonString());
        var documents = new List<JsonNode>();
        foreach (var item in page!["items"]!.AsArray())
        {
            var (_, full) = await Slice.GetAsync($"/api/bil/v1/invoices/{item!["invoice"]!["invoiceId"]!.GetValue<string>()}");
            documents.Add(full!);
        }

        return documents;
    }

    public async Task<JsonNode> BalancesAsync()
    {
        var (_, account) = await Slice.GetAsync($"/api/bil/v1/billing-accounts/{AccountId}");
        return account!["balancesByState"]!;
    }
}

internal static class CreditSqlAsserts
{
    /// <summary>Every entry balances per currency (REQ-BIL-280).</summary>
    public static async Task AllEntriesBalanceAsync(this BillingSlice slice) =>
        (await slice.ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT entry_id FROM bil.ledger_line GROUP BY entry_id, currency"
            + " HAVING sum(CASE side WHEN 'DEBIT' THEN amount ELSE -amount END) <> 0) unbalanced")).ShouldBe(0);

    public static IServiceCollection WithFakeTreatment(this IServiceCollection services, FakeTaxCalculator calculator) =>
        services.AddSingleton<ITaxCalculator>(calculator);
}
