using System.Globalization;
using CoreIns.Modules.Reinsurance.Contracts.Api;
using CoreIns.Modules.Reinsurance.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Common;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CoreIns.Modules.Reinsurance.Registry;

/// <summary>RI-ERR errors raised by the registry (the contract's x-error-codes).</summary>
internal static class RiErrors
{
    public static DomainError NotFound() => DomainError.Of(ModuleCode.RI, "NOT-FOUND", "The reinsurance contract does not exist in your legal entity.");

    public static DomainError Stale(int current) =>
        new(ErrorCode.For(ModuleCode.RI, "STALE"), "The contract changed meanwhile. Load the newer version and try again.")
        {
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal) { ["currentRecordVersion"] = current.ToString(CultureInfo.InvariantCulture) },
        };

    public static DomainError Stale(string detail) => DomainError.Of(ModuleCode.RI, "STALE", detail);

    public static DomainError State(ContractStatus status, string action) =>
        DomainError.Of(ModuleCode.RI, "STATE", $"The contract is {ContractStateModel.Code(status)}; it cannot be {action}.");

    public static DomainError Sod(string detail) => DomainError.Of(ModuleCode.RI, "SOD", detail);

    public static DomainError Validation(string field, string code, string detail) =>
        new(ErrorCode.For(ModuleCode.RI, "VALIDATION"), detail) { FieldErrors = [new FieldError(field, code, "ri." + code.ToLowerInvariant())] };

    public static DomainError ReinsurerId(string field, string code, string detail) =>
        new(ErrorCode.For(ModuleCode.RI, "REINSURER-ID"), detail) { FieldErrors = [new FieldError(field, code, "ri." + code.ToLowerInvariant())] };
}

/// <summary>Loading, locking, reading and writing the contract aggregate (header, current version, content rows).</summary>
internal static class ContractSupport
{
    /// <summary>
    /// The contract of the caller's legal entity, tracked for update and row-locked (<c>FOR UPDATE</c>) until the
    /// command's transaction ends; null when not found (another entity's contract is "not found"). The lock serialises
    /// every state-changing command on one contract: a racing command waits, then sees the winner's record_version and
    /// returns RI-ERR-STALE instead of losing on a constraint with a 500 (PITFALLS 15).
    /// </summary>
    public static async Task<ContractRow?> LockAsync(ReinsuranceDbContext db, LegalEntityId legalEntity, RiContractId contractId, CancellationToken cancellationToken)
    {
        var rows = await db.Contracts
            .FromSql($"SELECT * FROM ri.contract WHERE contract_id = {contractId.Value} AND legal_entity_id = {legalEntity.Value} FOR UPDATE")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>The contract's current (known) version, tracked.</summary>
    public static Task<ContractVersionRow> CurrentVersionAsync(ReinsuranceDbContext db, RiContractId contractId, CancellationToken cancellationToken) =>
        db.Versions.SingleAsync(v => v.ContractId == contractId && v.KnownTo == null, cancellationToken);

    /// <summary>The business content of <paramref name="version"/> as stored (the current revision of its child rows).</summary>
    public static async Task<ContractContent> ReadContentAsync(ReinsuranceDbContext db, ContractRow contract, ContractVersionRow version, CancellationToken cancellationToken)
    {
        var section = await db.Sections.AsNoTracking().SingleAsync(s => s.VersionId == version.VersionId && s.Rev == version.ContentRev, cancellationToken).ConfigureAwait(false);
        var layers = await db.Layers.AsNoTracking().Where(l => l.VersionId == version.VersionId && l.Rev == version.ContentRev).OrderBy(l => l.LayerNo)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var clause = await db.Clauses.AsNoTracking().SingleAsync(c => c.VersionId == version.VersionId && c.Rev == version.ContentRev, cancellationToken).ConfigureAwait(false);
        var lines = await db.Participations.AsNoTracking().Where(p => p.VersionId == version.VersionId && p.Rev == version.ContentRev)
            .OrderByDescending(p => p.Lead).ThenBy(p => p.ReinsurerPartyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new ContractContent(
            contract.ContractType, contract.ContractYear, contract.Currency.Trim(), version.ValidFrom, version.ValidTo,
            [.. section.ProductCodes], [.. section.CoverageCodes], clause.AlaeIncluded, clause.StatutoryInterestIncluded, clause.RecoveriesInure,
            [.. layers.Select(l => new ContentLayer(l.LayerNo, l.Attachment, l.LimitAmount, l.Aad, l.Aal))],
            [.. lines.Select(p => new ContentLine(p.ReinsurerPartyId.Value, p.BrokerPartyId, p.SignedLinePct, p.Lead))],
            version.PlacedPct);
    }

    /// <summary>Adds the child rows of <paramref name="content"/> at revision <paramref name="rev"/> (the version must already carry that revision).</summary>
    public static void AddContent(ReinsuranceDbContext db, ContractVersionRow version, int rev, ContractContent content)
    {
        var sectionId = Guid.CreateVersion7();
        db.Sections.Add(new SectionRow
        {
            SectionId = sectionId, VersionId = version.VersionId, Rev = rev, SectionNo = 1,
            ProductCodes = [.. content.ProductCodes], CoverageCodes = [.. content.CoverageCodes],
        });
        foreach (var layer in content.Layers)
        {
            db.Layers.Add(new LayerRow
            {
                LayerId = Guid.CreateVersion7(), SectionId = sectionId, VersionId = version.VersionId, Rev = rev, LayerNo = layer.LayerNo,
                Attachment = layer.Attachment, LimitAmount = layer.Limit, Aad = layer.Aad, Aal = layer.Aal, Currency = content.Currency,
            });
        }

        db.Clauses.Add(new ClauseRow
        {
            ClauseId = Guid.CreateVersion7(), VersionId = version.VersionId, Rev = rev, AlaeIncluded = content.AlaeIncluded,
            StatutoryInterestIncluded = content.StatutoryInterestIncluded, RecoveriesInure = content.RecoveriesInure,
        });
        foreach (var line in content.Lines)
        {
            db.Participations.Add(new ParticipationRow
            {
                ParticipationId = Guid.CreateVersion7(), VersionId = version.VersionId, Rev = rev, ReinsurerPartyId = new PartyId(line.ReinsurerPartyId),
                BrokerPartyId = line.BrokerPartyId, SignedLinePct = line.SignedLinePct, Lead = line.Lead,
            });
        }
    }

    /// <summary>The actor keys of whoever acts: the actor and, for a delegated actor, the person it acts for (PITFALLS 5).</summary>
    public static IReadOnlyList<string> ActorKeys(RequestContext context) =>
        context.OnBehalfOf is { } principal ? [context.Actor.ToString(), principal.ToString()] : [context.Actor.ToString()];

    /// <summary>The Athens-midnight instant that starts <paramref name="date"/>.</summary>
    public static Instant StartOf(BusinessDate date, TimeZoneInfo zone) =>
        Instant.FromUtcDateTime(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(date.Value.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone));

    /// <summary>True when a save lost a race on the named unique index or constraint.</summary>
    public static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;

    /// <summary>Amounts as stored (NUMERIC scale 4) read back with trailing zeros stripped but at least two decimals, like a request.</summary>
    internal static decimal Amount(decimal value)
    {
        var stripped = value / 1.0000000000000000000000000000m;
        return stripped.Scale < 2 ? stripped + 0.00m : stripped;
    }

    /// <summary>Percentages as stored (NUMERIC scale 6) read back with trailing zeros stripped.</summary>
    internal static decimal Percent(decimal value) => value / 1.0000000000000000000000000000m;

    /// <summary>The contract view (RiContractView) of the stored aggregate.</summary>
    public static RiContractView ToView(ContractRow contract, ContractVersionRow version, ContractContent content, string legalEntity, TimeZoneInfo zone)
    {
        var eur = Currency.FromCode(content.Currency);
        return new RiContractView
        {
            ContractId = contract.ContractId,
            ContractNumber = contract.ContractNumber,
            StableTreatyId = contract.StableTreatyId,
            LegalEntity = legalEntity,
            ContractType = RiContractType.XolPerRisk,
            ContractYear = contract.ContractYear,
            Currency = eur,
            Period = DateRange.Of(version.ValidFrom, version.ValidTo),
            Scope = new RiContractScope { ProductCodes = content.ProductCodes, CoverageCodes = content.CoverageCodes },
            Clause = new RiContractClause
            {
                AlaeIncluded = content.AlaeIncluded,
                StatutoryInterestIncluded = content.StatutoryInterestIncluded,
                RecoveriesInure = RiContractClause.RecoveriesInureValue.RealisedOnly,
            },
            Layers =
            [
                .. content.Layers.OrderBy(l => l.LayerNo).Select(l => new RiLayer
                {
                    LayerNo = l.LayerNo,
                    Attachment = new Money(Amount(l.Attachment), eur),
                    Limit = new Money(Amount(l.Limit), eur),
                    Aad = new Money(Amount(l.Aad), eur),
                    Aal = l.Aal is { } aal ? new Money(Amount(aal), eur) : null,
                }),
            ],
            Participations =
            [
                .. content.Lines.OrderByDescending(l => l.Lead).ThenBy(l => l.ReinsurerPartyId).Select(l => new RiParticipationView
                {
                    ReinsurerPartyId = new PartyId(l.ReinsurerPartyId), BrokerPartyId = l.BrokerPartyId, SignedLinePct = Percent(l.SignedLinePct), Lead = l.Lead,
                }),
            ],
            PlacedPct = Percent(content.PlacedPct),
            Status = Wire(ContractStateModel.Parse(contract.Status)),
            VersionNo = version.VersionNo,
            ValidFrom = StartOf(version.ValidFrom, zone),
            RecordVersion = contract.RecordVersion,
            Maker = contract.CreatedBy,
            ApprovalRequestId = contract.ApprovalRequestId is { } request ? new ApprovalRequestId(request) : null,
            CreatedAt = contract.CreatedAt,
            ActivatedAt = contract.ActivatedAt,
        };
    }

    /// <summary>The contract's wire status.</summary>
    public static RiContractStatus Wire(ContractStatus status) => status switch
    {
        ContractStatus.Draft => RiContractStatus.Draft,
        ContractStatus.PendingApproval => RiContractStatus.PendingApproval,
        ContractStatus.Approved => RiContractStatus.Approved,
        ContractStatus.Active => RiContractStatus.Active,
        ContractStatus.Expired => RiContractStatus.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}
