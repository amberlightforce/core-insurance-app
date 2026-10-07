using System.Text.Json;
using System.Text.Json.Nodes;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Rating.Contracts;
using CoreIns.Modules.Rating.Contracts.Api;
using CoreIns.Modules.Rating.Contracts.Events;
using CoreIns.Modules.Rating.Domain;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Events;
using CoreIns.Platform.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;

namespace CoreIns.Modules.Rating.Services;

/// <summary>
/// <c>rat.Rate.rate</c> (REQ-RAT-001, -006, -009, -030..053 subset): the in-process rating call POL makes with the product
/// version, the risk and the effective date. It is a query, not a command: it never goes through the idempotency decorator
/// (the same input always gives the same worksheet id, so a retry is naturally idempotent). The worksheet and its event
/// are written in the caller's transaction when there is one, otherwise in a transaction of their own.
/// </summary>
internal sealed class RatingRateService(
    RatingStore store,
    IMarketConfigurationService marketConfiguration,
    DbSession session,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IEventPublisher events) : IRatingRateService
{
    private const int MaxSegments = 400;

    public async Task<RateRateResponse> RateAsync(RateRateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var envelope = request.Envelope;
        var mode = envelope.Mode;
        if (mode is not (RateRateRequest.EnvelopeDetail.ModeValue.Full or RateRateRequest.EnvelopeDetail.ModeValue.Quick or RateRateRequest.EnvelopeDetail.ModeValue.DryRun))
        {
            throw Error("ENVELOPE", $"Mode {mode} is not available in this slice (FULL, QUICK and DRY_RUN are).");
        }

        if (request.Segments.Count is 0 or > MaxSegments)
        {
            throw Error("ENVELOPE", $"Send 1 to {MaxSegments} segments.");
        }

        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        if (!string.Equals(envelope.LegalEntity, context.LegalEntity.Value.Value, StringComparison.Ordinal))
        {
            throw Error("ENVELOPE", "The envelope's legal entity is not the caller's.");
        }

        await using var transaction = await session.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        var basis = envelope.RatingBasisDate.Value;
        var version = envelope.ProductVersion?.ToString();
        var artefact = await ResolveArtefactAsync(envelope, version, basis, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(envelope.Currency.Code, artefact.Definition.Currency, StringComparison.Ordinal))
        {
            throw Error("CURRENCY", $"The artefact rates in {artefact.Definition.Currency}, not {envelope.Currency.Code}.");
        }

        // 1. Premium per segment and coverage: pure, deterministic, explained step by step.
        var currency = envelope.Currency;
        var segments = new List<(string SegmentId, MotorRisk Risk, List<CoveragePremium> Premiums)>();
        foreach (var segment in request.Segments)
        {
            if (segment.ValidPeriod.End != segment.ValidPeriod.Start.AddYears(1))
            {
                throw Error("PERIOD", $"Segment {segment.SegmentId} must be one year: the slice rates annual terms only (D6).");
            }

            var risk = MotorRisk.Parse(segment.RiskTree, segment.SegmentId);
            var premiums = risk.Coverages.Select(c => RatingEngine.RateCoverage(artefact, risk, c, basis)).ToList();
            segments.Add((segment.SegmentId, risk, premiums));
        }

        // 2. Taxes and levies after the final premium: rates only from MKT configuration, fail closed (REQ-RAT-009, -112).
        var taxPoint = envelope.TaxPointDate ?? envelope.RatingBasisDate;
        var rates = await ResolveTaxRatesAsync(artefact, envelope, version, taxPoint, cancellationToken).ConfigureAwait(false);
        var taxes = new List<RateRateResponse.TaxeItem>();
        foreach (var (segmentId, _, premiums) in segments)
        {
            foreach (var premium in premiums)
            {
                var baseAmount = new Money(premium.Premium, currency);
                foreach (var plan in artefact.Definition.TaxPlan.Where(p => p.Coverages is null || p.Coverages.Contains(premium.Coverage)))
                {
                    var taxClass = plan.CoverageClasses.GetValueOrDefault(premium.Coverage, plan.DefaultClass);
                    var value = rates.Values.FirstOrDefault(v => v.Key == plan.RateKey)
                        ?? throw Error("TAX", $"MKT configuration returned no value for {plan.RateKey}; rating fails closed.");
                    var rate = RatingEngine.ReadRate(value.Value, taxClass, plan.RateKey);
                    taxes.Add(new RateRateResponse.TaxeItem
                    {
                        SegmentId = segmentId,
                        CoverageCode = premium.Coverage,
                        ChargeType = plan.ChargeType,
                        Category = plan.Category == "LEVY" ? RateRateResponse.TaxeItem.CategoryValue.Levy : RateRateResponse.TaxeItem.CategoryValue.Tax,
                        TaxClass = taxClass,
                        Base = baseAmount,
                        Rate = rate,
                        Amount = RatingEngine.TaxAmount(baseAmount, rate, plan),
                        RoundingRule = $"{plan.Places}:{plan.Mode}",
                        ConfigurationKey = plan.RateKey,
                        ConfigurationValueVersionId = value.ValueVersionId,
                    });
                }
            }
        }

        // 3. Totals, the canonical worksheet and its hash.
        var rateItems = segments.SelectMany(s => s.Premiums.Select(p => new RateRateResponse.RateItem
        {
            SegmentId = s.SegmentId,
            ElementId = s.Risk.VehicleElementId,
            ChargeType = "PREMIUM",
            ChargeCategory = "PREMIUM",
            CoverageCode = p.Coverage,
            AnnualRate = p.Premium,
            Currency = currency,
            RatingStep = artefact.Definition.Steps[^1].Id,
            Handling = RateRateResponse.RateItem.HandlingValue.Proratable,
        })).ToList();
        var premiumTotal = Money.Sum(rateItems.Select(r => new Money(r.AnnualRate, currency)), currency);
        var taxTotal = Money.Sum(taxes.Select(t => t.Amount), currency);
        var inputHash = CanonicalJson.Hash(new JsonArray(segments.Select(s => (JsonNode)new JsonObject
        {
            ["segmentId"] = s.SegmentId,
            ["risk"] = s.Risk.ToNormalised(),
        }).ToArray())).Value;
        var configurationHash = rates.ConfigurationHash;
        var dataStatus = artefact.Definition.Metadata.DataStatus;
        var worksheet = BuildWorksheet(artefact, envelope, request, segments, taxes, premiumTotal, taxTotal, inputHash, configurationHash, taxPoint, dataStatus);
        var worksheetId = CanonicalJson.Hash(worksheet);

        var warnings = new List<RateRateResponse.WarningItem>();
        if (dataStatus == DataStatus.Illustrative)
        {
            warnings.Add(new RateRateResponse.WarningItem { Code = "RAT-WARN-ILLUSTRATIVE-TARIFF" });
        }

        // 4. Persist (not for DRY_RUN) with the event, in the caller's transaction (REQ-RAT-003).
        if (mode != RateRateRequest.EnvelopeDetail.ModeValue.DryRun)
        {
            await store.SaveWorksheetAsync(
                worksheetId.Value, legalEntity.Value, envelope.Jurisdiction, artefact.Hash.Value, configurationHash.ToString(), inputHash, dataStatus,
                worksheet.ToJsonString(), envelope.Lineage?.QuoteId, envelope.Lineage?.JobId, envelope.Lineage?.TransactionId, mode.ToString().ToUpperInvariant(),
                cancellationToken).ConfigureAwait(false);
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(RatingCalculatedV1.Descriptor), "RatingRequest", worksheetId.Value,
                new RatingCalculatedV1
                {
                    RatingKey = $"{envelope.ProductArtefactHash}:{artefact.Hash}:{configurationHash}",
                    QuoteId = envelope.Lineage?.QuoteId,
                    JobId = envelope.Lineage?.JobId,
                    TransactionId = envelope.Lineage?.TransactionId,
                    ProductCode = envelope.ProductCode,
                    ProductVersion = ProductVersionNumber.Parse(artefact.Definition.ProductVersion),
                    Slot = artefact.Definition.Code,
                    Mode = mode == RateRateRequest.EnvelopeDetail.ModeValue.Quick ? RatingCalculatedV1.ModeValue.Quick : RatingCalculatedV1.ModeValue.Full,
                    Channel = envelope.Channel ?? "DIRECT",
                    ProducerCode = envelope.ProducerCode,
                    RatingCellKey = artefact.Definition.Code,
                    LevelCodes = [],
                    CoveragePremiumTotal = premiumTotal,
                    TaxTotal = taxTotal,
                    SignalCodes = [],
                    WorksheetId = worksheetId,
                    Bindable = mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
                },
                BusinessKeys.Empty.With("worksheetId", worksheetId.Value)));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RateRateResponse
        {
            Rates = rateItems,
            Taxes = taxes,
            CoveragePremiumTotal = premiumTotal,
            TaxTotal = taxTotal,
            GrossTotal = premiumTotal + taxTotal,
            RatingArtefactHash = artefact.Hash,
            ConfigurationHash = configurationHash,
            Warnings = warnings,
            AutomatedDecision = false,
            Bindable = mode == RateRateRequest.EnvelopeDetail.ModeValue.Full,
            WorksheetId = worksheetId,
            WorksheetHash = worksheetId,
        };
    }

    public Task<RateRateBatchResponse> RateBatchAsync(RateRateBatchRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw Error("DATA-UNAVAILABLE", "rat.Rate.rateBatch is not built yet (the slice implements rat.Rate.rate).");

    private async Task<CompiledArtefact> ResolveArtefactAsync(
        RateRateRequest.EnvelopeDetail envelope, string? version, DateOnly basis, CancellationToken cancellationToken)
    {
        string hash;
        if (envelope.RatingArtefactHash is { } named)
        {
            hash = named.Value;
        }
        else
        {
            var found = await store.ResolveAsync(envelope.ProductCode, version, basis, cancellationToken).ConfigureAwait(false)
                ?? throw Error("NO-ACTIVE-ARTEFACT", $"No rating artefact is active for {envelope.ProductCode} on {basis:yyyy-MM-dd}.");
            hash = found.ArtefactHash;
        }

        // An artefact other than the one named is never substituted (REQ-RAT-049).
        var artefact = await store.LoadAsync(hash, cancellationToken).ConfigureAwait(false)
            ?? throw Error("UNKNOWN-ARTEFACT", "The named rating artefact does not exist.");
        if (!string.Equals(artefact.Definition.ProductCode, envelope.ProductCode, StringComparison.Ordinal)
            || (version is not null && !string.Equals(artefact.Definition.ProductVersion, version, StringComparison.Ordinal)))
        {
            throw Error("INCOMPATIBLE-ARTEFACT", "The rating artefact is not bound to this product version.");
        }

        return artefact;
    }

    private async Task<(IReadOnlyList<ConfigurationResolveResponse.ValueItem> Values, ConfigurationHash ConfigurationHash)> ResolveTaxRatesAsync(
        CompiledArtefact artefact, RateRateRequest.EnvelopeDetail envelope, string? version, BusinessDate taxPoint, CancellationToken cancellationToken)
    {
        var request = new ConfigurationResolveRequest
        {
            LegalEntity = envelope.LegalEntity,
            Jurisdiction = envelope.Jurisdiction,
            ProductCode = envelope.ProductCode,
            ProductVersion = version is null ? null : ProductVersionNumber.Parse(version),
            Channel = envelope.Channel,
            TimeBasisDates = new Dictionary<string, BusinessDate> { ["TAX_POINT_DATE"] = taxPoint, ["EFFECTIVE_DATE"] = envelope.RatingBasisDate },
            ConfigurationHash = envelope.ConfigurationHash,
            Keys = [.. artefact.Definition.TaxPlan.Select(p => p.RateKey).Distinct(StringComparer.Ordinal)],
        };
        try
        {
            var response = await marketConfiguration.ResolveAsync(request, ValidAt.From(taxPoint), cancellationToken: cancellationToken).ConfigureAwait(false);
            return (response.Values, response.ConfigurationHash);
        }
        catch (DomainException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Error("TAX", $"MKT configuration could not be resolved; rating fails closed ({ex.GetType().Name}).");
        }
    }

    private static JsonObject BuildWorksheet(
        CompiledArtefact artefact,
        RateRateRequest.EnvelopeDetail envelope,
        RateRateRequest request,
        List<(string SegmentId, MotorRisk Risk, List<CoveragePremium> Premiums)> segments,
        List<RateRateResponse.TaxeItem> taxes,
        Money premiumTotal,
        Money taxTotal,
        string inputHash,
        ConfigurationHash configurationHash,
        BusinessDate taxPoint,
        string dataStatus)
    {
        _ = request;
        var tableHashes = new JsonObject();
        foreach (var (code, table) in artefact.Tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            tableHashes[code] = table.Hash;
        }

        return new JsonObject
        {
            ["schema"] = "rat.worksheet/1",
            ["header"] = new JsonObject
            {
                ["legalEntity"] = envelope.LegalEntity,
                ["jurisdiction"] = envelope.Jurisdiction,
                ["productCode"] = envelope.ProductCode,
                ["productVersion"] = artefact.Definition.ProductVersion,
                ["productArtefactHash"] = envelope.ProductArtefactHash.ToString(),
                ["ratingArtefactHash"] = artefact.Hash.ToString(),
                ["ratingArtefact"] = artefact.Definition.Code + " " + artefact.Definition.Label,
                ["configurationHash"] = configurationHash.ToString(),
                ["engineVersion"] = artefact.Definition.EngineVersion,
                ["inputHash"] = inputHash,
                ["tableHashes"] = tableHashes,
                ["mode"] = envelope.Mode.ToString().ToUpperInvariant(),
                ["transactionType"] = envelope.TransactionType,
                ["ratingBasisDate"] = envelope.RatingBasisDate.ToString(),
                ["taxPointDate"] = taxPoint.ToString(),
                ["currency"] = envelope.Currency.Code,
                ["dataStatus"] = dataStatus,
                ["notATariff"] = artefact.Definition.Metadata.NotATariff,
                ["notice"] = artefact.Definition.Metadata.Note,
            },
            ["lines"] = new JsonArray(segments.SelectMany(s => s.Premiums.Select(p => (JsonNode)new JsonObject
            {
                ["segmentId"] = s.SegmentId,
                ["elementId"] = s.Risk.VehicleElementId,
                ["coverageCode"] = p.Coverage,
                ["chargeType"] = "PREMIUM",
                ["annualPremium"] = RatingEngine.Format(p.Premium),
                ["steps"] = RatingEngine.TraceJson(p.Steps),
            })).ToArray()),
            ["taxes"] = new JsonArray(taxes.Select(t => (JsonNode)new JsonObject
            {
                ["segmentId"] = t.SegmentId,
                ["coverageCode"] = t.CoverageCode,
                ["chargeType"] = t.ChargeType,
                ["category"] = t.Category.ToString().ToUpperInvariant(),
                ["taxClass"] = t.TaxClass,
                ["base"] = RatingEngine.Format(t.Base.Amount),
                ["rate"] = RatingEngine.Format(t.Rate),
                ["amount"] = RatingEngine.Format(t.Amount.Amount),
                ["rounding"] = t.RoundingRule,
                ["configurationKey"] = t.ConfigurationKey,
                ["configurationValueVersionId"] = t.ConfigurationValueVersionId?.ToString(),
            }).ToArray()),
            ["totals"] = new JsonObject
            {
                ["coveragePremium"] = RatingEngine.Format(premiumTotal.Amount),
                ["tax"] = RatingEngine.Format(taxTotal.Amount),
                ["gross"] = RatingEngine.Format((premiumTotal + taxTotal).Amount),
            },
        };
    }

    private static DomainException Error(string code, string detail) => new(DomainError.Of(ModuleCode.RAT, code, detail));
}

/// <summary><c>rat.RatingArtifact.resolve</c> (REQ-RAT-064): the artefact active for a product version on a date.</summary>
internal sealed class RatingRatingArtifactService(RatingStore store) : IRatingRatingArtifactService
{
    public async Task<RatingArtifactResolveResponse> ResolveAsync(
        RatingArtifactResolveRequest request, ValidAt? validAt = null, Instant? knownAt = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ProductCode) || validAt is not { Date: { } date })
        {
            throw new DomainException(DomainError.Of(ModuleCode.RAT, "ENVELOPE", "Give the product code and validAt as a date."));
        }

        await store.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
        var found = await store.ResolveAsync(request.ProductCode, request.ProductVersion?.ToString(), date.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new DomainException(DomainError.Of(ModuleCode.RAT, "NO-ACTIVE-ARTEFACT", $"No rating artefact is active for {request.ProductCode} on {date}."));
        return new RatingArtifactResolveResponse
        {
            ArtefactHash = Sha256Hash.Parse(found.ArtefactHash),
            ActivationRecord = JsonSerializer.SerializeToElement(new { found.ProductCode, found.ProductVersion, found.DataStatus }),
        };
    }
}

/// <summary><c>rat.Worksheet.get</c> (REQ-RAT-003): the stored worksheet, with its step trace.</summary>
internal sealed class RatingWorksheetService(RatingStore store, RequestContext context, ILegalEntityDirectory legalEntities) : IRatingWorksheetService
{
    public async Task<WorksheetGetResponse> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var legalEntity = legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));
        var body = Sha256Hash.TryParse(id, out _) ? await store.GetWorksheetAsync(id, legalEntity.Value, cancellationToken).ConfigureAwait(false) : null;
        return body is null
            ? throw new DomainException(DomainError.Of(ModuleCode.RAT, "WORKSHEET-NOT-FOUND", "The worksheet does not exist."))
            : new WorksheetGetResponse { Worksheet = JsonDocument.Parse(body).RootElement.Clone() };
    }

    public Task<WorksheetAttachResponse> AttachAsync(WorksheetAttachRequest request, CommandOptions options, CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.RAT, "DATA-UNAVAILABLE", "rat.Worksheet.attach is not built yet."));

    public Task<WorksheetExplainResponse> ExplainAsync(CancellationToken cancellationToken = default) =>
        throw new DomainException(DomainError.Of(ModuleCode.RAT, "DATA-UNAVAILABLE", "rat.Worksheet.explain is not built yet; rat.Worksheet.get returns the step trace."));
}
