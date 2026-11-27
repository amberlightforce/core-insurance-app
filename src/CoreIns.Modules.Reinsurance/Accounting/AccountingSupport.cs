using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Reinsurance.Authority;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Modules.Reinsurance.Registry;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Contracts.Api;
using CoreIns.Platform.Errors;
using CoreIns.Rules;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Reinsurance.Accounting;

/// <summary>
/// The premium arithmetic of BO-RI-10. Pure; no I/O. Money is EUR in cents (NX-EC-01 half-up, <see cref="DecimalRounding"/>); a total that
/// is split goes through the shared largest-remainder allocator (NX-EC-06, <see cref="CentAllocation"/>), so the parts sum exactly.
/// </summary>
internal static class AccountingMath
{
    /// <summary>The instalment intervals the contract accepts, in months.</summary>
    public static readonly int[] IntervalsMonths = [1, 2, 3, 4, 6, 12];

    /// <summary>The deposit split into <paramref name="count"/> equal instalments, due every <paramref name="intervalMonths"/> from <paramref name="first"/>.</summary>
    public static IReadOnlyList<(int Seq, BusinessDate DueDate, decimal Amount)> Instalments(decimal deposit, int count, BusinessDate first, int intervalMonths)
    {
        var amounts = CentAllocation.Allocate(deposit, [.. Enumerable.Repeat(1m, count)]);
        return [.. amounts.Select((amount, i) => (i + 1, new BusinessDate(first.Value.AddMonths(i * intervalMonths)), amount))];
    }

    /// <summary>
    /// The adjustment premium: the subject premium at the rate (cents, half-up), never below the minimum premium, less the deposit.
    /// Positive is additional premium due to the reinsurer; negative is a return.
    /// </summary>
    public static (decimal Calculated, decimal Adjustment) Adjustment(decimal subjectPremium, decimal ratePct, decimal minimumPremium, decimal depositPremium)
    {
        var calculated = DecimalRounding.RoundTo(subjectPremium * ratePct / 100m, 2, DecimalRounding.DefaultMoneyMode);
        return (calculated, Math.Max(calculated, minimumPremium) - depositPremium);
    }

    /// <summary>The loss ratio in percent (six decimals), or null when there is no ceded premium.</summary>
    public static decimal? LossRatioPct(decimal incurredLoss, decimal cededPremium) =>
        cededPremium <= 0m ? null : DecimalRounding.RoundTo(incurredLoss * 100m / cededPremium, 6, DecimalRounding.DefaultMoneyMode);

    /// <summary>The commission rate of the scale step with the highest <c>lossRatioFromPct</c> not above the loss ratio; null when no step applies.</summary>
    public static decimal? EarnedRatePct(decimal lossRatioPct, IReadOnlyList<(decimal From, decimal Rate)> scale)
    {
        var step = scale.Where(s => s.From <= lossRatioPct).OrderByDescending(s => s.From).Cast<(decimal From, decimal Rate)?>().FirstOrDefault();
        return step?.Rate;
    }

    /// <summary>A commission amount: premium times rate, in cents.</summary>
    public static decimal Commission(decimal cededPremium, decimal ratePct) =>
        DecimalRounding.RoundTo(cededPremium * ratePct / 100m, 2, DecimalRounding.DefaultMoneyMode);
}

/// <summary>JSON shapes stored in the jsonb columns.</summary>
internal sealed record InstalmentJson(int Seq, string DueDate, string Amount);

/// <summary>A scale step as stored.</summary>
internal sealed record ScaleStepJson(string LossRatioFromPct, string CommissionRatePct);

/// <summary>Row to view mapping of the premium accounting rows.</summary>
internal static class AccountingViews
{
    public static string Fixed(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    public static Money Eur(decimal amount, string currency) => new(ContractSupport.Amount(amount), Currency.FromCode(currency.Trim()));

    public static string ToJson(IReadOnlyList<(int Seq, BusinessDate DueDate, decimal Amount)> instalments) =>
        JsonSerializer.Serialize(instalments.Select(i => new InstalmentJson(i.Seq, i.DueDate.ToString(), i.Amount.ToString("F2", CultureInfo.InvariantCulture))));

    public static string ToJson(IReadOnlyList<(decimal From, decimal Rate)> scale) =>
        JsonSerializer.Serialize(scale.Select(s => new ScaleStepJson(s.From.ToString(CultureInfo.InvariantCulture), s.Rate.ToString(CultureInfo.InvariantCulture))));

    public static DepositScheduleView ToView(DepositScheduleRow row, bool current)
    {
        var instalments = JsonSerializer.Deserialize<List<InstalmentJson>>(row.Instalments) ?? [];
        return new DepositScheduleView
        {
            ScheduleId = row.ScheduleId, ContractId = row.ContractId, LayerNo = row.LayerNo, ContractYear = row.ContractYear, Revision = row.Revision, Current = current,
            MinimumPremium = Eur(row.MinimumPremium, row.Currency), DepositPremium = Eur(row.DepositPremium, row.Currency),
            Instalments =
            [
                .. instalments.Select(i => new DepositInstalment
                {
                    Seq = i.Seq,
                    DueDate = BusinessDate.Parse(i.DueDate),
                    Amount = Eur(decimal.Parse(i.Amount, CultureInfo.InvariantCulture), row.Currency),
                }),
            ],
            Reason = row.Reason, CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy,
        };
    }

    public static AdjustmentView ToView(AdjustmentRow row, AccountingDecisionRow? decision) => new()
    {
        AdjustmentId = row.AdjustmentId, ContractId = row.ContractId, LayerNo = row.LayerNo, ContractYear = row.ContractYear, ScheduleId = row.ScheduleId,
        SubjectPremium = Eur(row.SubjectPremium, row.Currency), RatePct = row.RatePct, CalculatedPremium = Eur(row.CalculatedPremium, row.Currency),
        MinimumPremium = Eur(row.MinimumPremium, row.Currency), DepositPremium = Eur(row.DepositPremium, row.Currency), AdjustmentAmount = Eur(row.AdjustmentAmount, row.Currency),
        Status = decision is null ? AdjustmentView.StatusValue.Pending : decision.Decision == "APPROVED" ? AdjustmentView.StatusValue.Approved : AdjustmentView.StatusValue.Rejected,
        ApprovalRequestId = new ApprovalRequestId(row.ApprovalRequestId), Reason = decision?.Reason ?? row.Reason, CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy,
        DecidedAt = decision?.DecidedAt, DecidedBy = decision?.DecidedBy,
    };

    public static CommissionView ToView(CommissionRow row, AccountingDecisionRow? decision)
    {
        var scale = JsonSerializer.Deserialize<List<ScaleStepJson>>(row.Scale) ?? [];
        return new CommissionView
        {
            CommissionId = row.CommissionId, ContractId = row.ContractId, ContractYear = row.ContractYear,
            CededPremium = Eur(row.CededPremium, row.Currency), IncurredLoss = Eur(row.IncurredLoss, row.Currency), LossRatioPct = row.LossRatioPct,
            ProvisionalRatePct = row.ProvisionalRatePct, EarnedRatePct = row.EarnedRatePct,
            ProvisionalCommission = Eur(row.ProvisionalCommission, row.Currency), EarnedCommission = Eur(row.EarnedCommission, row.Currency),
            AdjustmentAmount = Eur(row.AdjustmentAmount, row.Currency),
            Scale = [.. scale.Select(s => new CommissionScaleStep { LossRatioFromPct = decimal.Parse(s.LossRatioFromPct, CultureInfo.InvariantCulture), CommissionRatePct = decimal.Parse(s.CommissionRatePct, CultureInfo.InvariantCulture) })],
            Status = decision is null ? CommissionView.StatusValue.Pending : decision.Decision == "APPROVED" ? CommissionView.StatusValue.Approved : CommissionView.StatusValue.Rejected,
            ApprovalRequestId = new ApprovalRequestId(row.ApprovalRequestId), Reason = decision?.Reason ?? row.Reason, CreatedAt = row.CreatedAt, CreatedBy = row.CreatedBy,
            DecidedAt = decision?.DecidedAt, DecidedBy = decision?.DecidedBy,
        };
    }
}

/// <summary>
/// The PLT binding of adjustment premiums and commission calculations (BO-RI-10, <c>RI.ADJUSTMENT_APPROVE</c>, amount authority on the
/// absolute adjustment). RI sets the type, subject, hash and authority; the client never does (PITFALLS 3, 4). The maker is the editor,
/// so PLT refuses the maker as checker; RI also refuses it before it asks (<c>RI-ERR-SOD</c>).
/// </summary>
internal sealed class AccountingApprovals(IPlatformApprovalService approvals)
{
    /// <summary>The PLT approval type; owner-decided (registered in the module), so the generic inbox does not decide it.</summary>
    public const string ApprovalType = "RI.ADJUSTMENT_APPROVE";

    /// <summary>Subject type of an adjustment premium.</summary>
    public const string AdjustmentSubject = "Adjustment";

    /// <summary>Subject type of a commission calculation.</summary>
    public const string CommissionSubject = "Commission";

    /// <summary>The role a referral is addressed to.</summary>
    public const string ReferralRole = "Staff.ReinsuranceManager";

    public static ObjectRef SubjectOf(string subjectType, Guid id) => new(ModuleCode.RI, subjectType, id.ToString("D"));

    /// <summary>The hash a decision must carry: the exact facts of the calculation.</summary>
    public static Sha256Hash HashOf(string subjectType, Guid id, params string[] facts) =>
        Sha256Hash.ComputeUtf8(string.Join('|', [subjectType, id.ToString("D"), .. facts]));

    public async Task<Guid> RequestAsync(
        string subjectType, Guid id, Sha256Hash hash, Money amount, IReadOnlyList<string> editors, string reason, object diff, CancellationToken cancellationToken)
    {
        var request = new ApprovalRequestRequest
        {
            Type = ApprovalType,
            ObjectRef = SubjectOf(subjectType, id),
            PayloadHash = hash,
            Authority = new ApprovalAuthority { Type = ReinsuranceAuthorityTypes.AdjustmentApprove.Value, Amount = amount.Abs() },
            ReferralRole = ReferralRole,
            Editors = [.. editors],
            Reason = reason,
            Diff = JsonSerializer.SerializeToElement(diff),
        };
        var response = await approvals.RequestAsync(request, CommandOptions.New(), cancellationToken).ConfigureAwait(false);
        return response.Request.RequestId;
    }

    /// <summary>
    /// Decides the PLT request with the hash RI recomputed from the stored row; the checker's key on success. An approval also
    /// has PLT confirm for execution that the approved request is for exactly this type, subject, hash and authority.
    /// </summary>
    public async Task<Result<string>> DecideAsync(
        string subjectType, Guid id, Guid requestId, Sha256Hash hash, string self, bool reject, string? comment, CancellationToken cancellationToken)
    {
        try
        {
            var current = await approvals.GetAsync(requestId.ToString("D"), cancellationToken).ConfigureAwait(false);
            if (current.Request.Status != ApprovalStatus.PendingApproval)
            {
                return RiErrors.Stale($"The approval request is {current.Request.Status}; calculate again.");
            }

            await approvals.DecideAsync(
                new ApprovalDecideRequest
                {
                    RequestId = requestId, PayloadHash = hash, Comment = comment,
                    Decision = reject ? ApprovalDecideRequest.DecisionValue.Reject : ApprovalDecideRequest.DecisionValue.Approve,
                },
                CommandOptions.New(), cancellationToken).ConfigureAwait(false);
            if (reject)
            {
                return self;
            }

            var verified = await approvals.VerifyForExecutionAsync(
                new ApprovalVerifyForExecutionRequest { RequestId = requestId, Hash = hash, Type = ApprovalType, ObjectRef = SubjectOf(subjectType, id) },
                cancellationToken).ConfigureAwait(false);
            return verified.Ok && verified.Authority.Type == ReinsuranceAuthorityTypes.AdjustmentApprove.Value
                ? self
                : RiErrors.Stale($"PLT refused execution: the request is {verified.Status}.");
        }
        catch (DomainException ex) when (ex.Error.Code.Module == ModuleCode.PLT)
        {
            return ex.Error.Code.Name switch
            {
                "SELF-APPROVAL" or "EDITOR-CANNOT-APPROVE" => RiErrors.Sod("PLT refused: the checker took part in the calculation (REQ-PLT-115)."),
                "APPROVAL-STALE" or "APPROVAL-HASH-MISMATCH" or "NOT-FOUND" => RiErrors.Stale("The approval request changed or no longer matches the calculation."),
                _ => ex.Error,
            };
        }
    }
}
