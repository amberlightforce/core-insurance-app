using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Policy.Commands;
using CoreIns.Modules.Migration.Contracts.Spi;
using CoreIns.Modules.Policy.Contracts;
using CoreIns.Modules.Policy.Contracts.Api;
using CoreIns.Modules.Policy.Contracts.Events;
using CoreIns.Modules.Policy.Domain;
using CoreIns.Modules.Policy.Persistence;
using CoreIns.Modules.Policy.Services;
using CoreIns.Modules.Product.Contracts;
using CoreIns.Modules.Product.Contracts.Api;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CoreIns.Modules.Policy.MigrationImport;

/// <summary>The import modes of a migrated policy.</summary>
internal static class PolicyImportModes
{
    /// <summary>Mid-term: an OPENING transaction carries the in-force term; its deltas are opening balances.</summary>
    public const string Opening = "OPENING";

    /// <summary>Renewal-based conversion: the term is imported as expiring data and the existing renewal command takes it on.</summary>
    public const string RenewalBased = "RENEWAL_BASED";

    public const string ObjectType = "Policy";
}

/// <summary>One charge of the legacy written premium: per element x charge type (the element is the vehicle).</summary>
internal sealed record ImportCharge(string CoverageCode, string ChargeType, string? ChargeCategory, decimal Amount, decimal? Rate, string? LegalStatus, bool? Provisional);

/// <summary>The legacy risk: one vehicle, its drivers and the selected coverages.</summary>
internal sealed record ImportRisk(Vehicle Vehicle, List<Driver>? Drivers, List<string>? Coverages);

/// <summary>The legacy policy payload (SL6-MIG-POL-IMPORT). Premium, IPT and levies are taken as given: no re-rating.</summary>
internal sealed record ImportPolicyPayload(
    string LegacyNumber,
    string? Mode,
    PartyId PolicyholderPartyId,
    string Product,
    string? ProducerCode,
    string? Currency,
    DateTimeOffset TermStart,
    DateTimeOffset? TermEnd,
    string? Status,
    string? PaymentPlan,
    ImportRisk Risk,
    List<ImportCharge> Charges);

/// <summary>
/// <c>IMigrationImporter</c> for module POL (SL6-MIG-POL-IMPORT). Writes policy, term, an OPENING transaction, the segment and the
/// charge deltas (flagged <c>opening</c>) exactly as a bind would, stamped origin=MIGRATION, with the batch baseline hash as the
/// configuration the term pins. The imported premium is the legacy premium: nothing is rated. Idempotency is the framework's xref;
/// a repeated legacy number also hits <c>ux_migration_import_legacy</c>.
/// </summary>
internal sealed class PolicyMigrationImporter(
    PolicyDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    INumberingService numbering,
    IEventPublisher events,
    RiskTrees riskTrees,
    Dependency<IProductProductVersionService> productVersions,
    Dependency<IMarketConfigurationService> marketConfiguration,
    IOptions<PolicyOptions> options) : IMigrationImporter
{
    private const string Validation = "MIG-ERR-VALIDATION";

    public string ModuleName => "POL";

    public async Task<ImportOutcome> ImportAsync(MigrationImportItem item, MigrationImportContext importContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(importContext);
        if (!string.Equals(item.ObjectType, PolicyImportModes.ObjectType, StringComparison.Ordinal))
        {
            return Failed(Validation, $"POL imports object type Policy, not '{item.ObjectType}'.");
        }

        ImportPolicyPayload? payload;
        try
        {
            payload = item.Payload.Deserialize<ImportPolicyPayload>(SharedKernelJson.Options);
        }
        catch (JsonException ex)
        {
            return Failed(Validation, "The policy payload is not valid: " + ex.Message);
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.LegacyNumber) || payload.Charges is not { Count: > 0 } || payload.Risk?.Vehicle is null)
        {
            return Failed(Validation, "A policy needs legacyNumber, risk.vehicle and at least one charge.");
        }

        var mode = payload.Mode ?? PolicyImportModes.Opening;
        if (mode is not (PolicyImportModes.Opening or PolicyImportModes.RenewalBased))
        {
            return Failed(Validation, $"Unknown import mode '{mode}' (OPENING or RENEWAL_BASED).");
        }

        // TODO(hardening): cancelled, lapsed and suspended legacy policies; only in-force ones are imported.
        if (!string.Equals(payload.Status ?? "IN_FORCE", "IN_FORCE", StringComparison.Ordinal))
        {
            return Failed("MIG-ERR-UNSUPPORTED-STATUS", $"Only IN_FORCE policies are imported, not '{payload.Status}'.");
        }

        var zone = options.Value.Zone;
        var now = PolicyWriteLock.ForNewPolicy(clock);
        var start = Instant.FromUtcDateTime(payload.TermStart.UtcDateTime);
        var end = payload.TermEnd is { } e ? Instant.FromUtcDateTime(e.UtcDateTime) : PolicyTime.AnnualEnd(start, zone);
        if (end <= start)
        {
            return Failed(Validation, "termEnd must be after termStart.");
        }

        if (end <= now)
        {
            return Failed(Validation, "The legacy term has already ended; only an in-force term can be imported.");
        }

        // TODO(hardening): terms that are not exactly one year (the annual rate would need un-proration of the written premium).
        if (end != PolicyTime.AnnualEnd(start, zone))
        {
            return Failed(Validation, "Only annual terms are imported (termEnd = termStart + 12 months).");
        }

        var currency = payload.Currency is { } code ? Currency.FromCode(code) : options.Value.Currency;
        var legalEntity = JobSupport.LegalEntity(context, legalEntities);
        var jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value;
        if (await db.MigrationImports.AnyAsync(m => m.LegalEntityId == legalEntity && m.LegacyNumber == payload.LegacyNumber, cancellationToken).ConfigureAwait(false))
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy policy {payload.LegacyNumber} is already imported.");
        }

        // The risk tree through the same editing rules as a draft: static locators, plate normalised by the market pack.
        var instructions = new List<DraftInstruction>
        {
            new() { Op = DraftInstruction.OpValue.SetVehicle, Vehicle = payload.Risk.Vehicle with { Locator = null } },
        };
        instructions.AddRange((payload.Risk.Drivers ?? []).Select(d => new DraftInstruction { Op = DraftInstruction.OpValue.SetDriver, Driver = d with { Locator = null } }));
        var built = await riskTrees.ApplyAsync(RiskTrees.Empty, instructions, jurisdiction, cancellationToken).ConfigureAwait(false);
        if (built.IsFailure)
        {
            return Failed(Validation, built.Error!.Detail ?? built.Error!.Code.ToString());
        }

        var vehicleLocator = built.Value.Vehicles[0].Locator!;
        var coverageCodes = payload.Risk.Coverages is { Count: > 0 } selected ? selected : [.. payload.Charges.Select(c => c.CoverageCode).Distinct(StringComparer.Ordinal)];
        var tree = built.Value with
        {
            Drivers = [.. built.Value.Drivers.Select(d => d with { VehicleLocator = d.VehicleLocator ?? vehicleLocator })],
            Coverages = [.. coverageCodes.Select(code => new CoverageSelection { CoverageCode = code, ElementLocator = vehicleLocator, Selected = true })],
        };

        var charges = new List<ChargeLine>();
        foreach (var charge in payload.Charges)
        {
            if (string.IsNullOrWhiteSpace(charge.CoverageCode) || string.IsNullOrWhiteSpace(charge.ChargeType))
            {
                return Failed(Validation, "Every charge needs coverageCode and chargeType.");
            }

            var amount = new Money(charge.Amount, currency);
            if (!amount.IsRoundedToMinorUnits)
            {
                return Failed(Validation, $"Charge {charge.ChargeType} ({charge.Amount.ToString(CultureInfo.InvariantCulture)}) has more decimals than the currency allows.");
            }

            var category = charge.ChargeCategory ?? ChargeCategories.Premium;
            charges.Add(new ChargeLine
            {
                ElementLocator = vehicleLocator, CoverageCode = charge.CoverageCode, ChargeType = charge.ChargeType, ChargeCategory = category,

                // Annual term: the term amount is the annual rate (premium); a tax or levy line carries its rate.
                AnnualRate = category == ChargeCategories.Premium ? charge.Amount : charge.Rate ?? 0m,
                Amount = amount, LegalStatus = charge.LegalStatus, Provisional = charge.Provisional,
            });
        }

        var (premium, taxes, total) = Charges.Totals(charges, currency);
        ProductVersionResolveResponse resolved;
        try
        {
            // The pinned artefacts are the product version in force now for the term (a legacy term may pre-date the first version).
            // TODO(hardening): pin the version in force at termStart when the product history reaches back that far.
            resolved = await productVersions.Value.ResolveAsync(
                new ProductVersionResolveRequest
                {
                    Jurisdiction = jurisdiction, LegalEntity = context.LegalEntity!.Value.Value, Product = payload.Product, Channel = "STAFF",
                    TransactionType = ProductVersionResolveRequest.TransactionTypeValue.NewBusiness,
                },
                ValidAt.From(Instant.Max(start, now)), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException ex)
        {
            return Failed("MIG-ERR-DEPENDENCY", $"No product version is available for {payload.Product}: {ex.Error.Code}.");
        }

        if (importContext.DryRun)
        {
            return new ImportOutcome(ImportStatus.Imported, null, null, $"Would import policy {payload.LegacyNumber} ({mode}), premium {premium}, taxes {taxes}.");
        }

        ConfigurationHash configuration;
        if (ConfigurationHash.TryParse(importContext.BaselineHash, out var baseline))
        {
            configuration = baseline;
        }
        else
        {
            var current = (await marketConfiguration.Value.CurrentHashAsync(cancellationToken).ConfigureAwait(false)).Hash
                          ?? throw new InvalidOperationException("No configuration hash is pinned (REQ-POL-088).");
            configuration = new ConfigurationHash(current);
        }

        var today = now.ToBusinessDate(zone);
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.Policy, today), cancellationToken).ConfigureAwait(false);
        var policyNumber = PolicyNumber.Parse(number.Value);
        var policyId = PolicyId.New();
        var termId = PolicyTermId.New();
        var transactionId = PolicyTransactionId.New();
        var syntheticJobId = JobId.New();
        var termState = PolicyTermStateModel.Machine.Start(start > now ? PolicyTermState.Scheduled : PolicyTermState.InForce).Value;
        var actor = context.Actor.ToString();
        var snapshot = JobSupport.Json(tree);
        var ratingHash = resolved.ResolutionManifest.RatingArtefactHash?.Value;

        db.Policies.Add(new PolicyRow
        {
            PolicyId = policyId, LegalEntityId = legalEntity, Jurisdiction = jurisdiction, PolicyNumber = policyNumber, ProductCode = payload.Product,
            PolicyholderPartyId = payload.PolicyholderPartyId, RecordedAt = now, CreatedBy = actor, RecordVersion = 1, LastRecordedAt = now,
        });
        db.Terms.Add(new PolicyTermRow
        {
            TermVersionId = Guid.CreateVersion7(), TermId = termId, PolicyId = policyId, LegalEntityId = legalEntity, TermNumber = 1,
            ValidFrom = start, ValidTo = end, RecordedFrom = now, State = Codes.Of(termState), ProductVersion = resolved.Version.ToString(),
            ArtefactHash = resolved.ArtefactHash.Value, RatingArtefactHash = ratingHash, ResolutionHash = resolved.ResolutionHash.Hash.Value,
            ConfigurationHash = configuration.Hash.Value, Currency = currency.Code, ProducerCode = payload.ProducerCode,
            PaymentPlanRef = payload.PaymentPlan ?? "LEGACY", WrittenDate = today, HeadTransactionId = transactionId, CreatedBy = actor,
        });
        db.Transactions.Add(new PolicyTransactionRow
        {
            TransactionId = transactionId, PolicyId = policyId, TermId = termId, JobId = syntheticJobId, LegalEntityId = legalEntity,
            Kind = Codes.Of(PolicyTransactionKind.Opening), Sequence = 1, EffectiveAt = start, RecordedAt = now,
            ConfigurationHash = configuration.Hash.Value, ArtefactHash = resolved.ArtefactHash.Value, RatingArtefactHash = ratingHash,
            ResolutionHash = resolved.ResolutionHash.Hash.Value,
            Intent = JobSupport.Json(new { migration = new { legacyNumber = payload.LegacyNumber, mode, batchId = importContext.BatchId, baselineHash = importContext.BaselineHash }, riskTree = tree }),
            Premium = premium.Amount, Taxes = taxes.Amount, Total = total.Amount, Currency = currency.Code, Actor = actor,
            CorrelationId = context.CorrelationId.ToString(), Origin = EventOrigin.Migration.ToCode(),
        });
        db.Segments.Add(new SegmentRow
        {
            SegmentId = SegmentId.New(), TermId = termId, PolicyId = policyId, TransactionId = transactionId, LegalEntityId = legalEntity,
            ValidFrom = start, ValidTo = end, RecordedFrom = now, SnapshotHash = CanonicalJson.Hash(snapshot).Value, Snapshot = snapshot,
        });
        db.MigrationImports.Add(new MigrationImportRow
        {
            PolicyId = policyId, TermId = termId, LegalEntityId = legalEntity, BatchId = importContext.BatchId, LegacyNumber = payload.LegacyNumber,
            Mode = mode, BaselineHash = importContext.BaselineHash, ImportedAt = now,
        });

        var period = PolicyTime.Dates(start, end, zone);
        for (var i = 0; i < charges.Count; i++)
        {
            var line = charges[i];
            var chargeId = ChargeId.New();
            db.ChargeLines.Add(new ChargeLineRow
            {
                ChargeId = chargeId, TransactionId = transactionId, TermId = termId, PolicyId = policyId, LegalEntityId = legalEntity,
                ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode, ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory,
                DeltaKind = DeltaKinds.Net, AnnualRate = line.AnnualRate, Amount = line.Amount.Amount, Currency = currency.Code,
                ValidFrom = period.Start, ValidTo = period.End!.Value, BookingDate = today, CorrelationKey = transactionId.Value.ToString(),
                SetIndex = i + 1, SetSize = charges.Count, RecordedAt = now, LegalStatus = line.LegalStatus, Provisional = line.Provisional,
                TransactionKind = Codes.Of(TaxTransactionKind.NewBusiness), Opening = true,
            });
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(ChargeDeltaEmittedV1.Descriptor), "Policy", policyId.Value.ToString(),
                new ChargeDeltaEmittedV1
                {
                    ChargeId = chargeId, TermId = termId, ElementLocator = line.ElementLocator, CoverageCode = line.CoverageCode,
                    ChargeType = line.ChargeType, ChargeCategory = line.ChargeCategory, DeltaKind = DeltaKinds.Net, NetAmount = line.Amount,
                    ValidPeriod = period, BookingDate = today, TransactionId = transactionId, CorrelationKey = transactionId.Value.ToString(),
                    LegalStatus = line.LegalStatus, Provisional = line.Provisional, Opening = true,
                    TransactionKind = ChargeDeltaEmittedV1.TransactionKindValue.NewBusiness,
                },
                BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("chargeId", chargeId.Value.ToString())
                    .With("policyTermId", termId.Value.ToString()).With("transactionId", transactionId.Value.ToString()))
            {
                OccurredAt = now,
                Set = new EventSet(transactionId.Value, charges.Count, i + 1),
            });
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(PolicyBoundV1.Descriptor), "Policy", policyId.Value.ToString(),
            new PolicyBoundV1
            {
                PolicyId = policyId, PolicyNumber = policyNumber, TermId = termId, TermNumber = 1, TransactionId = transactionId,
                EffectivePeriod = InstantRange.Of(start, end), ProductCode = payload.Product,
                ProductVersion = ProductVersionNumber.Parse(resolved.Version.ToString()), ArtefactHash = Sha256Hash.Parse(resolved.ArtefactHash.Value),
                ResolutionHash = ResolutionHash.Parse(resolved.ResolutionHash.Hash.Value),
                ConfigurationHash = configuration, ProducerOfRecord = payload.ProducerCode, PayerPartyId = payload.PolicyholderPartyId,
                PaymentPlanRef = payload.PaymentPlan ?? "LEGACY", Currency = currency,
                Ifrs17Tags = JsonSerializer.SerializeToElement(new { cohort = start.ToBusinessDate(zone).Value.Year.ToString(CultureInfo.InvariantCulture), measurementModel = "PAA" }),
                Motor = true,
            },
            BusinessKeys.Empty.With("policyId", policyId.Value.ToString()).With("policyTermId", termId.Value.ToString())
                .With("transactionId", transactionId.Value.ToString()).With("jobId", syntheticJobId.Value.ToString())
                .With("quoteId", QuoteId.New().Value.ToString()))
        {
            OccurredAt = now,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation })
        {
            return Failed("MIG-ERR-DUPLICATE", $"Legacy policy {payload.LegacyNumber} conflicts with an existing record.");
        }

        return new ImportOutcome(ImportStatus.Imported, policyId.Value, null, $"Policy {policyNumber.Value} ({mode}).");
    }

    private static ImportOutcome Failed(string code, string message) => new(ImportStatus.Failed, null, code, message);
}
