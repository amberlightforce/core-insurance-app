using System.Globalization;
using CoreIns.Modules.Migration.Domain;
using CoreIns.Modules.Migration.Persistence;
using CoreIns.Modules.Migration.Services.Reconciliation;
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
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Migration.Services.SignOff;

internal sealed record SignOffWaiveBody(Guid RunId, IReadOnlyList<string>? VarianceKeys, string? Reason);

internal sealed record SignOffWaiveView(Guid RunId, IReadOnlyList<string> Waived);

internal sealed record SignOffRequestBody(string? Scope);

internal sealed record SignOffDecisionBody(Guid SignoffId, string? Signatory, string? Comment);

internal sealed record SignatoryView(string Signatory, string Status, string? DecidedBy, DateTimeOffset? DecidedAt, string? Comment);

internal sealed record SignOffStatusView(
    Guid? SignoffId, string Scope, Guid? RunId, Guid? LatestRunId, string? ResultsHash, bool Green, IReadOnlyList<string> Variances,
    IReadOnlyList<string> WaivedVariances, string? RequestedBy, bool Ready, IReadOnlyList<string> Blockers, IReadOnlyList<SignatoryView> Signatories);

/// <summary>The named signatories of a wave sign-off and the staff role each is requested from.</summary>
internal static class SignatoryRole
{
    public const string FinanceController = "FINANCE_CONTROLLER";
    public const string ComplianceOfficer = "COMPLIANCE_OFFICER";
    public const string MigrationLead = "MIGRATION_LEAD";

    public static readonly IReadOnlyList<string> All = [FinanceController, ComplianceOfficer, MigrationLead];

    // Illustrative role mapping (the grants live in permissions/mig.json).
    public static string ReferralRole(string signatory) => signatory switch
    {
        FinanceController => "Staff.Finance",
        ComplianceOfficer => "Staff.ComplianceOfficer",
        _ => "Staff.MigrationManager",
    };
}

/// <summary>The approval type of a wave sign-off: one platform approval request per signatory.</summary>
internal static class SignOffApproval
{
    public const string Type = "MIG.WAVE_SIGNOFF";
    public const string SignatoryDimension = "signatory";

    public static ObjectRef Subject(Guid signOffId, string signatory) => new(ModuleCode.MIG, "WaveSignoff", $"{signOffId:D}/{signatory}");
}

/// <summary>
/// Wave sign-off (BO-MIG-11): a sign-off binds to the latest reconciliation run of a scope and a hash of its result rows (with the waived variances).
/// A newer run, changed results or a control-total reload after the run make it stale. The wave may close (<see cref="StatusAsync"/> Ready) when
/// the run is green or every variance is waived, and each named signatory approved. The person who ran the reconciliation or loaded the control totals
/// cannot waive or sign, and one person signs for one signatory only (SoD).
/// </summary>
internal sealed class SignOffService(
    MigrationDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, IServiceProvider services)
{
    private const string DefaultScope = "default";

    private LegalEntityId LegalEntity => legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private static string? NormaliseScope(string? scope)
    {
        var s = string.IsNullOrWhiteSpace(scope) ? DefaultScope : scope.Trim();
        return s.Length is >= 1 and <= 64 ? s : null;
    }

    private static string KeyOf(ReconResultRow r) => $"{r.ModuleName}/{r.ObjectType}/{r.Key}/{r.Currency}";

    private sealed record RunData(Guid RunId, Instant RunAt, string RunBy, List<ReconResultRow> Rows);

    private async Task<RunData?> LatestRunAsync(string scope, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var id = await db.ReconResults.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.Scope == scope)
            .OrderByDescending(r => r.RunAt).ThenByDescending(r => r.ResultId).Select(r => (Guid?)r.RunId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return id is null ? null : await RunAsync(id.Value, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RunData?> RunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var rows = await db.ReconResults.AsNoTracking().Where(r => r.LegalEntityId == legalEntity && r.RunId == runId)
            .OrderBy(r => r.ModuleName).ThenBy(r => r.ObjectType).ThenBy(r => r.Key).ThenBy(r => r.Currency).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : new RunData(runId, rows[0].RunAt, rows[0].RunBy, rows);
    }

    private async Task<List<string>> WaivedKeysAsync(Guid runId, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        return await db.SignOffWaivers.AsNoTracking().Where(w => w.LegalEntityId == legalEntity && w.RunId == runId)
            .Select(w => w.VarianceKey).OrderBy(k => k).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The hash a sign-off covers: the scope, run id, every result row and the waived variance keys.</summary>
    private static string ResultsHash(string scope, RunData run, IEnumerable<string> waived)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = run.Rows.Select(r => string.Join('|', r.ModuleName, r.ObjectType, r.Key, r.Currency, r.LegacyCount.ToString(inv), r.CoreCount.ToString(inv),
            r.LegacyAmount.ToString("0.0000", inv), r.CoreAmount.ToString("0.0000", inv), r.Status));
        return Sha256Hash.ComputeUtf8($"{scope}|{run.RunId:D}\n{string.Join('\n', lines)}\nwaived:{string.Join(',', waived.Order(StringComparer.Ordinal))}").Value;
    }

    /// <summary>Who ran the run or loaded the scope's control totals: they cannot sign off or waive.</summary>
    private async Task<bool> TookPartAsync(string scope, RunData run, string actor, CancellationToken cancellationToken)
    {
        if (string.Equals(run.RunBy, actor, StringComparison.Ordinal))
        {
            return true;
        }

        var legalEntity = LegalEntity;
        return await db.ControlTotals.AsNoTracking().AnyAsync(c => c.LegalEntityId == legalEntity && c.Scope == scope && c.LoadedBy == actor, cancellationToken).ConfigureAwait(false);
    }

    private static DomainError Sod(string detail) => DomainError.Of(ModuleCode.MIG, "SIGNOFF-SOD", detail);

    public async Task<Result<SignOffWaiveView>> WaiveAsync(SignOffWaiveBody body, CancellationToken cancellationToken)
    {
        if (body.VarianceKeys is not { Count: > 0 } || string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 500)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "Give at least one variance key and a reason of up to 500 characters.");
        }

        var run = await RunAsync(body.RunId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return MigrationErrors.NotFound("The reconciliation run");
        }

        var scope = run.Rows[0].Scope;
        var latest = await LatestRunAsync(scope, cancellationToken).ConfigureAwait(false);
        if (latest!.RunId != run.RunId)
        {
            return DomainError.Of(ModuleCode.MIG, "SIGNOFF-STALE", "A newer reconciliation run exists for the scope; waivers bind to the latest run.");
        }

        var actor = context.Actor.ToString();
        if (await TookPartAsync(scope, run, actor, cancellationToken).ConfigureAwait(false))
        {
            return Sod("The person who ran the reconciliation or loaded its control totals cannot waive its variances.");
        }

        var variances = run.Rows.Where(r => r.Status != ReconStatus.Match).Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        var keys = body.VarianceKeys.Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var unknown = keys.FirstOrDefault(k => !variances.Contains(k));
        if (unknown is not null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, $"'{unknown}' is not a variance of run {run.RunId}.");
        }

        var legalEntity = LegalEntity;
        var existing = (await WaivedKeysAsync(run.RunId, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var now = clock.Now;
        foreach (var key in keys.Where(k => !existing.Contains(k)))
        {
            db.SignOffWaivers.Add(new SignOffWaiverRow
            {
                WaiverId = Guid.CreateVersion7(), LegalEntityId = legalEntity, RunId = run.RunId, VarianceKey = key, Reason = body.Reason.Trim(), WaivedBy = actor, WaivedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SignOffWaiveView(run.RunId, [.. existing.Union(keys).Order(StringComparer.Ordinal)]);
    }

    public async Task<Result<SignOffStatusView>> RequestAsync(SignOffRequestBody body, CancellationToken cancellationToken)
    {
        var scope = NormaliseScope(body.Scope);
        if (scope is null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "scope must be 1 to 64 characters.");
        }

        var run = await LatestRunAsync(scope, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return MigrationErrors.NotFound("A reconciliation run of the scope");
        }

        var waived = await WaivedKeysAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        var unwaived = run.Rows.Where(r => r.Status != ReconStatus.Match).Select(KeyOf).Except(waived, StringComparer.Ordinal).ToList();
        if (unwaived.Count > 0)
        {
            return MigrationErrors.State($"The reconciliation run has {unwaived.Count} variance(s) that are not waived; waive them or fix and re-run before requesting sign-off.");
        }

        var hash = ResultsHash(scope, run, waived);
        var current = (await StatusAsync(scope, cancellationToken).ConfigureAwait(false)).Value;
        if (current is { SignoffId: not null } && current.RunId == run.RunId && current.ResultsHash == hash && !current.Signatories.Any(s => s.Status == SignatoryStatus.Rejected))
        {
            return MigrationErrors.State("A sign-off of this run and its results is already requested.");
        }

        var approvals = services.GetService<IPlatformApprovalService>();
        if (approvals is null)
        {
            return DomainError.Of(ModuleCode.MIG, "APPROVAL-REQUIRED", "The approval service is not available.");
        }

        var signOffId = Guid.CreateVersion7();
        var now = clock.Now;
        var rows = new List<SignOffSignatoryRow>();
        try
        {
            foreach (var role in SignatoryRole.All)
            {
                var response = await approvals.RequestAsync(
                    new ApprovalRequestRequest
                    {
                        Type = SignOffApproval.Type,
                        ObjectRef = SignOffApproval.Subject(signOffId, role),
                        PayloadHash = Sha256Hash.Parse(hash),
                        Authority = new ApprovalAuthority
                        {
                            Type = SignOffApproval.Type,
                            Codes = new Dictionary<string, string>(StringComparer.Ordinal) { [SignOffApproval.SignatoryDimension] = role },
                        },
                        ReferralRole = SignatoryRole.ReferralRole(role),
                        Reason = $"Sign off migration scope '{scope}' as {role}: run {run.RunId:D}, {run.Rows.Count} figures, {waived.Count} waived variance(s).",
                    },
                    CommandOptions.New(),
                    cancellationToken).ConfigureAwait(false);
                rows.Add(new SignOffSignatoryRow { SignatoryId = Guid.CreateVersion7(), SignOffId = signOffId, Signatory = role, ApprovalRequestId = response.Request.RequestId });
            }
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        db.SignOffs.Add(new SignOffRow
        {
            SignOffId = signOffId, LegalEntityId = LegalEntity, Scope = scope, RunId = run.RunId, ResultsHash = hash, RequestedBy = context.Actor.ToString(), RequestedAt = now,
        });
        db.SignOffSignatories.AddRange(rows);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await StatusAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<SignOffStatusView>> DecideAsync(SignOffDecisionBody body, bool reject, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var signOff = await db.SignOffs.SingleOrDefaultAsync(s => s.SignOffId == body.SignoffId && s.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (signOff is null)
        {
            return MigrationErrors.NotFound("The sign-off");
        }

        var role = body.Signatory?.Trim().ToUpperInvariant();
        if (role is null || !SignatoryRole.All.Contains(role))
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "signatory must be FINANCE_CONTROLLER, COMPLIANCE_OFFICER or MIGRATION_LEAD.");
        }

        var signatories = await db.SignOffSignatories.Where(s => s.SignOffId == signOff.SignOffId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var row = signatories.Single(s => s.Signatory == role);
        if (row.Status != SignatoryStatus.Pending)
        {
            return MigrationErrors.State($"The {role} decision is already recorded ({row.Status}).");
        }

        // The sign-off must still cover the latest run and its results as they are now.
        var run = await LatestRunAsync(signOff.Scope, cancellationToken).ConfigureAwait(false);
        var stale = await StaleReasonAsync(signOff, run, cancellationToken).ConfigureAwait(false);
        if (stale is not null)
        {
            return DomainError.Of(ModuleCode.MIG, "SIGNOFF-STALE", $"The sign-off no longer covers the reconciliation ({stale}); request sign-off again.");
        }

        var actor = context.Actor.ToString();
        if (await TookPartAsync(signOff.Scope, run!, actor, cancellationToken).ConfigureAwait(false))
        {
            return Sod("The person who ran the reconciliation or loaded its control totals cannot sign off.");
        }

        if (signatories.Any(s => s.DecidedBy == actor))
        {
            return Sod("One person cannot sign for more than one signatory of the same sign-off.");
        }

        try
        {
            await services.GetRequiredService<IPlatformApprovalService>().DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = row.ApprovalRequestId,
                    PayloadHash = Sha256Hash.Parse(signOff.ResultsHash),
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                    Comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment,
                },
                CommandOptions.New(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error;
        }

        row.Status = reject ? SignatoryStatus.Rejected : SignatoryStatus.Approved;
        row.DecidedBy = actor;
        row.DecidedAt = clock.Now;
        row.Comment = string.IsNullOrWhiteSpace(body.Comment) ? null : body.Comment.Trim();
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await StatusAsync(signOff.Scope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> StaleReasonAsync(SignOffRow signOff, RunData? latest, CancellationToken cancellationToken)
    {
        if (latest is null || latest.RunId != signOff.RunId)
        {
            return "NEWER_RUN";
        }

        var waived = await WaivedKeysAsync(latest.RunId, cancellationToken).ConfigureAwait(false);
        if (ResultsHash(signOff.Scope, latest, waived) != signOff.ResultsHash)
        {
            return "RESULTS_CHANGED";
        }

        // Control totals reloaded after the run: the run no longer reflects the legacy figures.
        var legalEntity = LegalEntity;
        var loaded = await db.ControlTotals.AsNoTracking().Where(c => c.LegalEntityId == legalEntity && c.Scope == signOff.Scope && c.Active)
            .Select(c => c.LoadedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        return loaded.Any(l => l.CompareTo(latest.RunAt) > 0) ? "RESULTS_CHANGED" : null;
    }

    /// <summary>The sign-off state of a scope: whether the wave may close and what blocks it.</summary>
    public async Task<Result<SignOffStatusView>> StatusAsync(string? scopeInput, CancellationToken cancellationToken)
    {
        var scope = NormaliseScope(scopeInput);
        if (scope is null)
        {
            return DomainError.Of(ModuleCode.MIG, MigrationErrors.Validation, "scope must be 1 to 64 characters.");
        }

        var legalEntity = LegalEntity;
        var run = await LatestRunAsync(scope, cancellationToken).ConfigureAwait(false);
        var signOff = await db.SignOffs.AsNoTracking().Where(s => s.LegalEntityId == legalEntity && s.Scope == scope)
            .OrderByDescending(s => s.RequestedAt).ThenByDescending(s => s.SignOffId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var blockers = new List<string>();
        var variances = run?.Rows.Where(r => r.Status != ReconStatus.Match).Select(KeyOf).ToList() ?? [];
        var waived = run is null ? [] : await WaivedKeysAsync(run.RunId, cancellationToken).ConfigureAwait(false);
        var waivedVariances = variances.Where(v => waived.Contains(v)).ToList();
        if (run is null)
        {
            blockers.Add("NO_RUN");
        }
        else if (variances.Except(waived, StringComparer.Ordinal).Any())
        {
            blockers.Add("UNWAIVED_VARIANCES");
        }

        var signatories = new List<SignatoryView>();
        if (signOff is null)
        {
            blockers.Add("NO_SIGNOFF");
        }
        else
        {
            var stale = await StaleReasonAsync(signOff, run, cancellationToken).ConfigureAwait(false);
            if (stale is not null)
            {
                blockers.Add(stale);
            }

            var rows = await db.SignOffSignatories.AsNoTracking().Where(s => s.SignOffId == signOff.SignOffId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var role in SignatoryRole.All)
            {
                var r = rows.Single(x => x.Signatory == role);
                signatories.Add(new SignatoryView(role, r.Status, r.DecidedBy, r.DecidedAt?.ToDateTimeOffset(), r.Comment));
                if (r.Status == SignatoryStatus.Pending)
                {
                    blockers.Add($"SIGNATORY_PENDING:{role}");
                }
                else if (r.Status == SignatoryStatus.Rejected)
                {
                    blockers.Add($"SIGNATORY_REJECTED:{role}");
                }
            }
        }

        return new SignOffStatusView(
            signOff?.SignOffId, scope, signOff?.RunId, run?.RunId, signOff?.ResultsHash, run is not null && variances.Count == 0, variances, waivedVariances,
            signOff?.RequestedBy, blockers.Count == 0, blockers, signatories);
    }
}
