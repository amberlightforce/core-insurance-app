using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Billing.Contracts;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.Platform.Ai;
using CoreIns.Platform.Context;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Billing.Services.Ai;

/// <summary>An unmatched payment in suspense.</summary>
internal sealed record SuspensePayment(decimal Amount, string? Reference, string? PayerName);

/// <summary>An open invoice that could settle it.</summary>
internal sealed record SuspenseCandidate(string InvoiceNo, decimal OpenAmount, string? Reference, string? PayerName);

/// <summary>Request of <c>bil.Suspense.suggestMatch</c>: the receipt id only; the payment and open invoices are loaded server-side.</summary>
internal sealed record SuspenseMatchRequest(string? ReceiptId);

/// <summary>Deterministic suspense-match scoring (AI-BIL-01 fallback template). A suggestion only: allocation stays the manual action.</summary>
internal static class SuspenseMatching
{
    public const string FeatureId = "AI-BIL-01";

    public const string Note = "Suggestion only; allocate manually.";

    /// <summary>Up to three candidates with a positive score, best first (ties by invoice number).</summary>
    public static IReadOnlyList<(SuspenseCandidate Candidate, int Score)> Rank(SuspensePayment payment, IReadOnlyList<SuspenseCandidate> candidates) =>
    [
        .. candidates
            .Select(c => (Candidate: c, Score: Score(payment, c)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Candidate.InvoiceNo, StringComparer.Ordinal)
            .Take(3),
    ];

    public static JsonObject Build(SuspensePayment payment, IReadOnlyList<SuspenseCandidate> candidates)
    {
        var ranked = Rank(payment, candidates)
            .Select(x => (JsonNode?)new JsonObject { ["invoiceNo"] = x.Candidate.InvoiceNo, ["score"] = x.Score, ["exactAmount"] = x.Candidate.OpenAmount == payment.Amount })
            .ToArray();
        return new JsonObject { ["matches"] = new JsonArray(ranked), ["note"] = Note };
    }

    private static int Score(SuspensePayment p, SuspenseCandidate c)
    {
        var score = 0;
        if (c.OpenAmount == p.Amount)
        {
            score += 50;
        }

        if (!string.IsNullOrWhiteSpace(p.Reference)
            && (c.Reference?.Contains(p.Reference, StringComparison.OrdinalIgnoreCase) == true || p.Reference.Contains(c.InvoiceNo, StringComparison.OrdinalIgnoreCase)))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace(p.PayerName) && string.Equals(p.PayerName.Trim(), c.PayerName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        return score;
    }
}

/// <summary><c>bil.Suspense.suggestMatch</c> (AI-BIL-01) behind the AI gate (SL6-AI-FEATURES).</summary>
[ApiController]
[Route("api/bil/v1/suspense")]
internal sealed class SuspenseMatchController : ControllerBase
{
    /// <summary>Disabled: 503 PLT-ERR-AI-DISABLED and manual allocation is unchanged.</summary>
    [HttpPost("suggest-match")]
    [SkipIdempotency]
    [Authorize(Policy = "bil.Suspense.suggestMatch")]
    public async Task<IResult> SuggestAsync([FromBody] SuspenseMatchRequest request, [FromServices] BillingDbContext db, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.ReceiptId, out var receiptGuid))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["receiptId"] = ["A receipt id is required."] });
        }

        // The gate comes first (a disabled feature answers 503 whatever the id); the facts are loaded server-side after it.
        var result = await AiAssist.InvokeAsync(HttpContext, SuspenseMatching.FeatureId, new JsonObject { ["receiptId"] = request.ReceiptId }, cancellationToken).ConfigureAwait(false);
        if (result.Refusal is { } refusal)
        {
            return refusal;
        }

        var services = HttpContext.RequestServices;
        var context = services.GetRequiredService<RequestContext>();
        var legalEntity = services.GetRequiredService<ILegalEntityDirectory>().Resolve(context.LegalEntity ?? throw new InvalidOperationException("No legal entity."));
        var facts = await SuspenseFacts.LoadAsync(db, legalEntity, new PaymentId(receiptGuid), cancellationToken).ConfigureAwait(false);
        if (facts is null)
        {
            return Results.NotFound();
        }

        var (payment, candidates) = facts.Value;
        return Results.Ok(new { suggestion = SuspenseMatching.Build(payment, candidates), interactionId = result.Response!.InteractionId, featureId = SuspenseMatching.FeatureId, aiGenerated = true });
    }
}

/// <summary>Server-side facts of a suspense receipt: the unapplied amount and the open invoices of the same legal entity and currency.</summary>
internal static class SuspenseFacts
{
    public static async Task<(SuspensePayment Payment, List<SuspenseCandidate> Candidates)?> LoadAsync(BillingDbContext db, LegalEntityId legalEntity, PaymentId receiptId, CancellationToken cancellationToken)
    {
        var receipt = await db.Receipts.AsNoTracking()
            .SingleOrDefaultAsync(r => r.ReceiptId == receiptId && r.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        var allocated = await db.Allocations.AsNoTracking().Where(a => a.ReceiptId == receiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        var receivablePaid = await db.ReceivableAllocations.AsNoTracking().Where(a => a.ReceiptId == receiptId).SumAsync(a => a.Amount, cancellationToken).ConfigureAwait(false);
        var payerId = await db.Accounts.AsNoTracking().Where(a => a.BillingAccountId == receipt.BillingAccountId).Select(a => a.PayerPartyId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var payment = new SuspensePayment(receipt.Amount - allocated - receivablePaid, receipt.BankReference, payerId.ToString());

        string[] openStates = [Codes.Of(InvoiceState.Billed), Codes.Of(InvoiceState.Due), Codes.Of(InvoiceState.PartiallyPaid), Codes.Of(InvoiceState.Overdue)];
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.LegalEntityId == legalEntity && i.Currency == receipt.Currency && i.OriginalInvoiceId == null && openStates.Contains(i.State))
            .OrderBy(i => i.DueDate).Take(200)
            .Select(i => new { i.InvoiceId, i.InvoiceNumber, i.Total, i.BillingAccountId })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = invoices.Select(i => i.InvoiceId).ToList();
        var cash = await db.Allocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceId)).GroupBy(a => a.InvoiceId)
            .Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, cancellationToken).ConfigureAwait(false);
        var offsets = await db.CreditApplications.AsNoTracking().Where(a => a.TargetInvoiceId != null && ids.Contains(a.TargetInvoiceId.Value))
            .GroupBy(a => a.TargetInvoiceId!.Value).Select(g => new { g.Key, Sum = g.Sum(a => a.Amount) }).ToDictionaryAsync(x => x.Key, x => x.Sum, cancellationToken).ConfigureAwait(false);
        var accountIds = invoices.Select(i => i.BillingAccountId).Distinct().ToList();
        var payers = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.BillingAccountId))
            .ToDictionaryAsync(a => a.BillingAccountId, a => a.PayerPartyId.ToString(), cancellationToken).ConfigureAwait(false);
        var candidates = invoices
            .Select(i => new SuspenseCandidate(i.InvoiceNumber, i.Total - cash.GetValueOrDefault(i.InvoiceId) - offsets.GetValueOrDefault(i.InvoiceId), null, payers.GetValueOrDefault(i.BillingAccountId)))
            .Where(c => c.OpenAmount > 0).ToList();
        return (payment, candidates);
    }
}
