using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using Dapper;
using Npgsql;

namespace CoreIns.Modules.Market.Queries;

/// <summary>A legal entity of the registry (REQ-MKT-151).</summary>
/// <param name="Id">Row id (UUID), used in rows, keys and SPIs.</param>
/// <param name="Code">Code used in the event envelope and configuration, for example <c>GR-TEST</c>.</param>
/// <param name="HomeJurisdiction">Home jurisdiction (ISO 3166-1).</param>
/// <param name="PackId">Country pack the entity runs on.</param>
/// <param name="FunctionalCurrency">Functional currency (REQ-MKT-190).</param>
/// <param name="TimeZone">IANA time zone in which business dates are computed (REQ-MKT-187).</param>
/// <param name="Status">Lifecycle status code.</param>
/// <param name="IsTestEntity">Synthetic entity for tests and the local stack.</param>
internal sealed record LegalEntityInfo(
    LegalEntityId Id, LegalEntityCode Code, string HomeJurisdiction, string PackId, string FunctionalCurrency, string TimeZone, string Status, bool IsTestEntity)
{
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

/// <summary>
/// The legal-entity registry as a process-wide read-through cache of <c>mkt.legal_entity</c> (D-CON-33). The registry has
/// no write path in the slice (one seeded entity, GR-TEST), so a short time-to-live is enough; the lookup is synchronous
/// because <see cref="ILegalEntityDirectory"/> is. Rows are read on the application role's data source.
/// </summary>
internal sealed class LegalEntityRegistry
{
    private static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);
    private readonly NpgsqlDataSource? _dataSource;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private List<LegalEntityInfo> _entities = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>Registry over the database (the Host).</summary>
    public LegalEntityRegistry(NpgsqlDataSource dataSource, TimeProvider time)
    {
        _dataSource = dataSource;
        _time = time;
    }

    /// <summary>A fixed registry for tests: no database is read.</summary>
    public LegalEntityRegistry(IReadOnlyList<LegalEntityInfo> fixedEntities)
    {
        _time = TimeProvider.System;
        _entities = [.. fixedEntities];
        _loadedAt = DateTimeOffset.MaxValue;
    }

    /// <summary>Every legal entity of the deployment.</summary>
    public IReadOnlyList<LegalEntityInfo> All()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_entities.Count == 0 || now - _loadedAt > TimeToLive)
            {
                _entities = Load();
                _loadedAt = now;
            }

            return _entities;
        }
    }

    /// <summary>The entity with <paramref name="code"/>, or null.</summary>
    public LegalEntityInfo? Find(LegalEntityCode code) => All().FirstOrDefault(e => e.Code == code);

    private List<LegalEntityInfo> Load()
    {
        using var connection = _dataSource!.OpenConnection();
        var rows = connection.Query<Row>(
            """
            SELECT legal_entity_id AS LegalEntityId, code AS Code, home_jurisdiction AS HomeJurisdiction, pack_id AS PackId,
                   functional_currency AS FunctionalCurrency, timezone AS TimeZone, status AS Status, is_test_entity AS IsTestEntity
              FROM mkt.legal_entity
             ORDER BY code
            """);
        return [.. rows.Select(r => new LegalEntityInfo(
            new LegalEntityId(r.LegalEntityId), LegalEntityCode.Parse(r.Code), r.HomeJurisdiction.Trim(), r.PackId, r.FunctionalCurrency.Trim(), r.TimeZone, r.Status, r.IsTestEntity))];
    }

    private sealed class Row
    {
        public Guid LegalEntityId { get; set; }

        public string Code { get; set; } = string.Empty;

        public string HomeJurisdiction { get; set; } = string.Empty;

        public string PackId { get; set; } = string.Empty;

        public string FunctionalCurrency { get; set; } = string.Empty;

        public string TimeZone { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public bool IsTestEntity { get; set; }
    }
}

/// <summary>
/// The platform's <see cref="ILegalEntityDirectory"/> backed by the MKT registry; replaces the synthetic
/// <c>Stamp:LegalEntityId</c> placeholder (D-CON-33).
/// </summary>
internal sealed class MarketLegalEntityDirectory(LegalEntityRegistry registry) : ILegalEntityDirectory
{
    public LegalEntityId Resolve(LegalEntityCode code) =>
        registry.Find(code)?.Id ?? throw new InvalidOperationException($"Legal entity {code} is not in the MKT registry.");

    public IReadOnlyList<LegalEntityId> All => [.. registry.All().Select(e => e.Id)];
}
