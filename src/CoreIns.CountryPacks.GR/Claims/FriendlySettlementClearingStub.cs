using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.CountryPacks.GR.Claims;

/// <summary>
/// STUB of the Greek <c>FriendlySettlementClearing</c> SPI (SPI 23, REQ-MKT-109/-313, D-SL4-02/-03/-18). It makes no external call
/// and holds its receivables and payables in an in-memory singleton store (acceptable for a stub; lost on restart). The Host binds
/// it only outside Production; in Production the SPI is unbound and CLM answers <c>CLM-ERR-FS-DISABLED</c> (fail closed).
/// <para>
/// Nothing it decides is Settled: every eligibility decision carries <see cref="LegalStatus.Unverified"/> and
/// <c>provisional = true</c>, so the Production gate (<c>== Settled</c>, PITFALLS 36) refuses it. The clearing value basis is
/// always ACTUAL (averages are unknown).
/// </para>
/// </summary>
public sealed class FriendlySettlementClearingStub : IFriendlySettlementClearing
{
    /// <summary>Rule id written into every decision.</summary>
    public const string RuleId = "GR-FS-ELIGIBILITY-STUB";

    /// <summary>Rule version written into every decision.</summary>
    public const string RuleVersion = "0.1.0";

    /// <summary>Format code of the JSON notification test format (see README.md).</summary>
    public const string NotificationFormat = "coreins.fs-notification.stub+json";

    /// <summary>Prefix of every clearing reference.</summary>
    public const string ReferencePrefix = "STUB-FS-";

    /// <summary>
    /// Material-damage limit. C-01: the PRDs conflict (6,500 in the press, 5,000/15,000 in the prompt); the stub takes the LOWER
    /// candidate, 5,000.00, and marks it Unverified. The same sentence is <see cref="LegalSourceRef"/>.
    /// </summary>
    public const decimal MaterialDamageLimit = 5000.00m;

    /// <summary>Legal source reference of the limit (the SPI result has no field for it; CLM stores it with the rule id).</summary>
    public const string LegalSourceRef =
        "PRD-17 GR-06 / C-01: conflicting candidates (6,500 press vs 5,000/15,000 prompt); the stub uses the lower, 5,000.00, UNVERIFIED";

    private readonly FriendlySettlementStubOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, StoredLine> _lines = new(StringComparer.Ordinal);

    /// <summary>Creates the stub.</summary>
    public FriendlySettlementClearingStub(FriendlySettlementStubOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <inheritdoc />
    public ValueTask<FsEligibilityDecision> EvaluateEligibilityAsync(FsClaimFacts claimFacts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claimFacts);
        var members = new HashSet<string>(_options.MemberCodes, StringComparer.Ordinal);
        var reasons = new List<string>();
        if (!members.Contains(_options.OwnInsurerCode))
        {
            reasons.Add("OWN_INSURER_NOT_MEMBER");
        }

        if (string.IsNullOrEmpty(claimFacts.CounterpartyInsurerCode) || !members.Contains(claimFacts.CounterpartyInsurerCode))
        {
            reasons.Add("COUNTERPARTY_NOT_MEMBER");
        }

        if (!claimFacts.AccidentInCountry)
        {
            reasons.Add("NOT_IN_GREECE");
        }

        if (claimFacts.VehicleCount != 2)
        {
            reasons.Add("VEHICLE_COUNT");
        }

        if (claimFacts.DamageTypes.Count == 0 || claimFacts.DamageTypes.Any(t => !string.Equals(t, "MATERIAL_DAMAGE", StringComparison.Ordinal)))
        {
            reasons.Add("DAMAGE_TYPE_NOT_MATERIAL_ONLY");
        }

        // Liability allocation must be 0/100 (one party fully at fault); anything in between is not clear.
        if (claimFacts.InsuredFaultPercent is not (0m or 100m))
        {
            reasons.Add("LIABILITY_NOT_CLEAR");
        }

        if (claimFacts.Amount.Currency != "EUR")
        {
            reasons.Add("CURRENCY_NOT_EUR");
        }
        else if (claimFacts.Amount.Amount > MaterialDamageLimit)
        {
            reasons.Add("AMOUNT_ABOVE_LIMIT");
        }

        var eligible = reasons.Count == 0;
        return ValueTask.FromResult(new FsEligibilityDecision(
            eligible ? FsEligibilityResult.Eligible : FsEligibilityResult.NotEligible,
            reasons, RuleId, RuleVersion, LegalStatus.Unverified, Provisional: true,
            eligible ? claimFacts.Amount : null, eligible ? ClearingValueBasis.Actual : null));
    }

    /// <inheritdoc />
    public ValueTask<FsSubmissionResult> SubmitAsync(FsSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        var reference = ReferencePrefix + submission.FsCaseId.ToString("N", CultureInfo.InvariantCulture)[^12..].ToUpperInvariant();
        var line = _lines.GetOrAdd(reference, _ => new StoredLine(
            reference, submission.CounterpartyInsurerCode, FsDirection.Receivable, submission.ClearingValue, submission.AccidentDate, _time.GetUtcNow()));
        return ValueTask.FromResult(new FsSubmissionResult(line.Reference, line.At));
    }

    /// <inheritdoc />
    public ValueTask<FsNotification> ReceiveNotificationAsync(FsNotificationMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var notification = Parse(message);
        _lines.GetOrAdd(notification.ClearingReference, _ => new StoredLine(
            notification.ClearingReference, notification.CounterpartyInsurerCode, FsDirection.Payable, notification.ClearingValue,
            notification.AccidentDate, _time.GetUtcNow()));
        return ValueTask.FromResult(notification);
    }

    /// <inheritdoc />
    public ValueTask<FsSettlementStatement> SettlementStatementAsync(
        FsStatementPeriod period, string? counterpartyCode = null, CancellationToken cancellationToken = default)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"STUB-FS-STMT-{period.From:yyyyMMdd}-{period.To:yyyyMMdd}-{counterpartyCode ?? "ALL"}");
        var selected = _lines.Values
            .Where(l => l.AccidentDate >= period.From && l.AccidentDate < period.To
                && (counterpartyCode is null || l.Counterparty == counterpartyCode))
            .OrderBy(l => l.Reference, StringComparer.Ordinal)
            .ToList();
        var lines = selected
            .Select((l, i) => new FsStatementLine(l.Reference, l.Counterparty, l.Direction, l.Amount, string.Create(CultureInfo.InvariantCulture, $"{id}-{i + 1:D3}")))
            .ToList();
        var nets = selected
            .GroupBy(l => (l.Counterparty, l.Amount.Currency))
            .OrderBy(g => g.Key.Counterparty, StringComparer.Ordinal)
            .Select(g =>
            {
                var net = g.Sum(l => l.Direction == FsDirection.Receivable ? l.Amount.Amount : -l.Amount.Amount);
                return new FsCounterpartyNet(g.Key.Counterparty, net < 0 ? FsDirection.Payable : FsDirection.Receivable, new SpiMoney(Math.Abs(net), g.Key.Currency));
            })
            .ToList();
        return ValueTask.FromResult(new FsSettlementStatement(id, period, lines, nets));
    }

    /// <inheritdoc />
    public ValueTask<FsDisputeResult> SubmitDisputeAsync(FsDisputeSubmission dispute, CancellationToken cancellationToken = default) =>
        throw NotImplemented();

    /// <inheritdoc />
    public ValueTask<FsDisputeResult> RecordReplyAsync(FsReplyRecord reply, CancellationToken cancellationToken = default) =>
        throw NotImplemented();

    private static SpiException NotImplemented() =>
        new(new SpiError(SpiErrorCategory.NotApplicable, "FS_DISPUTE_NOT_IMPLEMENTED"), "Friendly Settlement disputes are out of scope (OI-CLM-02, D-SL4-01).");

    private static FsNotification Parse(FsNotificationMessage message)
    {
        // Every failure is the typed SPI error FS_NOTIFICATION_MALFORMED (Validation); no JSON or format exception escapes.
        try
        {
            if (message.Format != NotificationFormat)
            {
                throw Malformed("format");
            }

            using var doc = JsonDocument.Parse(message.Body);
            var root = doc.RootElement;
            string Text(string name) =>
                root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
                    ? p.GetString()!
                    : throw Malformed(name);

            var amount = decimal.Parse(Text("amount"), NumberStyles.Number, CultureInfo.InvariantCulture);
            if (amount <= 0)
            {
                throw Malformed("amount");
            }

            var currency = root.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } cur ? cur : "EUR";
            return new FsNotification
            {
                ClearingReference = Text("clearingReference"),
                CounterpartyInsurerCode = Text("counterpartyInsurerPartyId"),
                PolicyNumber = Text("policyNumber"),
                AccidentDate = DateOnly.ParseExact(Text("lossDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ClearingValue = new SpiMoney(amount, currency),
                ClearingValueBasis = ClearingValueBasis.Actual,
                Role = FsRole.AtFaultInsurer,
                DamageTypes = [Text("damageType")],
            };
        }
        catch (Exception e) when (e is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw Malformed("not parseable");
        }
    }

    private static SpiException Malformed(string field) =>
        new(new SpiError(SpiErrorCategory.Validation, "FS_NOTIFICATION_MALFORMED", field), $"The Friendly Settlement notification is malformed ({field}).");

    private sealed record StoredLine(string Reference, string Counterparty, FsDirection Direction, SpiMoney Amount, DateOnly AccidentDate, DateTimeOffset At);
}
