using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Accounting;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Reinsurance.Cession;

/// <summary>JSON shapes of the cession exception and its deltas (the Unspecified contract fields of ri.CessionException.*).</summary>
internal static class CessionExceptionViews
{
    public static JsonElement Exception(CessionExceptionRow e, CessionExceptionStateRow? state, CessionFailure? lastOutcome = null) => Json(new
    {
        exceptionId = e.ExceptionId,
        termId = e.TermId,
        sourceEventId = e.SourceEventId,
        code = e.Code,
        detail = e.Detail,
        recordedAt = e.RecordedAt.ToString(),
        status = state?.Status ?? CessionExceptionStatus.Open,
        assignee = state?.Assignee,
        assignedAt = state?.AssignedAt?.ToString(),
        resolvedAt = state?.ResolvedAt?.ToString(),
        resolvedBy = state?.ResolvedBy,
        resolution = state?.Resolution,
        resolutionReason = state?.ResolutionReason,
        rerunCount = state?.RerunCount ?? 0,
        lastRerunAt = state?.LastRerunAt?.ToString(),
        rerunOutcome = lastOutcome is null ? null : new { code = lastOutcome.Code, detail = lastOutcome.Detail },
    });

    public static JsonElement Deltas(IEnumerable<CessionRow> rows) => Json(rows.Select(c => new
    {
        cessionId = c.CessionId,
        termId = c.TermId,
        treatyId = c.TreatyId,
        contractYear = c.ContractYear,
        runId = c.RunId,
        writtenPremium = Money(c.WrittenPremium),
        target = Money(c.Target),
        bookedBefore = Money(c.BookedBefore),
        delta = Money(c.Delta),
        recordedAt = c.RecordedAt.ToString(),
    }).ToList());

    public static string Money(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    public static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}

/// <summary>Reading the loosely typed request fields (the contract leaves them Unspecified).</summary>
internal static class CessionJson
{
    public static string? String(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static Guid? Guid(JsonElement? element, string name) =>
        System.Guid.TryParse(String(element, name), out var id) && id != System.Guid.Empty ? id : null;

    /// <summary>The reason as plain text: a JSON string.</summary>
    public static string? Text(JsonElement? element) => element is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
}

/// <summary>
/// <c>ri.Cession.preview</c>: the dry run of the cession intake. <c>riskDescription</c> names a <c>policyId</c> or <c>termId</c> (its ledger is replayed against the
/// treaties in force) and may add hypothetical <c>lines</c> (<c>coverageCode</c>, <c>amount</c>, optional <c>chargeCategory</c>, <c>currency</c>, <c>validFrom</c>); nothing is written.
/// </summary>
internal sealed class CessionPreviewService(CessionIntake intake, ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int MaxLines = 50;

    public async Task<Result<CessionPreviewResponse>> PreviewAsync(CessionPreviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var risk = request.RiskDescription;
        var policy = CessionJson.Guid(risk, "policyId");
        var term = CessionJson.Guid(risk, "termId");
        var hypothetical = new List<DeltaLine>();
        if (risk is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("lines", out var lines))
        {
            if (lines.ValueKind != JsonValueKind.Array || lines.GetArrayLength() > MaxLines)
            {
                return RiErrors.Validation("riskDescription.lines", "LINES", $"lines must be a list of at most {MaxLines} premium lines.");
            }

            foreach (var item in lines.EnumerateArray())
            {
                var parsed = ParseLine(item, term ?? Guid.Empty, policy ?? Guid.Empty);
                if (parsed.IsFailure)
                {
                    return parsed.Error!;
                }

                hypothetical.Add(parsed.Value);
            }
        }

        if (policy is null && term is null && hypothetical.Count == 0)
        {
            return RiErrors.Validation("riskDescription", "RISK", "Name a policyId or termId, or give premium lines.");
        }

        var termIds = new List<Guid>();
        if (term is { } t)
        {
            termIds.Add(t);
        }
        else if (policy is { } p)
        {
            termIds.AddRange(await intake.TermsOfPolicyAsync(p, cancellationToken).ConfigureAwait(false));
        }

        if (hypothetical.Count > 0 && termIds.Count == 0)
        {
            termIds.Add(Guid.Empty); // Purely hypothetical risk: no ledger, no booked cession.
        }

        var shares = new List<object>();
        var gaps = new List<object>();
        var seen = new HashSet<(Guid, string, string)>();
        decimal ceded = 0m, booked = 0m, toBook = 0m;
        foreach (var id in termIds)
        {
            var extra = hypothetical.Select(l => l with { TermId = id }).Select(ToRow).ToList();
            foreach (var plan in await intake.PlanTermAsync(id, extra, cancellationToken).ConfigureAwait(false))
            {
                shares.Add(new
                {
                    termId = id == Guid.Empty ? (Guid?)null : id,
                    treatyId = plan.Treaty.TreatyId,
                    contractYear = plan.Treaty.ContractYear,
                    sharePct = plan.Treaty.SharePct.ToString("0.######", CultureInfo.InvariantCulture),
                    writtenPremium = CessionExceptionViews.Money(plan.Written),
                    target = CessionExceptionViews.Money(plan.Target),
                    booked = CessionExceptionViews.Money(plan.Booked),
                    delta = CessionExceptionViews.Money(plan.Delta),
                });
                ceded += plan.Target;
                booked += plan.Booked;
                toBook += plan.Delta;
            }

            // The checks the intake would apply: the ledger lines (no currency) and the hypothetical ones.
            var ledger = await db.PremiumDeltas.AsNoTracking().Where(l => l.TermId == id).ToListAsync(cancellationToken).ConfigureAwait(false);
            var candidates = ledger.Select(l => new DeltaLine(l.EventId, l.PolicyId, l.TermId, l.CoverageCode, l.ChargeCategory, l.Amount, null, l.ValidFrom, l.SetId, l.SetSize, l.CorrectionOf, l.OosSetId))
                .Concat(hypothetical.Select(l => l with { TermId = id }));
            foreach (var line in candidates)
            {
                if ((await intake.CheckAsync(line, cancellationToken).ConfigureAwait(false)).Failure is { } failure && seen.Add((id, line.Coverage, failure.Code)))
                {
                    gaps.Add(new { termId = id == Guid.Empty ? (Guid?)null : id, coverageCode = line.Coverage, code = failure.Code, detail = failure.Detail });
                }
            }
        }

        return new CessionPreviewResponse
        {
            Shares = CessionExceptionViews.Json(shares),
            Capacity = CessionExceptionViews.Json(new { cededTarget = CessionExceptionViews.Money(ceded), booked = CessionExceptionViews.Money(booked), toBook = CessionExceptionViews.Money(toBook), treaties = shares.Count }),
            Gaps = CessionExceptionViews.Json(gaps),
        };
    }

    private static Result<DeltaLine> ParseLine(JsonElement item, Guid term, Guid policy)
    {
        var coverage = CessionJson.String(item, "coverageCode");
        if (string.IsNullOrWhiteSpace(coverage) || coverage.Length > 100)
        {
            return RiErrors.Validation("riskDescription.lines", "COVERAGE", "Each line needs a coverageCode.");
        }

        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("amount", out var amountElement)
            || !decimal.TryParse(amountElement.ValueKind == JsonValueKind.String ? amountElement.GetString() : amountElement.GetRawText(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
        {
            return RiErrors.Validation("riskDescription.lines", "AMOUNT", "Each line needs an amount.");
        }

        var validFrom = CessionJson.String(item, "validFrom");
        if (validFrom is null || !BusinessDate.TryParse(validFrom, out var from))
        {
            return RiErrors.Validation("riskDescription.lines", "VALID_FROM", "Each line needs a validFrom date.");
        }

        var category = CessionJson.String(item, "chargeCategory") ?? "PREMIUM";
        var currency = CessionJson.String(item, "currency") ?? "EUR";
        return new DeltaLine(Guid.CreateVersion7(), policy, term, coverage, category, amount, currency, from, null, 1, null, null);
    }

    private PremiumDeltaRow ToRow(DeltaLine line) => new()
    {
        EventId = line.EventId,
        LegalEntityId = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value,
        PolicyId = line.PolicyId,
        TermId = line.TermId,
        CoverageCode = line.Coverage,
        ChargeCategory = line.Category,
        Amount = line.Amount,
        ValidFrom = line.ValidFrom,
        SetSize = 1,
    };
}

/// <summary>Shared by the rerun commands: the scope of a term, the plan JSON and the audit facts.</summary>
internal static class CessionRerunSupport
{
    public static DomainError NotFound(string what) => DomainError.Of(ModuleCode.RI, "NOT-FOUND", $"The {what} does not exist in your legal entity.");

    public static object PlanView(TermCessionPlan plan, CessionRow? row) => new
    {
        termId = plan.TermId,
        treatyId = plan.Treaty.TreatyId,
        contractYear = plan.Treaty.ContractYear,
        writtenPremium = CessionExceptionViews.Money(plan.Written),
        target = CessionExceptionViews.Money(plan.Target),
        booked = CessionExceptionViews.Money(plan.Booked),
        delta = CessionExceptionViews.Money(plan.Delta),
        cessionId = row?.CessionId,
    };

    public static CommandAuditFacts Facts(string kind, string id, object before, object after, bool ok) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.RI, kind, id),
        BusinessKeys = BusinessKeys.Empty.With(kind == "Cession" ? "policyOrTerm" : "exceptionId", id),
        Changes = ok ? AuditDiff.Compute(before, after) : AuditDiff.Compute(before, before),
    };
}

/// <summary>ri.Cession.rerun: replays one policy (or one term) and writes only the differences between target and booked.</summary>
internal sealed record RerunCession(CessionRerunRequest Request) : ICommand<CessionRerunResponse>;

/// <summary>Shape rules: a policyId or termId scope and a reason.</summary>
internal sealed class RerunCessionValidator : AbstractValidator<RerunCession>
{
    public RerunCessionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Reason).Must(r => CessionExceptionCommands.ReasonOk(r, true)).WithErrorCode("REASON").When(c => c.Request is not null);
        RuleFor(c => c.Request.Scope).Must(s => CessionJson.Guid(s, "policyId") is not null ^ CessionJson.Guid(s, "termId") is not null).WithErrorCode("SCOPE").When(c => c.Request is not null)
            .WithMessage("The scope names exactly one of policyId and termId.");
    }
}

/// <summary>Replay under the term locks; a second rerun is a no-op because target equals booked.</summary>
internal sealed class RerunCessionHandler(CessionIntake intake, ReinsuranceDbContext db, RequestContext context, ILegalEntityDirectory legalEntities)
    : ICommandHandler<RerunCession, CessionRerunResponse>
{
    public async Task<Result<CessionRerunResponse>> HandleAsync(RerunCession command, CancellationToken cancellationToken)
    {
        var scope = command.Request.Scope;
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        IReadOnlyList<Guid> terms;
        if (CessionJson.Guid(scope, "termId") is { } term)
        {
            terms = await db.PremiumDeltas.AnyAsync(p => p.LegalEntityId == legalEntity && p.TermId == term, cancellationToken).ConfigureAwait(false) ? [term] : [];
        }
        else
        {
            terms = await intake.TermsOfPolicyAsync(CessionJson.Guid(scope, "policyId")!.Value, cancellationToken).ConfigureAwait(false);
        }

        if (terms.Count == 0)
        {
            return CessionRerunSupport.NotFound("policy or term");
        }

        var runId = Guid.CreateVersion7();
        var views = new List<object>();
        foreach (var termId in terms)
        {
            views.AddRange((await intake.RerunTermAsync(termId, runId, null, cancellationToken).ConfigureAwait(false)).Select(r => CessionRerunSupport.PlanView(r.Plan, r.Row)));
        }

        return new CessionRerunResponse { Preview = CessionExceptionViews.Json(views), RunId = runId };
    }
}

/// <summary>Audit facts of <c>ri.Cession.rerun</c>.</summary>
internal sealed class RerunCessionAuditor : ICommandAuditor<RerunCession, CessionRerunResponse>
{
    public CommandAuditFacts Describe(RerunCession command, Result<CessionRerunResponse>? result) =>
        CessionRerunSupport.Facts("Cession", (CessionJson.Guid(command.Request.Scope, "policyId") ?? CessionJson.Guid(command.Request.Scope, "termId") ?? Guid.Empty).ToString("D"),
            new { run = (Guid?)null }, new { run = result?.Value.RunId, reason = CessionJson.Text(command.Request.Reason) }, result is { IsSuccess: true });
}

/// <summary>The three exception commands share one request shape: <c>filters.exceptionId</c>, <c>action</c>, <c>reason</c>.</summary>
internal static class CessionExceptionCommands
{
    public static Guid? ExceptionId(JsonElement? filters) => CessionJson.Guid(filters, "exceptionId");

    public static async Task<(CessionExceptionRow? Row, CessionExceptionStateRow State)> LoadAsync(
        ReinsuranceDbContext db, Guid legalEntity, Guid exceptionId, CancellationToken cancellationToken)
    {
        var row = await db.CessionExceptions.AsNoTracking().FirstOrDefaultAsync(e => e.ExceptionId == exceptionId && e.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return (null, null!);
        }

        // Serialise the workflow of one exception; the state row is created on first touch.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended({0}, 0))", [$"ri:cession-exception:{exceptionId:D}"], cancellationToken).ConfigureAwait(false);
        var state = await db.CessionExceptionStates.FirstOrDefaultAsync(s => s.ExceptionId == exceptionId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            state = new CessionExceptionStateRow { ExceptionId = exceptionId, LegalEntityId = legalEntity };
            db.CessionExceptionStates.Add(state);
        }

        return (row, state);
    }

    public static bool ReasonOk(JsonElement? reason, bool required)
    {
        if (reason is null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return !required;
        }

        return reason is { ValueKind: JsonValueKind.String } text && !string.IsNullOrWhiteSpace(text.GetString()) && text.GetString()!.Length <= 1000;
    }

    public static DomainError Resolved() => AccountingGuards.State("The exception is already resolved; it cannot be assigned, resolved or rerun again.");

    public static CommandAuditFacts Facts(Guid? id, object before, object after, bool ok) =>
        CessionRerunSupport.Facts("CessionException", (id ?? Guid.Empty).ToString("D"), before, after, ok);
}

/// <summary>ri.CessionException.assign: names who works the exception.</summary>
internal sealed record AssignCessionException(CessionExceptionAssignRequest Request) : ICommand<CessionExceptionAssignResponse>;

/// <summary>Shape rules: the exception id and an assignee.</summary>
internal sealed class AssignCessionExceptionValidator : AbstractValidator<AssignCessionException>
{
    public AssignCessionExceptionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Filters).Must(f => CessionExceptionCommands.ExceptionId(f) is not null).WithErrorCode("EXCEPTION_ID").When(c => c.Request is not null);
        RuleFor(c => c.Request.Action).Must(a => CessionJson.String(a, "assignee") is { } who && !string.IsNullOrWhiteSpace(who) && who.Length <= 200).WithErrorCode("ASSIGNEE").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).Must(r => CessionExceptionCommands.ReasonOk(r, false)).WithErrorCode("REASON").When(c => c.Request is not null);
    }
}

/// <summary>Assign (or reassign) an open exception.</summary>
internal sealed class AssignCessionExceptionHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<AssignCessionException, CessionExceptionAssignResponse>
{
    public async Task<Result<CessionExceptionAssignResponse>> HandleAsync(AssignCessionException command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var (row, state) = await CessionExceptionCommands.LoadAsync(db, legalEntity, CessionExceptionCommands.ExceptionId(command.Request.Filters)!.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CessionRerunSupport.NotFound("cession exception");
        }

        if (state.Status == CessionExceptionStatus.Resolved)
        {
            return CessionExceptionCommands.Resolved();
        }

        state.Status = CessionExceptionStatus.Assigned;
        state.Assignee = CessionJson.String(command.Request.Action, "assignee")!.Trim();
        state.AssignedAt = clock.Now;
        state.AssignedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var deltas = await db.Cessions.AsNoTracking().Where(c => c.ExceptionId == row.ExceptionId).OrderBy(c => c.CessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CessionExceptionAssignResponse { Exception = CessionExceptionViews.Exception(row, state), Deltas = CessionExceptionViews.Deltas(deltas) };
    }
}

/// <summary>Audit facts of <c>ri.CessionException.assign</c>.</summary>
internal sealed class AssignCessionExceptionAuditor : ICommandAuditor<AssignCessionException, CessionExceptionAssignResponse>
{
    public CommandAuditFacts Describe(AssignCessionException command, Result<CessionExceptionAssignResponse>? result) =>
        CessionExceptionCommands.Facts(CessionExceptionCommands.ExceptionId(command.Request.Filters), new { assignee = (string?)null }, new { assignee = CessionJson.String(command.Request.Action, "assignee") }, result is { IsSuccess: true });
}

/// <summary>ri.CessionException.resolve: closes the exception by hand; a reason is required.</summary>
internal sealed record ResolveCessionException(CessionExceptionResolveRequest Request) : ICommand<CessionExceptionResolveResponse>;

/// <summary>Shape rules: the exception id and a reason.</summary>
internal sealed class ResolveCessionExceptionValidator : AbstractValidator<ResolveCessionException>
{
    public ResolveCessionExceptionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Filters).Must(f => CessionExceptionCommands.ExceptionId(f) is not null).WithErrorCode("EXCEPTION_ID").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).Must(r => CessionExceptionCommands.ReasonOk(r, true)).WithErrorCode("REASON").When(c => c.Request is not null);
    }
}

/// <summary>Resolve an open or assigned exception.</summary>
internal sealed class ResolveCessionExceptionHandler(ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<ResolveCessionException, CessionExceptionResolveResponse>
{
    public async Task<Result<CessionExceptionResolveResponse>> HandleAsync(ResolveCessionException command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var (row, state) = await CessionExceptionCommands.LoadAsync(db, legalEntity, CessionExceptionCommands.ExceptionId(command.Request.Filters)!.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CessionRerunSupport.NotFound("cession exception");
        }

        if (state.Status == CessionExceptionStatus.Resolved)
        {
            return CessionExceptionCommands.Resolved();
        }

        state.Status = CessionExceptionStatus.Resolved;
        state.Resolution = "MANUAL";
        state.ResolutionReason = CessionJson.Text(command.Request.Reason)!.Trim();
        state.ResolvedAt = clock.Now;
        state.ResolvedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var deltas = await db.Cessions.AsNoTracking().Where(c => c.ExceptionId == row.ExceptionId).OrderBy(c => c.CessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CessionExceptionResolveResponse { Exception = CessionExceptionViews.Exception(row, state), Deltas = CessionExceptionViews.Deltas(deltas) };
    }
}

/// <summary>Audit facts of <c>ri.CessionException.resolve</c>.</summary>
internal sealed class ResolveCessionExceptionAuditor : ICommandAuditor<ResolveCessionException, CessionExceptionResolveResponse>
{
    public CommandAuditFacts Describe(ResolveCessionException command, Result<CessionExceptionResolveResponse>? result) =>
        CessionExceptionCommands.Facts(CessionExceptionCommands.ExceptionId(command.Request.Filters), new { status = "OPEN" }, new { status = "RESOLVED", reason = CessionJson.Text(command.Request.Reason) }, result is { IsSuccess: true });
}

/// <summary>ri.CessionException.rerun: replays the held premium line; a clean replay resolves the exception, a failing one leaves it as it was.</summary>
internal sealed record RerunCessionException(CessionExceptionRerunRequest Request) : ICommand<CessionExceptionRerunResponse>;

/// <summary>Shape rules: the exception id and an optional reason.</summary>
internal sealed class RerunCessionExceptionValidator : AbstractValidator<RerunCessionException>
{
    public RerunCessionExceptionValidator()
    {
        RuleFor(c => c.Request).NotNull();
        RuleFor(c => c.Request.Filters).Must(f => CessionExceptionCommands.ExceptionId(f) is not null).WithErrorCode("EXCEPTION_ID").When(c => c.Request is not null);
        RuleFor(c => c.Request.Reason).Must(r => CessionExceptionCommands.ReasonOk(r, false)).WithErrorCode("REASON").When(c => c.Request is not null);
    }
}

/// <summary>Rerun one exception.</summary>
internal sealed class RerunCessionExceptionHandler(CessionIntake intake, ReinsuranceDbContext db, RequestContext context, IClock clock, ILegalEntityDirectory legalEntities)
    : ICommandHandler<RerunCessionException, CessionExceptionRerunResponse>
{
    public async Task<Result<CessionExceptionRerunResponse>> HandleAsync(RerunCessionException command, CancellationToken cancellationToken)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value;
        var (row, state) = await CessionExceptionCommands.LoadAsync(db, legalEntity, CessionExceptionCommands.ExceptionId(command.Request.Filters)!.Value, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return CessionRerunSupport.NotFound("cession exception");
        }

        if (state.Status == CessionExceptionStatus.Resolved)
        {
            return CessionExceptionCommands.Resolved();
        }

        var now = clock.Now;
        var (failure, _) = await intake.RerunExceptionAsync(row, Guid.CreateVersion7(), cancellationToken).ConfigureAwait(false);
        state.RerunCount++;
        state.LastRerunAt = now;
        if (failure is null)
        {
            state.Status = CessionExceptionStatus.Resolved;
            state.Resolution = "RERUN";
            state.ResolutionReason = CessionJson.Text(command.Request.Reason)?.Trim() ?? "Rerun cleared the exception.";
            state.ResolvedAt = now;
            state.ResolvedBy = context.Actor.ToString();
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var deltas = await db.Cessions.AsNoTracking().Where(c => c.ExceptionId == row.ExceptionId).OrderBy(c => c.CessionId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CessionExceptionRerunResponse { Exception = CessionExceptionViews.Exception(row, state, failure), Deltas = CessionExceptionViews.Deltas(deltas) };
    }
}

/// <summary>Audit facts of <c>ri.CessionException.rerun</c>.</summary>
internal sealed class RerunCessionExceptionAuditor : ICommandAuditor<RerunCessionException, CessionExceptionRerunResponse>
{
    public CommandAuditFacts Describe(RerunCessionException command, Result<CessionExceptionRerunResponse>? result) =>
        CessionExceptionCommands.Facts(CessionExceptionCommands.ExceptionId(command.Request.Filters), new { rerun = false }, new { rerun = true, reason = CessionJson.Text(command.Request.Reason) }, result is { IsSuccess: true });
}
