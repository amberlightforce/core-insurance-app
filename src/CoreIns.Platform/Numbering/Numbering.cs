using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Persistence;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace CoreIns.Platform.Numbering;

/// <summary>
/// Identifier types issued by the numbering service (REQ-PLT-014, REQ-PLT-209). The codes are the
/// <c>identifierType</c> argument of the <c>NumberingScheme</c> SPI. Fiscal series are never numbered here: CMP issues
/// them through <c>FiscalDocumentChannel.series</c> (D3).
/// </summary>
public static class NumberingSchemes
{
    /// <summary>Party number (REQ-PTY-034).</summary>
    public const string Party = "PARTY";

    /// <summary>Account number (REQ-PTY-004).</summary>
    public const string Account = "ACCOUNT";

    /// <summary>Producer code (REQ-PTY-204).</summary>
    public const string ProducerCode = "PRODUCER_CODE";

    /// <summary>Job (submission / quote) number (REQ-POL-030).</summary>
    public const string Job = "JOB";

    /// <summary>Policy number (REQ-POL-030).</summary>
    public const string Policy = "POLICY";

    /// <summary>Non-fiscal invoice / payment demand number (REQ-BIL-030).</summary>
    public const string Invoice = "INVOICE";

    /// <summary>Receipt number (REQ-BIL-086).</summary>
    public const string Receipt = "RECEIPT";

    /// <summary>Claim number (REQ-CLM-001).</summary>
    public const string Claim = "CLAIM";

    /// <summary>Journal number (REQ-FIN-069).</summary>
    public const string Journal = "JOURNAL";
}

/// <summary>Whether a series may have gaps (REQ-PLT-210, REQ-PLT-211).</summary>
public enum GapPolicy
{
    /// <summary>
    /// Allocated inside the caller's transaction under a row lock: a rollback does not consume the number. Callers of one
    /// series serialise until commit, so use it only where the law or the business needs gapless series.
    /// </summary>
    Gapless,

    /// <summary>Allocated from blocks reserved in short independent transactions (REQ-PLT-212: ≥ 500 numbers/s); unused numbers of a block are lost on restart.</summary>
    GapsAllowed,
}

/// <summary>When a series restarts (REQ-PLT-210).</summary>
public enum ResetRule
{
    /// <summary>One series forever.</summary>
    Never,

    /// <summary>A new series per calendar year of <c>validAt</c>; the year is part of the number so numbers stay unique.</summary>
    Yearly,
}

/// <summary>Series definition of one identifier type (core default format, REQ-MKT-096: prefix + zero-padded sequence, no personal data).</summary>
public sealed class NumberingSchemeOptions
{
    /// <summary>Constant prefix (upper-case letters, digits and '-', at most 10 characters).</summary>
    public string Prefix { get; set; } = string.Empty;

    /// <summary>Digits of the zero-padded sequence (4..18). The range ends at 10^Width − 1.</summary>
    public int Width { get; set; } = 9;

    /// <summary>Gap policy.</summary>
    public GapPolicy GapPolicy { get; set; } = GapPolicy.Gapless;

    /// <summary>Numbers reserved per block for <see cref="GapPolicy.GapsAllowed"/>.</summary>
    public int BlockSize { get; set; } = 50;

    /// <summary>Reset rule.</summary>
    public ResetRule Reset { get; set; } = ResetRule.Never;
}

/// <summary>Configuration section <c>Platform:Numbering</c>: one entry per identifier type.</summary>
public sealed partial class NumberingOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Platform:Numbering";

    /// <summary>Identifier type → series definition. A type without an entry cannot be numbered (fail closed).</summary>
    public Dictionary<string, NumberingSchemeOptions> Schemes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Refuses definitions that could encode personal data or break uniqueness (REQ-PLT-213): the format has no
    /// placeholders at all, only a constant prefix, the year (yearly series) and the sequence.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        foreach (var (type, scheme) in Schemes)
        {
            if (!TypePattern().IsMatch(type))
            {
                errors.Add($"Identifier type '{type}' must be upper-snake case.");
            }

            if (!PrefixPattern().IsMatch(scheme.Prefix ?? string.Empty))
            {
                errors.Add($"{type}: the prefix may hold only A-Z, 0-9 and '-', at most 10 characters.");
            }

            if (scheme.Width is < 4 or > 18)
            {
                errors.Add($"{type}: Width must be between 4 and 18.");
            }

            if (scheme.BlockSize is < 1 or > 10_000)
            {
                errors.Add($"{type}: BlockSize must be between 1 and 10000.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    [GeneratedRegex("^[A-Z0-9-]{0,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();
}

/// <summary>A request for the next number of a series (<c>plt.Number.next(scheme, context)</c>).</summary>
/// <param name="IdentifierType">One of <see cref="NumberingSchemes"/> (or a pack-defined type).</param>
/// <param name="ValidAt">Business date (yearly series roll on it); defaults to today in UTC.</param>
public sealed record NumberRequest(string IdentifierType, BusinessDate? ValidAt = null);

/// <summary>An issued number.</summary>
/// <param name="Value">Formatted business number.</param>
/// <param name="IdentifierType">Identifier type.</param>
/// <param name="SeriesId">Series it came from.</param>
/// <param name="Sequence">Sequence within the series.</param>
public sealed record IssuedNumber(string Value, string IdentifierType, string SeriesId, long Sequence);

/// <summary>
/// The pack seam of the numbering service (SPI <c>NumberingScheme</c>, REQ-MKT-096): series and formats. The platform
/// cannot reference the MKT contracts, so the Host binds a pack's <c>INumberingScheme</c> through an adapter when a pack
/// provides one; otherwise <see cref="CoreNumberFormat"/> (the SPI's core default) applies.
/// </summary>
public interface INumberFormat
{
    /// <summary>Series of the type for the context (e.g. <c>default</c>, or the year for yearly series).</summary>
    string Series(string identifierType, NumberingSchemeOptions scheme, BusinessDate validAt);

    /// <summary>Formats a sequence of the series.</summary>
    string Format(string identifierType, NumberingSchemeOptions scheme, string seriesId, long sequence);
}

/// <summary>The SPI's core default: <c>prefix + [year] + zero-padded sequence</c>; never derived from personal data.</summary>
public sealed class CoreNumberFormat : INumberFormat
{
    /// <summary>Series id of a series that never resets.</summary>
    public const string DefaultSeries = "default";

    /// <inheritdoc />
    public string Series(string identifierType, NumberingSchemeOptions scheme, BusinessDate validAt)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        return scheme.Reset == ResetRule.Yearly ? validAt.Value.Year.ToString("D4", CultureInfo.InvariantCulture) : DefaultSeries;
    }

    /// <inheritdoc />
    public string Format(string identifierType, NumberingSchemeOptions scheme, string seriesId, long sequence)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var year = scheme.Reset == ResetRule.Yearly ? seriesId : string.Empty;
        return string.Create(CultureInfo.InvariantCulture, $"{scheme.Prefix}{year}{sequence.ToString("D" + scheme.Width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)}");
    }
}

/// <summary>
/// <c>plt.Number.next</c> (REQ-PLT-014, REQ-PLT-209…213): business numbers unique per legal entity and identifier type.
/// Modules call it from their command handlers, inside their unit of work: a gapless number is allocated in the
/// caller's transaction (a rollback releases it, REQ-PLT-211); a gap-allowed number comes from a reserved block.
/// </summary>
public interface INumberingService
{
    /// <summary>Issues the next number for the request context's legal entity. Fails with PLT-ERR-RANGE-EXHAUSTED.</summary>
    Task<IssuedNumber> NextAsync(NumberRequest request, CancellationToken cancellationToken);
}

/// <summary>Error names of the numbering service.</summary>
public static class NumberingErrors
{
    /// <summary>The series has no number left (REQ-PLT-014 error model).</summary>
    public const string RangeExhausted = "RANGE-EXHAUSTED";

    /// <summary>The identifier type has no series definition.</summary>
    public const string UnknownScheme = "NUMBERING-SCHEME-UNKNOWN";
}

/// <summary>Blocks of gap-allowed series reserved by this process (singleton).</summary>
internal sealed class NumberBlockCache
{
    private readonly ConcurrentDictionary<string, Block> _blocks = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, Block> Blocks => _blocks;

    internal sealed class Block
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public long Next { get; set; }

        public long End { get; set; }
    }
}

/// <summary>Default <see cref="INumberingService"/> over <c>plt.number_series</c>.</summary>
internal sealed class NumberingService(
    DbSession session,
    NpgsqlDataSource dataSource,
    RequestContext context,
    IClock clock,
    IOptions<NumberingOptions> options,
    INumberFormat format,
    NumberBlockCache blocks) : INumberingService
{
    public async Task<IssuedNumber> NextAsync(NumberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Value.Schemes.TryGetValue(request.IdentifierType, out var scheme))
        {
            throw new DomainException(DomainError.Of(ModuleCode.PLT, NumberingErrors.UnknownScheme, $"No numbering series is defined for '{request.IdentifierType}'."));
        }

        var legalEntity = context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.");
        var validAt = request.ValidAt ?? new BusinessDate(DateOnly.FromDateTime(clock.Now.ToUtcDateTime()));
        var series = format.Series(request.IdentifierType, scheme, validAt);
        var max = MaxOf(scheme);

        var sequence = scheme.GapPolicy == GapPolicy.Gapless
            ? await NextGaplessAsync(legalEntity, request.IdentifierType, series, max, cancellationToken).ConfigureAwait(false)
            : await NextFromBlockAsync(legalEntity, request.IdentifierType, series, max, scheme.BlockSize, cancellationToken).ConfigureAwait(false);

        return new IssuedNumber(format.Format(request.IdentifierType, scheme, series, sequence), request.IdentifierType, series, sequence);
    }

    private static long MaxOf(NumberingSchemeOptions scheme)
    {
        long max = 1;
        for (var i = 0; i < scheme.Width; i++)
        {
            max *= 10;
        }

        return max - 1;
    }

    /// <summary>Row-locked increment in the caller's transaction (REQ-PLT-211).</summary>
    private async Task<long> NextGaplessAsync(LegalEntityCode legalEntity, string type, string series, long max, CancellationToken cancellationToken)
    {
        var transaction = session.Transaction
            ?? throw new InvalidOperationException("Gapless numbers are allocated inside the caller's transaction (run the command pipeline first).");
        return await AllocateAsync(session.Connection, transaction, legalEntity, type, series, max, 1, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Hands out numbers of a reserved block; reserves the next block in a short independent transaction (REQ-PLT-212).</summary>
    private async Task<long> NextFromBlockAsync(
        LegalEntityCode legalEntity, string type, string series, long max, int blockSize, CancellationToken cancellationToken)
    {
        var block = blocks.Blocks.GetOrAdd($"{legalEntity}|{type}|{series}", _ => new NumberBlockCache.Block());
        await block.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (block.Next == 0 || block.Next > block.End)
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                var first = await AllocateAsync(connection, transaction, legalEntity, type, series, max, blockSize, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                block.Next = first;
                block.End = Math.Min(first + blockSize - 1, max);
            }

            return block.Next++;
        }
        finally
        {
            block.Gate.Release();
        }
    }

    /// <summary>Reserves <paramref name="count"/> numbers (at least one) and returns the first; fails when the range is used up.</summary>
    private async Task<long> AllocateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LegalEntityCode legalEntity, string type, string series, long max, int count,
        CancellationToken cancellationToken)
    {
        var now = clock.Now.ToUtcDateTime();
        await using (var ensure = new NpgsqlCommand(
            """
            INSERT INTO plt.number_series (legal_entity, identifier_type, series_id, next_value, max_value, updated_at)
            VALUES (@le, @type, @series, 1, @max, @now)
            ON CONFLICT (legal_entity, identifier_type, series_id) DO NOTHING
            """, connection, transaction))
        {
            AddKey(ensure, legalEntity, type, series);
            ensure.Parameters.Add(new NpgsqlParameter<long>("max", max));
            ensure.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
            await ensure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var next = new NpgsqlCommand(
            """
            WITH current_row AS (
                SELECT next_value FROM plt.number_series
                 WHERE legal_entity = @le AND identifier_type = @type AND series_id = @series
                 FOR UPDATE)
            UPDATE plt.number_series s
               SET next_value = LEAST(c.next_value + @count, s.max_value + 1), updated_at = @now
              FROM current_row c
             WHERE s.legal_entity = @le AND s.identifier_type = @type AND s.series_id = @series AND c.next_value <= s.max_value
            RETURNING c.next_value
            """, connection, transaction);
        AddKey(next, legalEntity, type, series);
        next.Parameters.Add(new NpgsqlParameter<long>("count", count));
        next.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });
        var first = await next.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return first is long value
            ? value
            : throw new DomainException(DomainError.Of(ModuleCode.PLT, NumberingErrors.RangeExhausted, $"Series {type}/{series} of {legalEntity} has no number left."));
    }

    private static void AddKey(NpgsqlCommand command, LegalEntityCode legalEntity, string type, string series)
    {
        command.Parameters.Add(new NpgsqlParameter<string>("le", legalEntity.Value));
        command.Parameters.Add(new NpgsqlParameter<string>("type", type));
        command.Parameters.Add(new NpgsqlParameter<string>("series", series));
    }
}
