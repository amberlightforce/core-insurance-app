using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Channels.Ai;
using CoreIns.Modules.Channels.Domain;
using CoreIns.Modules.Channels.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Channels.Commands;

/// <summary>An evaluation request from inside the module (portal, agency and AI commands call this, BO-CHN-03/06).</summary>
internal sealed record PermissionCheck(string TransactionType, string? Channel, string? ActorType, string? ProductLine, decimal? Amount, JsonElement? ObjectRef, bool RaiseTicket);

/// <summary>The answer: the outcome, the entry and matrix version it came from, and the ticket when REFER raised one.</summary>
internal sealed record PermissionAnswer(MatrixDecision Decision, int? Version, Guid? TicketId);

/// <summary>
/// The transaction-permission matrix (REQ-CHN-047..052): draft, edit, test with scenarios, submit (maker), schedule (a different checker,
/// effective-dated), read, diff, and <see cref="EvaluateAsync"/>. Every mutation is a signed-in person; nothing else may change the matrix.
/// </summary>
internal sealed class PermissionMatrixService(ChannelsDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, TicketService tickets)
{
    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    // ---- evaluate ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Decides one transaction for the caller. The actor type follows from who is calling (never from the request) unless the caller is staff
    /// asking a what-if. No matrix in force, no matching entry, an unknown actor: DENY.
    /// </summary>
    public async Task<Result<PermissionAnswer>> EvaluateAsync(PermissionCheck check, CancellationToken cancellationToken)
    {
        if (!MatrixDims.Transactions.Contains(check.TransactionType, StringComparer.Ordinal))
        {
            return DomainError.Of(ModuleCode.CHN, "UNKNOWN-TRANSACTION", $"'{check.TransactionType}' is not a known transaction type.");
        }

        var staffCaller = IsStaff();
        var actorType = staffCaller && !string.IsNullOrWhiteSpace(check.ActorType) ? check.ActorType.Trim().ToUpperInvariant() : DeriveActorType();
        var channel = staffCaller && !string.IsNullOrWhiteSpace(check.Channel) ? check.Channel.Trim().ToUpperInvariant() : context.Channel?.ToUpperInvariant() ?? check.Channel?.Trim().ToUpperInvariant() ?? string.Empty;
        // TODO(hardening): bind the channel of a non-staff caller to the token's channel claim instead of context/request.

        var matrix = await InForceAsync(cancellationToken).ConfigureAwait(false);
        MatrixDecision decision;
        if (matrix is null)
        {
            decision = new MatrixDecision(MatrixOutcomes.Deny, null, null, ["NO_ACTIVE_MATRIX"], "chn.permission.noMatrix");
        }
        else if (!MatrixDims.ActorTypes.Contains(actorType, StringComparer.Ordinal) || !MatrixDims.Channels.Contains(channel, StringComparer.Ordinal))
        {
            decision = new MatrixDecision(MatrixOutcomes.Deny, null, null, ["UNKNOWN_ACTOR_OR_CHANNEL"], "chn.permission.noEntry");
        }
        else
        {
            var entries = await LoadEntriesAsync(matrix.MatrixId, cancellationToken).ConfigureAwait(false);
            decision = MatrixEngine.Evaluate(entries, new MatrixQuery(check.TransactionType, channel, actorType, check.ProductLine?.Trim(), check.Amount));
        }

        Guid? ticketId = null;
        if (decision.Outcome == MatrixOutcomes.Refer && check.RaiseTicket && context.Actor.Kind == ActorKind.User)
        {
            var args = JsonSerializer.SerializeToElement(new { transactionType = check.TransactionType, channel, actorType, amount = check.Amount?.ToString(CultureInfo.InvariantCulture), objectRef = check.ObjectRef, reasons = decision.Reasons });
            ticketId = (await tickets.CreatePendingAsync("permission.refer:" + check.TransactionType, args, null, cancellationToken).ConfigureAwait(false)).TicketId;
        }

        return new PermissionAnswer(decision, matrix?.VersionNo, ticketId);
    }

    /// <summary>Customer.Portal is a customer, an AI agent actor is an agent, staff and platform roles are staff. Anything else is unknown (DENY).</summary>
    private string DeriveActorType()
    {
        if (context.Actor.Kind == ActorKind.AiAgent)
        {
            return "AI_AGENT";
        }

        if (IsStaff())
        {
            return "STAFF";
        }

        if (context.Roles.Contains("Customer.Portal"))
        {
            return "CUSTOMER";
        }

        return context.Roles.Any(r => r.StartsWith("Agency.", StringComparison.Ordinal)) ? "AGENT" : string.Empty;
    }

    private bool IsStaff() => context.Roles.Any(r => r.StartsWith("Staff.", StringComparison.Ordinal) || r.StartsWith("Platform.", StringComparison.Ordinal));

    private async Task<PermissionMatrixRow?> InForceAsync(CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var now = clock.Now;
        return await db.PermissionMatrices.AsNoTracking()
            .Where(m => m.LegalEntityId == legalEntity && m.Status == MatrixStatuses.Scheduled && m.EffectiveFrom <= now)
            .OrderByDescending(m => m.EffectiveFrom).ThenByDescending(m => m.VersionNo)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<MatrixEntry>> LoadEntriesAsync(Guid matrixId, CancellationToken cancellationToken) =>
        (await db.PermissionMatrixEntries.AsNoTracking().Where(e => e.MatrixId == matrixId && !e.Removed).ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(e => new MatrixEntry(e.TransactionType, e.Channel, e.ActorType, e.ProductLine, e.Outcome, e.MaxAmount, e.Level)).ToList();

    // ---- maintenance ------------------------------------------------------------------------------------------------------------

    /// <summary>A new DRAFT copied from the base (the named version, else the one in force, else the baseline), then the requested changes.</summary>
    public async Task<Result<object>> CreateDraftAsync(JsonElement? baseVersion, IReadOnlyList<JsonElement>? changes, IReadOnlyList<JsonElement>? scenarios, CancellationToken cancellationToken)
    {
        if (RequirePerson() is { } notPerson)
        {
            return notPerson;
        }

        var legalEntity = LegalEntity;
        List<MatrixEntry> baseEntries;
        string baseScenarios = "[]";
        if (baseVersion is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var no))
        {
            var found = await db.PermissionMatrices.AsNoTracking().FirstOrDefaultAsync(m => m.LegalEntityId == legalEntity && m.VersionNo == no, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return NotFound();
            }

            baseEntries = await LoadEntriesAsync(found.MatrixId, cancellationToken).ConfigureAwait(false);
            baseScenarios = found.Scenarios;
        }
        else if (await InForceAsync(cancellationToken).ConfigureAwait(false) is { } inForce)
        {
            baseEntries = await LoadEntriesAsync(inForce.MatrixId, cancellationToken).ConfigureAwait(false);
            baseScenarios = inForce.Scenarios;
        }
        else
        {
            baseEntries = [.. MatrixBaseline.Entries];
        }

        var working = baseEntries.ToDictionary(e => e.Key);
        // The final (EU, country) entries always come from the baseline: a base missing one gets it back, one changed is restored.
        foreach (var final in MatrixBaseline.Entries.Where(e => MatrixLevels.IsFinal(e.Level)))
        {
            working[final.Key] = final;
        }

        if (ApplyChanges(working, changes) is { } problem)
        {
            return problem;
        }

        var scenarioJson = scenarios is null ? baseScenarios : JsonSerializer.Serialize(scenarios);
        if (ParseScenarios(scenarioJson, out var _) is { } badScenario)
        {
            return badScenario;
        }

        var next = (await db.PermissionMatrices.Where(m => m.LegalEntityId == legalEntity).MaxAsync(m => (int?)m.VersionNo, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var actor = context.Actor.ToString();
        var row = new PermissionMatrixRow
        {
            MatrixId = Guid.CreateVersion7(),
            LegalEntityId = legalEntity,
            VersionNo = next,
            Status = MatrixStatuses.Draft,
            Scenarios = scenarioJson,
            CreatedBy = actor,
            CreatedAt = clock.Now,
            LastEditedBy = actor,
        };
        db.PermissionMatrices.Add(row);
        AddEntries(row.MatrixId, working.Values);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Envelope(row, working.Values.ToList(), RunScenarios(working.Values.ToList(), scenarioJson), MatrixEngine.Diff(baseEntries, working.Values.ToList()), includeEntries: true);
    }

    /// <summary>Upserts or removes entries of a DRAFT. A final entry cannot be touched (CHN-ERR-FINAL-ENTRY).</summary>
    public async Task<Result<object>> UpdateEntryAsync(Guid? matrixId, IReadOnlyList<JsonElement>? changes, IReadOnlyList<JsonElement>? scenarios, CancellationToken cancellationToken)
    {
        if (RequirePerson() is { } notPerson)
        {
            return notPerson;
        }

        var loaded = await LoadAsync(matrixId, MatrixStatuses.Draft, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var row = loaded.Value;
        var before = await LoadEntriesAsync(row.MatrixId, cancellationToken).ConfigureAwait(false);
        var working = before.ToDictionary(e => e.Key);
        if (ApplyChanges(working, changes) is { } problem)
        {
            return problem;
        }

        if (scenarios is not null)
        {
            row.Scenarios = JsonSerializer.Serialize(scenarios);
            if (ParseScenarios(row.Scenarios, out var _) is { } badScenario)
            {
                return badScenario;
            }
        }

        var existing = await db.PermissionMatrixEntries.Where(e => e.MatrixId == row.MatrixId).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var e in existing)
        {
            if (working.TryGetValue(Key(e), out var w))
            {
                e.Outcome = w.Outcome;
                e.MaxAmount = w.MaxAmount;
                e.Removed = false;
            }
            else
            {
                e.Removed = true;
            }
        }

        AddEntries(row.MatrixId, working.Values.Where(w => !existing.Any(e => Key(e) == w.Key)));
        row.LastEditedBy = context.Actor.ToString();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var list = working.Values.ToList();
        return Envelope(row, list, RunScenarios(list, row.Scenarios), MatrixEngine.Diff(before, list), includeEntries: true);
    }

    /// <summary>Runs scenarios against a matrix (default: its own scenarios), or against an entry set given inline. Writes nothing.</summary>
    public async Task<Result<object>> RunScenariosAsync(Guid? matrixId, IReadOnlyList<JsonElement>? inlineEntries, IReadOnlyList<JsonElement>? scenarios, CancellationToken cancellationToken)
    {
        PermissionMatrixRow? row = null;
        List<MatrixEntry> entries;
        if (matrixId is not null)
        {
            var loaded = await LoadAsync(matrixId, null, cancellationToken).ConfigureAwait(false);
            if (!loaded.IsSuccess)
            {
                return loaded.Error;
            }

            row = loaded.Value;
            entries = await LoadEntriesAsync(row.MatrixId, cancellationToken).ConfigureAwait(false);
        }
        else if (inlineEntries is not null)
        {
            var working = new Dictionary<string, MatrixEntry>();
            if (ApplyChanges(working, inlineEntries) is { } problem)
            {
                return problem;
            }

            entries = working.Values.ToList();
        }
        else
        {
            return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "Give a matrixId or entries.");
        }

        var json = scenarios is null ? row?.Scenarios ?? "[]" : JsonSerializer.Serialize(scenarios);
        if (ParseScenarios(json, out var _) is { } bad)
        {
            return bad;
        }

        var results = RunScenarios(entries, json);
        return new { version = row is null ? null : Version(row), results, diff = (object?)null };
    }

    /// <summary>The maker submits a DRAFT: the scenarios must all pass and every final entry must be intact.</summary>
    public async Task<Result<object>> SubmitAsync(Guid? matrixId, CancellationToken cancellationToken)
    {
        if (RequirePerson() is { } notPerson)
        {
            return notPerson;
        }

        var loaded = await LoadAsync(matrixId, MatrixStatuses.Draft, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var row = loaded.Value;
        var entries = await LoadEntriesAsync(row.MatrixId, cancellationToken).ConfigureAwait(false);
        var byKey = entries.ToDictionary(e => e.Key);
        foreach (var final in MatrixBaseline.Entries.Where(e => MatrixLevels.IsFinal(e.Level)))
        {
            if (!byKey.TryGetValue(final.Key, out var present) || present != final)
            {
                return DomainError.Of(ModuleCode.CHN, "FINAL-ENTRY", $"The final entry {final.Key} is missing or changed.");
            }
        }

        var results = RunScenarios(entries, row.Scenarios);
        var failed = results.Where(r => !r.Pass).Select(r => r.Name).ToList();
        if (failed.Count > 0)
        {
            return DomainError.Of(ModuleCode.CHN, "SCENARIO-FAILED", $"Scenarios failed: {string.Join(", ", failed)}.");
        }

        row.Status = MatrixStatuses.Submitted;
        row.SubmittedBy = context.Actor.ToString();
        row.SubmittedAt = clock.Now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Envelope(row, null, results, null, includeEntries: false);
    }

    /// <summary>
    /// The checker activates a SUBMITTED matrix from <paramref name="effectiveFrom"/> (default now). The checker must not be the person who
    /// created, edited or submitted it (CHN-ERR-SELF-APPROVAL).
    /// </summary>
    public async Task<Result<object>> ScheduleAsync(Guid? matrixId, string? effectiveFrom, CancellationToken cancellationToken)
    {
        if (RequirePerson() is { } notPerson)
        {
            return notPerson;
        }

        var loaded = await LoadAsync(matrixId, MatrixStatuses.Submitted, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var row = loaded.Value;
        var checker = context.Actor.ToString();
        if (checker == row.CreatedBy || checker == row.LastEditedBy || checker == row.SubmittedBy)
        {
            return DomainError.Of(ModuleCode.CHN, "SELF-APPROVAL", "The person who prepared or submitted a matrix cannot approve it.");
        }

        var now = clock.Now;
        var effective = now;
        if (!string.IsNullOrWhiteSpace(effectiveFrom))
        {
            if (!DateTimeOffset.TryParse(effectiveFrom, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "effectiveFrom must be an ISO 8601 instant.");
            }

            effective = Instant.FromDateTimeOffset(parsed);
            if (effective < now - TimeSpan.FromMinutes(1))
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "effectiveFrom cannot be in the past.");
            }
        }

        row.Status = MatrixStatuses.Scheduled;
        row.ApprovedBy = checker;
        row.ApprovedAt = now;
        row.EffectiveFrom = effective;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Envelope(row, null, [], null, includeEntries: false);
    }

    /// <summary>A matrix by id, or <c>active</c> for the one in force.</summary>
    public async Task<Result<object>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var row = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        var entries = await LoadEntriesAsync(row.MatrixId, cancellationToken).ConfigureAwait(false);
        return Envelope(row, entries, RunScenarios(entries, row.Scenarios), null, includeEntries: true);
    }

    /// <summary>The difference of a matrix to another one (default: the one in force; none in force: the baseline).</summary>
    public async Task<Result<object>> DiffAsync(string id, string? against, CancellationToken cancellationToken)
    {
        var row = await ResolveAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        var other = string.IsNullOrWhiteSpace(against) ? await InForceAsync(cancellationToken).ConfigureAwait(false) : await ResolveAsync(against, cancellationToken).ConfigureAwait(false);
        if (other is null && !string.IsNullOrWhiteSpace(against))
        {
            return NotFound();
        }

        var entries = await LoadEntriesAsync(row.MatrixId, cancellationToken).ConfigureAwait(false);
        var otherEntries = other is null ? [.. MatrixBaseline.Entries] : await LoadEntriesAsync(other.MatrixId, cancellationToken).ConfigureAwait(false);
        return Envelope(row, null, [], MatrixEngine.Diff(otherEntries, entries), includeEntries: false);
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------------------

    private DomainError? RequirePerson() =>
        context.Actor.Kind == ActorKind.User ? null : DomainError.Of(ModuleCode.CHN, "HUMAN-REQUIRED", "Only a person can change the permission matrix.");

    private static DomainError NotFound() => DomainError.Of(ModuleCode.CHN, "NOT-FOUND", "The matrix does not exist in your legal entity.");

    private async Task<PermissionMatrixRow?> ResolveAsync(string id, CancellationToken cancellationToken)
    {
        if (string.Equals(id, "active", StringComparison.OrdinalIgnoreCase))
        {
            var inForce = await InForceAsync(cancellationToken).ConfigureAwait(false);
            return inForce;
        }

        var legalEntity = LegalEntity;
        return Guid.TryParse(id, out var guid)
            ? await db.PermissionMatrices.FirstOrDefaultAsync(m => m.MatrixId == guid && m.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<Result<PermissionMatrixRow>> LoadAsync(Guid? matrixId, string? requiredStatus, CancellationToken cancellationToken)
    {
        if (matrixId is null)
        {
            return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "matrixId is required.");
        }

        var row = await ResolveAsync(matrixId.Value.ToString(), cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        return requiredStatus is not null && row.Status != requiredStatus
            ? DomainError.Of(ModuleCode.CHN, "WRONG-STATE", $"The matrix is {row.Status}; this needs {requiredStatus}.")
            : row;
    }

    private static string Key(PermissionMatrixEntryRow e) => $"{e.TransactionType}|{e.Channel}|{e.ActorType}|{e.ProductLine}";

    private void AddEntries(Guid matrixId, IEnumerable<MatrixEntry> entries)
    {
        foreach (var e in entries)
        {
            db.PermissionMatrixEntries.Add(new PermissionMatrixEntryRow
            {
                EntryId = Guid.CreateVersion7(),
                MatrixId = matrixId,
                TransactionType = e.TransactionType,
                Channel = e.Channel,
                ActorType = e.ActorType,
                ProductLine = e.ProductLine,
                Outcome = e.Outcome,
                MaxAmount = e.MaxAmount,
                Level = e.Level,
            });
        }
    }

    /// <summary>Applies upserts and removals to <paramref name="working"/>; returns the first problem, or null.</summary>
    private static DomainError? ApplyChanges(Dictionary<string, MatrixEntry> working, IReadOnlyList<JsonElement>? changes)
    {
        foreach (var c in changes ?? [])
        {
            if (c.ValueKind != JsonValueKind.Object)
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "An entry must be an object.");
            }

            var transaction = Str(c, "transactionType");
            var channel = (Str(c, "channel") ?? MatrixDims.Any).ToUpperInvariant();
            var actor = (Str(c, "actorType") ?? MatrixDims.Any).ToUpperInvariant();
            var product = Str(c, "productLine") ?? MatrixDims.Any;
            var outcome = Str(c, "outcome")?.ToUpperInvariant();
            var remove = c.TryGetProperty("remove", out var r) && r.ValueKind == JsonValueKind.True;
            if (transaction is null || (transaction != MatrixDims.Any && !MatrixDims.Transactions.Contains(transaction, StringComparer.Ordinal)))
            {
                return DomainError.Of(ModuleCode.CHN, "UNKNOWN-TRANSACTION", $"'{transaction}' is not a known transaction type.");
            }

            if ((channel != MatrixDims.Any && !MatrixDims.Channels.Contains(channel)) || (actor != MatrixDims.Any && !MatrixDims.ActorTypes.Contains(actor)) || product.Length is 0 or > 40)
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "Unknown channel, actor type or product line.");
            }

            var key = new MatrixEntry(transaction, channel, actor, product, MatrixOutcomes.Deny, null, MatrixLevels.LegalEntity).Key;
            if (working.TryGetValue(key, out var existing) && MatrixLevels.IsFinal(existing.Level))
            {
                return DomainError.Of(ModuleCode.CHN, "FINAL-ENTRY", $"The entry {key} is set by {existing.Level} rules and cannot be changed.");
            }

            if (remove)
            {
                working.Remove(key);
                continue;
            }

            if (outcome is null || !MatrixOutcomes.All.Contains(outcome))
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "outcome must be ALLOW, REFER or DENY.");
            }

            decimal? max = null;
            if (c.TryGetProperty("maxAmount", out var m) && m.ValueKind is not JsonValueKind.Null)
            {
                if (!(m.ValueKind == JsonValueKind.Number && m.TryGetDecimal(out var d)) && !(m.ValueKind == JsonValueKind.String && decimal.TryParse(m.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out d)) || d < 0)
                {
                    return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", "maxAmount must be a non-negative number.");
                }

                max = d;
            }

            working[key] = new MatrixEntry(transaction, channel, actor, product, outcome, max, MatrixLevels.LegalEntity);
        }

        return null;
    }

    private static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record Scenario(string Name, MatrixQuery Query, string Expect);

    private sealed record ScenarioResult(string Name, string Expected, string Actual, bool Pass, string? EntryKey, string[] Reasons);

    private static DomainError? ParseScenarios(string json, out List<Scenario> scenarios)
    {
        scenarios = [];
        using var doc = JsonDocument.Parse(json);
        var i = 0;
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            i++;
            var transaction = Str(s, "transactionType");
            var expect = Str(s, "expect")?.ToUpperInvariant();
            if (transaction is null || expect is null || !MatrixOutcomes.All.Contains(expect))
            {
                return DomainError.Of(ModuleCode.CHN, "INVALID-ENTRY", $"Scenario {i} needs transactionType and expect (ALLOW, REFER or DENY).");
            }

            decimal? amount = null;
            if (s.TryGetProperty("amount", out var a) && a.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                amount = a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : decimal.TryParse(a.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
            }

            scenarios.Add(new Scenario(Str(s, "name") ?? $"scenario-{i}", new MatrixQuery(transaction, (Str(s, "channel") ?? string.Empty).ToUpperInvariant(), (Str(s, "actorType") ?? string.Empty).ToUpperInvariant(), Str(s, "productLine"), amount), expect));
        }

        return null;
    }

    private static List<ScenarioResult> RunScenarios(IReadOnlyCollection<MatrixEntry> entries, string json)
    {
        ParseScenarios(json, out var scenarios);
        return scenarios.Select(s =>
        {
            var d = MatrixEngine.Evaluate(entries, s.Query);
            return new ScenarioResult(s.Name, s.Expect, d.Outcome, d.Outcome == s.Expect, d.Entry?.Key, d.Reasons);
        }).ToList();
    }

    private static object Version(PermissionMatrixRow r) => new
    {
        matrixId = r.MatrixId,
        number = r.VersionNo,
        status = r.Status,
        createdBy = r.CreatedBy,
        createdAt = r.CreatedAt,
        submittedBy = r.SubmittedBy,
        approvedBy = r.ApprovedBy,
        effectiveFrom = r.EffectiveFrom,
    };

    private static object Envelope(PermissionMatrixRow row, IReadOnlyCollection<MatrixEntry>? entries, IEnumerable<ScenarioResult> results, object? diff, bool includeEntries) => new
    {
        version = includeEntries ? new { matrix = Version(row), entries = entries!.OrderBy(e => e.Key, StringComparer.Ordinal).Select(MatrixEngine.Describe).ToArray() } : (object)Version(row),
        results = results.ToArray(),
        diff,
    };
}
