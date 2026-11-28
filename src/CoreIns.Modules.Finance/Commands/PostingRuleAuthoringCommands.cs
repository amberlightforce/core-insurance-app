using System.Text.Json;
using CoreIns.Modules.Finance.Domain;
using CoreIns.Modules.Finance.Persistence;
using CoreIns.Modules.Finance.Posting;
using CoreIns.Platform.Authority;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Finance.Commands;

internal sealed record DraftPostingRules(PostingRulesAuthoringInput Request) : ICommand<PostingRulesAuthoringResult>;

internal sealed record CompilePostingRules(PostingRulesAuthoringInput Request) : ICommand<PostingRulesAuthoringResult>;

internal sealed record TestPostingRules(PostingRulesAuthoringInput Request) : ICommand<PostingRulesAuthoringResult>;

internal sealed record PreviewPostingRuleImpact(PostingRulesAuthoringInput Request) : ICommand<PostingRulesAuthoringResult>;

internal sealed record SubmitPostingRules(PostingRulesSubmitInput Request) : ICommand<PostingRulesAuthoringResult>;

internal sealed record DecidePostingRules(PostingRulesDecideInput Request) : ICommand<PostingRulesAuthoringResult>;

/// <summary>
/// The maker-checker binding of a posting-rule draft to <c>plt.Approval</c> (BO-FIN-07): approval type and authority type
/// <c>FIN.POSTING_RULE_SET</c> (no amount: the holder of the grant may approve any rule set), the subject, and the content hash of the draft.
/// </summary>
internal static class PostingRuleApproval
{
    public const string Type = "FIN.POSTING_RULE_SET";

    /// <summary>The inbox role a referred request lands in (illustrative).</summary>
    public const string ReferralRole = "Platform.Admin";

    public static AuthorityTypeCode Authority { get; } = AuthorityTypeCode.Parse(Type);

    public static AuthorityTypeDefinition Definition { get; } = new(
        Authority, ModuleCode.FIN, new LocalizedText("Έγκριση κανόνων καταχώρισης", "Posting rule set approval"), []);

    public static ObjectRef Subject(Guid id) => new(ModuleCode.FIN, "PostingRuleDraft", id.ToString("D"));
}

/// <summary>A draft (stored or inline) resolved for one authoring call.</summary>
internal sealed record PostingRuleWorkspace(
    PostingRuleDraftRow? Draft, LegalEntityId LegalEntity, string LegalEntityCode, string Book, BusinessDate EffectiveFrom,
    IReadOnlyList<PostingRule> Rules, IReadOnlyList<SampleEventInput> Samples);

/// <summary>Shared authoring services: resolving the draft, compile checks against the chart, golden cases and the replay (all read-only).</summary>
internal sealed class PostingRuleAuthoringService(
    FinanceDbContext db, ReferenceData reference, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IOptions<FinanceOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BusinessDate Today => clock.Now.ToBusinessDate(options.Value.Zone);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static IReadOnlyList<PostingRule> RulesOf(PostingRuleDraftRow row) =>
        PostingRuleAuthoring.ParseRules(JsonSerializer.Deserialize<List<RuleRowInput>>(row.Rules, Json)).Value;

    public static IReadOnlyList<SampleEventInput> SamplesOf(PostingRuleDraftRow row) =>
        PostingRuleAuthoring.ParseSamples(JsonSerializer.Deserialize<List<SampleEventInput>>(row.SampleEvents, Json)).Value;

    public static PostingRuleDraftView View(PostingRuleDraftRow r) => new(
        r.DraftId, r.Book, r.EffectiveFrom.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), r.Status, r.BaseVersion, r.ContentHash,
        JsonSerializer.Deserialize<List<RuleRowInput>>(r.Rules, Json)?.Count ?? 0, r.RecordVersion, r.CreatedBy, r.SubmittedBy, r.DecidedBy, r.DecisionReason,
        r.ActivatedRuleSetId, r.ActivatedVersion);

    public (LegalEntityId Id, LegalEntityCode Code)? Scope() => ManualJournalScope.Of(context, legalEntities);

    public async Task<PostingRuleDraftRow?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Scope() is { } scope
            ? await db.PostingRuleDrafts.SingleOrDefaultAsync(r => r.DraftId == id && r.LegalEntityId == scope.Id.Value, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Resolves the draft named by the request, or the inline rule set; rows and samples not sent default to the stored draft's.</summary>
    public async Task<Result<PostingRuleWorkspace>> LoadAsync(PostingRulesAuthoringInput? input, bool requireDate, CancellationToken cancellationToken)
    {
        if (input is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A request body is required.");
        }

        if (Scope() is not (var legalEntity, var code))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The legal entity is not served by this stamp.");
        }

        PostingRuleDraftRow? row = null;
        if (input.RuleSet?.DraftId is { } draftId)
        {
            row = await FindAsync(draftId, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The posting rule draft does not exist.");
            }
        }

        var book = string.IsNullOrWhiteSpace(input.RuleSet?.Book) ? row?.Book : input.RuleSet!.Book!.Trim();
        if (book is null || (row is not null && !string.Equals(book, row.Book, StringComparison.Ordinal)))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "ruleSet.book is required and cannot change on an existing draft.");
        }

        var profile = await reference.BookProfileAsync(code.Value, cancellationToken).ConfigureAwait(false);
        if (profile is null || !profile.ActiveBooks.Contains(book, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", $"The book '{book}' is not an active book of the legal entity.");
        }

        BusinessDate effective;
        if (input.RuleSet?.EffectiveFrom is { } text)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", "ruleSet.effectiveFrom must be a date (yyyy-MM-dd).");
            }

            effective = new BusinessDate(parsed);
        }
        else if (row is not null)
        {
            effective = row.EffectiveFrom;
        }
        else if (requireDate)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "ruleSet.effectiveFrom is required.");
        }
        else
        {
            effective = Today;
        }

        IReadOnlyList<PostingRule> rules;
        if (input.RuleRows is null && row is not null)
        {
            rules = RulesOf(row);
        }
        else
        {
            var parsed = PostingRuleAuthoring.ParseRules(input.RuleRows);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            rules = parsed.Value;
        }

        IReadOnlyList<SampleEventInput> samples;
        if (input.SampleEvents is null && row is not null)
        {
            samples = SamplesOf(row);
        }
        else
        {
            var parsed = PostingRuleAuthoring.ParseSamples(input.SampleEvents);
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            samples = parsed.Value;
        }

        return new PostingRuleWorkspace(row, legalEntity, code.Value, book, effective, rules, samples);
    }

    /// <summary>A workspace of a stored draft as it is now (used when deciding).</summary>
    public static PostingRuleWorkspace Of(PostingRuleDraftRow row) =>
        new(row, new LegalEntityId(row.LegalEntityId), row.LegalEntityCode, row.Book, row.EffectiveFrom, RulesOf(row), SamplesOf(row));

    public async Task<IReadOnlyDictionary<string, string>> DerivationsAsync(PostingRuleWorkspace w, CancellationToken cancellationToken)
    {
        var rows = await db.Derivations.AsNoTracking().Where(d => d.LegalEntityCode == w.LegalEntityCode && d.Book == w.Book).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Where(d => d.ValidFrom.Value <= w.EffectiveFrom.Value && (d.ValidTo is null || d.ValidTo.Value.Value > w.EffectiveFrom.Value))
            .GroupBy(d => d.GlKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().AccountCode, StringComparer.Ordinal);
    }

    /// <summary>
    /// Compile checks (REQ-FIN-053): the domain checks of <see cref="PostingRules.Compile"/> against the book's active chart and derivations, every
    /// source event a posting event of the finance catalogue, and a warning for each key the set in force covers that the draft drops.
    /// </summary>
    public async Task<PostingRuleCompileResult> CompileAsync(PostingRuleWorkspace w, CancellationToken cancellationToken)
    {
        var chart = await db.Accounts.AsNoTracking().Where(a => a.LegalEntityCode == w.LegalEntityCode && a.Book == w.Book).ToListAsync(cancellationToken).ConfigureAwait(false);
        var active = chart.Where(a => a.Status == "ACTIVE").Select(a => a.AccountCode).ToHashSet(StringComparer.Ordinal);
        var derivations = await DerivationsAsync(w, cancellationToken).ConfigureAwait(false);
        var errors = PostingRules.Compile(w.Rules, active, derivations).ToList();

        var catalogue = await db.Catalogue.AsNoTracking().Where(c => c.Relevance == Relevance.Posting).Select(c => c.RegistryName).ToListAsync(cancellationToken).ConfigureAwait(false);
        var known = catalogue.ToHashSet(StringComparer.Ordinal);
        foreach (var rule in w.Rules.Where(r => !known.Contains(r.SourceEvent)))
        {
            errors.Add($"Rule {rule.Code} names source event {rule.SourceEvent}, which is not a posting event of the finance catalogue.");
        }

        var warnings = new List<string>();
        if (w.EffectiveFrom.Value < Today.Value)
        {
            warnings.Add($"The effective date {w.EffectiveFrom.Value:yyyy-MM-dd} is in the past; a set can only be submitted with an effective date of today or later.");
        }

        var profile = await reference.BookProfileAsync(w.LegalEntityCode, cancellationToken).ConfigureAwait(false);
        var inForce = profile is null ? null : await reference.BookSetupAsync(profile, w.Book, w.EffectiveFrom, cancellationToken).ConfigureAwait(false);
        if (inForce is not null)
        {
            var covered = w.Rules.Select(r => (r.SourceEvent, r.EntryType, r.SourceAccount)).ToHashSet();
            foreach (var key in inForce.RuleSet.Rules.Select(r => (r.SourceEvent, r.EntryType, r.SourceAccount)).Distinct().Where(k => !covered.Contains(k)))
            {
                warnings.Add($"The set in force (version {inForce.RuleSet.Version}) covers {key.SourceEvent} {key.EntryType} on {key.SourceAccount}; the draft has no rule for it, so such lines become intake exceptions.");
            }
        }

        return new PostingRuleCompileResult(errors.Count == 0, errors, warnings);
    }

    public async Task<int?> BaseVersionAsync(PostingRuleWorkspace w, CancellationToken cancellationToken)
    {
        var profile = await reference.BookProfileAsync(w.LegalEntityCode, cancellationToken).ConfigureAwait(false);
        var setup = profile is null ? null : await reference.BookSetupAsync(profile, w.Book, w.EffectiveFrom, cancellationToken).ConfigureAwait(false);
        return setup?.RuleSet.Version;
    }

    /// <summary>Replays the book's most recent event-posted journals through the draft's rules. Reads only; nothing is posted or written.</summary>
    public async Task<PostingRuleImpactResult> PreviewAsync(PostingRuleWorkspace w, int? limit, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(limit ?? 200, 1, 1000);
        var derivations = await DerivationsAsync(w, cancellationToken).ConfigureAwait(false);
        var journals = await db.Journals.AsNoTracking()
            .Where(j => j.LegalEntityId == w.LegalEntity.Value && j.Book == w.Book && j.SourceType == SourceTypes.Event)
            .OrderByDescending(j => j.PostedAt).Take(take)
            .Select(j => new { j.JournalId, j.JournalNumber, j.RuleSetId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = journals.Select(j => j.JournalId).ToList();
        var lines = await db.JournalLines.AsNoTracking().Where(l => ids.Contains(l.JournalId))
            .Select(l => new { l.JournalId, l.LineNo, l.RuleCode, l.ChargeCategory, l.ChargeType, l.GlKey, l.AccountCode }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var setIds = journals.Select(j => j.RuleSetId).Distinct().ToList();
        var original = (await db.Rules.AsNoTracking().Where(r => setIds.Contains(r.RuleSetId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => (r.RuleSetId, r.RuleCode), r => new PostingRule(
                r.RuleCode, r.SourceEvent, r.EntryType, r.SourceAccount, r.ChargeCategory, r.ChargeType, r.AccountCode, r.DeriveFrom, r.DescriptionEl, r.DescriptionEn));

        var replay = new List<ReplayLine>();
        var skipped = 0;
        var byId = journals.ToDictionary(j => j.JournalId);
        foreach (var line in lines.OrderBy(l => byId[l.JournalId].JournalNumber, StringComparer.Ordinal).ThenBy(l => l.LineNo))
        {
            var journal = byId[line.JournalId];
            if (!original.TryGetValue((journal.RuleSetId, line.RuleCode), out var rule))
            {
                skipped++;
                continue;
            }

            replay.Add(new ReplayLine(journal.JournalNumber, line.LineNo, rule, line.ChargeCategory, line.ChargeType, line.GlKey, line.AccountCode));
        }

        return PostingRuleAuthoring.Replay(w.Rules, derivations, replay, journals.Count, skipped);
    }

    public static DomainError CompileError(PostingRuleCompileResult compile) =>
        DomainError.Of(ModuleCode.FIN, "COMPILE", string.Join(" ", compile.Errors.Take(5)) + (compile.Errors.Count > 5 ? $" (+{compile.Errors.Count - 5} more)" : string.Empty));
}

/// <summary><c>fin.PostingRules.draft</c>: stores (or replaces, by its maker, while DRAFT) a rule-set proposal. Nothing is activated; the draft may be incomplete.</summary>
internal sealed class DraftPostingRulesHandler(PostingRuleAuthoringService authoring, FinanceDbContext db, RequestContext context, IClock clock)
    : ICommandHandler<DraftPostingRules, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(DraftPostingRules command, CancellationToken cancellationToken)
    {
        var loaded = await authoring.LoadAsync(command.Request, requireDate: true, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var w = loaded.Value;
        var actor = context.Actor.Id;
        var row = w.Draft;
        if (row is not null)
        {
            if (row.Status != PostingRuleDraftStatus.Draft)
            {
                return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} posting rule draft cannot be edited; start a new draft.");
            }

            if (!string.Equals(row.CreatedBy, actor, StringComparison.Ordinal))
            {
                return DomainError.Of(ModuleCode.FIN, "SOD", "Only the maker of a posting rule draft can edit it.");
            }
        }
        else
        {
            row = new PostingRuleDraftRow
            {
                DraftId = Guid.CreateVersion7(), LegalEntityId = w.LegalEntity.Value, LegalEntityCode = w.LegalEntityCode, Book = w.Book,
                Status = PostingRuleDraftStatus.Draft, CreatedBy = actor, CreatedAt = clock.Now, RecordVersion = 0,
            };
            db.PostingRuleDrafts.Add(row);
        }

        row.EffectiveFrom = w.EffectiveFrom;
        row.Rules = PostingRuleAuthoringService.Serialize(w.Rules.Select(PostingRuleAuthoring.ToInput));
        row.SampleEvents = PostingRuleAuthoringService.Serialize(w.Samples);
        row.ContentHash = PostingRuleAuthoring.Hash(w.Book, w.EffectiveFrom, w.Rules).Value;
        row.BaseVersion = await authoring.BaseVersionAsync(w, cancellationToken).ConfigureAwait(false);
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The draft changed meanwhile; load it and try again.");
        }

        var compile = await authoring.CompileAsync(w, cancellationToken).ConfigureAwait(false);
        var golden = PostingRuleAuthoring.Golden(w.Rules, await authoring.DerivationsAsync(w, cancellationToken).ConfigureAwait(false), w.Samples);
        return new PostingRulesAuthoringResult(PostingRuleAuthoringService.View(row), compile, golden, null);
    }
}

/// <summary><c>fin.PostingRules.compile</c>: the compile checks of a stored draft or an inline set. Writes nothing.</summary>
internal sealed class CompilePostingRulesHandler(PostingRuleAuthoringService authoring) : ICommandHandler<CompilePostingRules, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(CompilePostingRules command, CancellationToken cancellationToken)
    {
        var loaded = await authoring.LoadAsync(command.Request, requireDate: false, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var compile = await authoring.CompileAsync(loaded.Value, cancellationToken).ConfigureAwait(false);
        return new PostingRulesAuthoringResult(loaded.Value.Draft is null ? null : PostingRuleAuthoringService.View(loaded.Value.Draft), compile, null, null);
    }
}

/// <summary><c>fin.PostingRules.test</c> (dry run, always side-effect free): compile checks plus the sample events resolved against the rules.</summary>
internal sealed class TestPostingRulesHandler(PostingRuleAuthoringService authoring) : ICommandHandler<TestPostingRules, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(TestPostingRules command, CancellationToken cancellationToken)
    {
        var loaded = await authoring.LoadAsync(command.Request, requireDate: false, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var w = loaded.Value;
        var compile = await authoring.CompileAsync(w, cancellationToken).ConfigureAwait(false);
        var golden = PostingRuleAuthoring.Golden(w.Rules, await authoring.DerivationsAsync(w, cancellationToken).ConfigureAwait(false), w.Samples);
        return new PostingRulesAuthoringResult(w.Draft is null ? null : PostingRuleAuthoringService.View(w.Draft), compile, golden, null);
    }
}

/// <summary><c>fin.PostingRules.previewImpact</c> (dry run, always side-effect free): replays the book's recent posted lines through the proposed rules.</summary>
internal sealed class PreviewPostingRuleImpactHandler(PostingRuleAuthoringService authoring) : ICommandHandler<PreviewPostingRuleImpact, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(PreviewPostingRuleImpact command, CancellationToken cancellationToken)
    {
        var loaded = await authoring.LoadAsync(command.Request, requireDate: false, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return loaded.Error;
        }

        var w = loaded.Value;
        var compile = await authoring.CompileAsync(w, cancellationToken).ConfigureAwait(false);
        if (!compile.Ok)
        {
            return PostingRuleAuthoringService.CompileError(compile);
        }

        var preview = await authoring.PreviewAsync(w, command.Request.Limit, cancellationToken).ConfigureAwait(false);
        return new PostingRulesAuthoringResult(w.Draft is null ? null : PostingRuleAuthoringService.View(w.Draft), compile, null, preview);
    }
}

/// <summary>
/// <c>fin.PostingRules.submit</c>: the maker sends a DRAFT for approval. It must compile, pass its sample events and take effect today or later.
/// FIN requests a <c>plt.Approval</c> (in process) for the content hash; one set per book waits for a decision at a time.
/// </summary>
internal sealed class SubmitPostingRulesHandler(PostingRuleAuthoringService authoring, FinanceDbContext db, RequestContext context, IClock clock, IPlatformApprovalService approvals)
    : ICommandHandler<SubmitPostingRules, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(SubmitPostingRules command, CancellationToken cancellationToken)
    {
        if (command.Request?.DraftId is not { } id)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "draftId is required.");
        }

        var row = await authoring.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The posting rule draft does not exist.");
        }

        var actor = context.Actor.Id;
        if (!string.Equals(row.CreatedBy, actor, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "Only the maker of a posting rule draft can submit it.");
        }

        if (row.Status != PostingRuleDraftStatus.Draft)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} posting rule draft cannot be submitted.");
        }

        if (row.EffectiveFrom.Value < authoring.Today.Value)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A rule set can only take effect today or later; change the effective date of the draft.");
        }

        if (await db.PostingRuleDrafts.AnyAsync(r => r.LegalEntityId == row.LegalEntityId && r.Book == row.Book && r.Status == PostingRuleDraftStatus.Submitted, cancellationToken).ConfigureAwait(false))
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"Another rule set of book {row.Book} already waits for a decision.");
        }

        var w = PostingRuleAuthoringService.Of(row);
        var compile = await authoring.CompileAsync(w, cancellationToken).ConfigureAwait(false);
        if (!compile.Ok)
        {
            return PostingRuleAuthoringService.CompileError(compile);
        }

        var golden = PostingRuleAuthoring.Golden(w.Rules, await authoring.DerivationsAsync(w, cancellationToken).ConfigureAwait(false), w.Samples);
        if (golden.Failed > 0)
        {
            var first = golden.Cases.First(c => !c.Passed);
            return DomainError.Of(ModuleCode.FIN, "GOLDEN", $"{golden.Failed} of {golden.Total} sample events fail; first: {first.Name}: {first.Detail}");
        }

        try
        {
            var response = await approvals.RequestAsync(
                new ApprovalRequestRequest
                {
                    Type = PostingRuleApproval.Type,
                    ObjectRef = PostingRuleApproval.Subject(id),
                    PayloadHash = Sha256Hash.Parse(row.ContentHash),
                    Authority = new ApprovalAuthority { Type = PostingRuleApproval.Type },
                    ReferralRole = PostingRuleApproval.ReferralRole,
                    Reason = $"Posting rule set of book {row.Book} effective {row.EffectiveFrom.Value:yyyy-MM-dd} ({w.Rules.Count} rules)",
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            row.ApprovalRequestId = response.Request.RequestId;
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.Status = PostingRuleDraftStatus.Submitted;
        row.SubmittedBy = actor;
        row.SubmittedAt = clock.Now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The draft changed meanwhile; load it and try again.");
        }

        return new PostingRulesAuthoringResult(PostingRuleAuthoringService.View(row), compile, golden, null);
    }
}

/// <summary>
/// <c>fin.PostingRules.decide</c>: a checker approves or rejects a SUBMITTED draft. FIN refuses the maker, <c>plt.Approval.decide</c> checks a human
/// checker with the <c>FIN.POSTING_RULE_SET</c> grant, FIN recomputes the content hash from the stored draft and compiles it again. Approval appends
/// a new immutable rule-set version effective from the draft's date: the set in force before that date is untouched (intake picks the newest version
/// effective on the accounting date), and the shipped set is never rewritten. A repeated decision returns the current state.
/// </summary>
internal sealed class DecidePostingRulesHandler(
    PostingRuleAuthoringService authoring, FinanceDbContext db, RequestContext context, IClock clock, IPlatformApprovalService approvals)
    : ICommandHandler<DecidePostingRules, PostingRulesAuthoringResult>
{
    public async Task<Result<PostingRulesAuthoringResult>> HandleAsync(DecidePostingRules command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request?.DraftId is not { } id)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "draftId is required.");
        }

        var decision = request.Decision?.Trim().ToUpperInvariant();
        if (decision is not ("APPROVE" or "REJECT"))
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "decision must be APPROVE or REJECT.");
        }

        var approve = decision == "APPROVE";
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (!approve && comment is null)
        {
            return DomainError.Of(ModuleCode.FIN, "VALIDATION", "A rejection needs a comment.");
        }

        var row = await authoring.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return DomainError.Of(ModuleCode.FIN, "NOT-FOUND", "The posting rule draft does not exist.");
        }

        if ((approve && row.Status == PostingRuleDraftStatus.Approved) || (!approve && row.Status == PostingRuleDraftStatus.Rejected))
        {
            return new PostingRulesAuthoringResult(PostingRuleAuthoringService.View(row), null, null, null);
        }

        if (row.Status != PostingRuleDraftStatus.Submitted || row.ApprovalRequestId is not { } approvalRequestId)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", $"A {row.Status} posting rule draft cannot be decided.");
        }

        var checker = context.Actor.Id;
        if (string.Equals(row.CreatedBy, checker, StringComparison.Ordinal) || string.Equals(row.SubmittedBy, checker, StringComparison.Ordinal))
        {
            return DomainError.Of(ModuleCode.FIN, "SOD", "The maker of a posting rule draft cannot decide it (maker is not checker).");
        }

        var w = PostingRuleAuthoringService.Of(row);
        var hash = PostingRuleAuthoring.Hash(row.Book, row.EffectiveFrom, w.Rules);
        if (hash.Value != row.ContentHash)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The stored draft no longer matches its content hash.");
        }

        PostingRuleCompileResult? compile = null;
        if (approve)
        {
            // Everything that can refuse the activation is checked before the approval is decided, so a refusal leaves the request open.
            if (row.EffectiveFrom.Value < authoring.Today.Value)
            {
                return DomainError.Of(ModuleCode.FIN, "VALIDATION", "The effective date of the draft has passed; the maker must submit a new draft with a later date.");
            }

            compile = await authoring.CompileAsync(w, cancellationToken).ConfigureAwait(false);
            if (!compile.Ok)
            {
                return PostingRuleAuthoringService.CompileError(compile);
            }
        }

        try
        {
            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = approvalRequestId,
                    PayloadHash = hash,
                    Decision = approve ? ApprovalDecideRequest.DecisionValue.Approve : ApprovalDecideRequest.DecisionValue.Reject,
                    Comment = comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
            if (approve)
            {
                var verified = await approvals.VerifyForExecutionAsync(
                    new ApprovalVerifyForExecutionRequest { RequestId = approvalRequestId, Hash = hash, Type = PostingRuleApproval.Type, ObjectRef = PostingRuleApproval.Subject(id) },
                    cancellationToken).ConfigureAwait(false);
                if (!verified.Ok || verified.Authority.Type != PostingRuleApproval.Type)
                {
                    return DomainError.Of(ModuleCode.FIN, "APPROVAL-REQUIRED", "The approval does not cover this rule set; nothing is activated that was not approved.");
                }
            }
        }
        catch (DomainException ex)
        {
            return ex.Error;
        }

        row.DecidedBy = checker;
        row.DecidedAt = clock.Now;
        row.DecisionReason = comment;
        row.Status = approve ? PostingRuleDraftStatus.Approved : PostingRuleDraftStatus.Rejected;
        row.RecordVersion++;
        if (approve)
        {
            var latest = await db.RuleSets.Where(s => s.LegalEntityCode == row.LegalEntityCode && s.Book == row.Book).MaxAsync(s => (int?)s.VersionNo, cancellationToken).ConfigureAwait(false) ?? 0;
            var set = new PostingRuleSetRow
            {
                RuleSetId = Guid.CreateVersion7(), LegalEntityCode = row.LegalEntityCode, Book = row.Book, VersionNo = latest + 1, Status = RuleSetStatus.Active,
                EffectiveFrom = row.EffectiveFrom, ContentHash = PostingRuleAuthoring.RuleSetHash(row.Book, w.Rules).Value, Source = $"authoring {row.DraftId:D}", CreatedAt = clock.Now,
            };
            db.RuleSets.Add(set);
            db.Rules.AddRange(w.Rules.Select(r => new PostingRuleRow
            {
                RuleId = Guid.CreateVersion7(), RuleSetId = set.RuleSetId, RuleCode = r.Code, SourceEvent = r.SourceEvent, EntryType = r.EntryType, SourceAccount = r.SourceAccount,
                ChargeCategory = r.ChargeCategory, ChargeType = r.ChargeType, Specificity = r.Specificity, AccountCode = r.Account, DeriveFrom = r.DeriveFrom,
                DescriptionEl = r.DescriptionEl, DescriptionEn = r.DescriptionEn,
            }));
            row.ActivatedRuleSetId = set.RuleSetId;
            row.ActivatedVersion = set.VersionNo;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return DomainError.Of(ModuleCode.FIN, "STATE", "The rule set changed meanwhile; load the draft and try again.");
        }

        return new PostingRulesAuthoringResult(PostingRuleAuthoringService.View(row), compile, null, null);
    }
}
