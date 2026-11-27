using System.Globalization;

namespace CoreIns.Modules.Channels.Domain;

/// <summary>Outcomes of a matrix decision (REQ-CHN-049).</summary>
internal static class MatrixOutcomes
{
    public const string Allow = "ALLOW";
    public const string Refer = "REFER";
    public const string Deny = "DENY";

    public static readonly string[] All = [Allow, Refer, Deny];

    /// <summary>Restrictiveness for tie-breaks: higher = more restrictive.</summary>
    public static int Rank(string outcome) => outcome switch { Deny => 2, Refer => 1, _ => 0 };
}

/// <summary>Lifecycle of a matrix version. DRAFT, then SUBMITTED by the maker, then SCHEDULED by a different checker (effective-dated).</summary>
internal static class MatrixStatuses
{
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Scheduled = "SCHEDULED";

    public static readonly string[] All = [Draft, Submitted, Scheduled];
}

/// <summary>Who set an entry: EU and COUNTRY entries are final (immutable); LE entries are the legal entity's own.</summary>
internal static class MatrixLevels
{
    public const string Eu = "EU";
    public const string Country = "COUNTRY";
    public const string LegalEntity = "LE";

    public static bool IsFinal(string level) => level is Eu or Country;
}

/// <summary>The dimension values. <see cref="Any"/> is the wildcard.</summary>
internal static class MatrixDims
{
    public const string Any = "*";

    /// <summary>Channel codes (CHN code list).</summary>
    public static readonly string[] Channels = ["WEB_DIRECT", "AGENCY", "STAFF", "API", "AI_CHN"];

    /// <summary>Actor types.</summary>
    public static readonly string[] ActorTypes = ["CUSTOMER", "AGENT", "STAFF", "PARTNER", "AI_AGENT"];

    /// <summary>The transaction catalogue. Anything else is CHN-ERR-UNKNOWN-TRANSACTION (REQ-CHN-049).</summary>
    public static readonly string[] Transactions =
    [
        "policy.change", "policy.renewal.accept", "policy.renewal.decline", "policy.cancel", "policy.bind", "contact.update",
        "withdrawal.declare", "claim.notify", "payment.pay", "document.download", "quote.start",
    ];
}

/// <summary>One matrix entry as the engine sees it.</summary>
internal sealed record MatrixEntry(string TransactionType, string Channel, string ActorType, string ProductLine, string Outcome, decimal? MaxAmount, string Level)
{
    /// <summary>The identity of an entry inside a matrix.</summary>
    public string Key => $"{TransactionType}|{Channel}|{ActorType}|{ProductLine}";

    /// <summary>Specificity (REQ-CHN-049): an exact transaction counts most, then actor, channel and product line. Higher wins.</summary>
    public int Specificity =>
        (TransactionType != MatrixDims.Any ? 8 : 0) + (ActorType != MatrixDims.Any ? 4 : 0) + (Channel != MatrixDims.Any ? 2 : 0) + (ProductLine != MatrixDims.Any ? 1 : 0);
}

/// <summary>What is asked: all dimensions are already normalised.</summary>
internal sealed record MatrixQuery(string TransactionType, string Channel, string ActorType, string? ProductLine, decimal? Amount);

/// <summary>The decision with its reasons.</summary>
internal sealed record MatrixDecision(string Outcome, MatrixEntry? Entry, decimal? MaxAmount, string[] Reasons, string ExplanationKey);

/// <summary>The pure decision logic: most specific match wins; a tie goes to the more restrictive outcome; nothing matching is DENY (fail closed).</summary>
internal static class MatrixEngine
{
    public static MatrixDecision Evaluate(IReadOnlyCollection<MatrixEntry> entries, MatrixQuery query)
    {
        MatrixEntry? best = null;
        foreach (var e in entries)
        {
            if (!Matches(e.TransactionType, query.TransactionType) || !Matches(e.Channel, query.Channel) || !Matches(e.ActorType, query.ActorType)
                || !Matches(e.ProductLine, query.ProductLine ?? MatrixDims.Any))
            {
                continue;
            }

            if (best is null || e.Specificity > best.Specificity || (e.Specificity == best.Specificity && MatrixOutcomes.Rank(e.Outcome) > MatrixOutcomes.Rank(best.Outcome)))
            {
                best = e;
            }
        }

        if (best is null)
        {
            return new MatrixDecision(MatrixOutcomes.Deny, null, null, ["NO_MATCHING_ENTRY"], "chn.permission.noEntry");
        }

        if (best.Outcome == MatrixOutcomes.Allow && best.MaxAmount is { } max)
        {
            if (query.Amount is null)
            {
                return new MatrixDecision(MatrixOutcomes.Refer, best, max, ["AMOUNT_MISSING"], "chn.permission.amountMissing");
            }

            if (query.Amount > max)
            {
                return new MatrixDecision(MatrixOutcomes.Refer, best, max, ["LIMIT_EXCEEDED"], "chn.permission.limitExceeded");
            }
        }

        return new MatrixDecision(best.Outcome, best, best.MaxAmount, [$"ENTRY_{best.Outcome}"], $"chn.permission.{best.Outcome.ToLowerInvariant()}");
    }

    private static bool Matches(string entryValue, string queryValue) => entryValue == MatrixDims.Any || string.Equals(entryValue, queryValue, StringComparison.Ordinal);

    /// <summary>The key-by-key difference of two entry sets.</summary>
    public static object Diff(IReadOnlyCollection<MatrixEntry> older, IReadOnlyCollection<MatrixEntry> newer)
    {
        var a = older.ToDictionary(e => e.Key);
        var b = newer.ToDictionary(e => e.Key);
        return new
        {
            added = b.Where(kv => !a.ContainsKey(kv.Key)).Select(kv => Describe(kv.Value)).ToArray(),
            removed = a.Where(kv => !b.ContainsKey(kv.Key)).Select(kv => Describe(kv.Value)).ToArray(),
            changed = b.Where(kv => a.TryGetValue(kv.Key, out var old) && (old.Outcome != kv.Value.Outcome || old.MaxAmount != kv.Value.MaxAmount))
                .Select(kv => new { key = kv.Key, before = Describe(a[kv.Key]), after = Describe(kv.Value) }).ToArray(),
        };
    }

    public static object Describe(MatrixEntry e) => new
    {
        transactionType = e.TransactionType,
        channel = e.Channel,
        actorType = e.ActorType,
        productLine = e.ProductLine,
        outcome = e.Outcome,
        maxAmount = e.MaxAmount?.ToString(CultureInfo.InvariantCulture),
        level = e.Level,
    };
}

/// <summary>The starting matrix of a legal entity with none yet. The EU rows are final: an AI agent never binds or cancels without a human.</summary>
internal static class MatrixBaseline
{
    public static IReadOnlyList<MatrixEntry> Entries { get; } = Build();

    private static List<MatrixEntry> Build()
    {
        var list = new List<MatrixEntry> { Row("*", "*", "*", "*", MatrixOutcomes.Deny) };
        foreach (var t in new[] { "policy.renewal.accept", "policy.renewal.decline", "contact.update", "withdrawal.declare", "claim.notify", "payment.pay", "document.download", "quote.start", "policy.bind" })
        {
            list.Add(Row(t, "*", "CUSTOMER", "*", MatrixOutcomes.Allow));
        }

        list.Add(Row("policy.change", "*", "CUSTOMER", "*", MatrixOutcomes.Allow, 500m));
        list.Add(Row("policy.cancel", "*", "CUSTOMER", "*", MatrixOutcomes.Refer));
        foreach (var t in new[] { "policy.change", "policy.renewal.accept", "policy.renewal.decline", "contact.update", "claim.notify", "document.download", "quote.start", "policy.bind" })
        {
            list.Add(Row(t, "AGENCY", "AGENT", "*", MatrixOutcomes.Allow));
        }

        list.Add(Row("policy.cancel", "AGENCY", "AGENT", "*", MatrixOutcomes.Refer));
        list.Add(Row("quote.start", "*", "AI_AGENT", "*", MatrixOutcomes.Allow));
        list.Add(Row("policy.bind", "*", "AI_AGENT", "*", MatrixOutcomes.Refer, level: MatrixLevels.Eu));
        list.Add(Row("policy.cancel", "*", "AI_AGENT", "*", MatrixOutcomes.Refer, level: MatrixLevels.Eu));
        return list;
    }

    private static MatrixEntry Row(string t, string c, string a, string p, string o, decimal? max = null, string level = MatrixLevels.LegalEntity) => new(t, c, a, p, o, max, level);
}
