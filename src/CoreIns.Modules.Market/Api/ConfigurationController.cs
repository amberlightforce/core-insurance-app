using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Contracts;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Market.Api;

/// <summary>Permissions of the MKT operations this wave implements (the operations' <c>x-permission</c>).</summary>
internal static class MarketPermissions
{
    public const string ConfigurationResolve = "mkt.Configuration.resolve";
    public const string ConfigurationCurrentHash = "mkt.Configuration.currentHash";
    public const string ConfigurationExplain = "mkt.Configuration.explain";
    public const string ConfigurationCompare = "mkt.Configuration.compare";
    public const string ConfigurationExport = "mkt.Configuration.export";
    public const string RoundingApply = "mkt.Rounding.apply";
    public const string PackList = "mkt.Pack.list";
    public const string PackGet = "mkt.Pack.get";
}

/// <summary>REST facade of <c>mkt.Configuration.resolve</c> and <c>mkt.Configuration.currentHash</c> (W1-MKT-01 subset).</summary>
[ApiController]
[Route("api/mkt/v1/configuration")]
internal sealed class ConfigurationController(MarketConfigurationService service, ConfigurationLineageService lineage) : ControllerBase
{
    /// <summary>mkt.Configuration.resolve: a pure read sent as POST, so it takes no Idempotency-Key (contract x-operation-kind query).</summary>
    [HttpPost("resolve")]
    [SkipIdempotency]
    [Authorize(Policy = MarketPermissions.ConfigurationResolve)]
    public async Task<IResult> ResolveAsync(
        [FromBody] ConfigurationResolveRequest request, [FromQuery] string? validAt, [FromQuery] string? knownAt, CancellationToken cancellationToken)
    {
        if (!TryValidAt(validAt, out var valid) || !TryKnownAt(knownAt, out var known))
        {
            throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-VALIDATION", "validAt must be a date or instant, knownAt an instant."));
        }

        return Results.Ok(await service.ResolveAsync(request, valid, known, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>mkt.Configuration.currentHash.</summary>
    [HttpGet("current-hash")]
    [Authorize(Policy = MarketPermissions.ConfigurationCurrentHash)]
    public async Task<IResult> CurrentHashAsync(CancellationToken cancellationToken) =>
        Results.Ok(await service.CurrentHashAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>mkt.Configuration.explain (BO-MKT-03): which layer supplied the value of a key, with the pack version and the change request.</summary>
    [HttpGet("explain")]
    [Authorize(Policy = MarketPermissions.ConfigurationExplain)]
    public async Task<IResult> ExplainAsync(
        [FromQuery] string? key, [FromQuery] string? context, [FromQuery] string? validAt, [FromQuery] string? hash, CancellationToken cancellationToken) =>
        Results.Ok(await lineage.ExplainAsync(key, context, ParseDate(validAt, "validAt"), ParseHash(hash, "hash"), cancellationToken).ConfigureAwait(false));

    /// <summary>mkt.Configuration.compare (BO-MKT-03): two recorded states by hash, or two business dates of one state.</summary>
    [HttpGet("compare")]
    [Authorize(Policy = MarketPermissions.ConfigurationCompare)]
    public async Task<IResult> CompareAsync(
        [FromQuery] string? hash, [FromQuery] string? hashB, [FromQuery] string? validAt, [FromQuery] string? validAtB, [FromQuery] string? filter,
        CancellationToken cancellationToken) =>
        Results.Ok(await lineage.CompareAsync(
            ParseHash(hash, "hash"), ParseHash(hashB, "hashB"), ParseDate(validAt, "validAt"), ParseDate(validAtB, "validAtB"), filter, cancellationToken).ConfigureAwait(false));

    /// <summary>mkt.Configuration.export (BO-MKT-03): a pure read sent as POST (no Idempotency-Key).</summary>
    [HttpPost("export")]
    [SkipIdempotency]
    [Authorize(Policy = MarketPermissions.ConfigurationExport)]
    public async Task<IResult> ExportAsync([FromBody] ConfigurationExportRequest request, CancellationToken cancellationToken) =>
        Results.Ok(await lineage.ExportAsync(request, cancellationToken).ConfigureAwait(false));

    private static BusinessDate? ParseDate(string? text, string name) =>
        text is null ? null
        : BusinessDate.TryParse(text, out var date) ? date
        : throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-VALIDATION", $"{name} must be a date (yyyy-MM-dd)."));

    private static Sha256Hash? ParseHash(string? text, string name) =>
        text is null ? null
        : Sha256Hash.TryParse(text, out var hash) ? hash
        : throw new DomainException(DomainError.Of(ModuleCode.MKT, "CFG-VALIDATION", $"{name} must be a SHA-256 hex digest."));

    internal static bool TryValidAt(string? text, out ValidAt? validAt)
    {
        validAt = null;
        if (text is null)
        {
            return true;
        }

        if (Instant.TryParse(text, out var instant))
        {
            validAt = ValidAt.From(instant);
            return true;
        }

        if (BusinessDate.TryParse(text, out var date))
        {
            validAt = ValidAt.From(date);
            return true;
        }

        return false;
    }

    internal static bool TryKnownAt(string? text, out Instant? knownAt)
    {
        knownAt = null;
        if (text is null)
        {
            return true;
        }

        if (!Instant.TryParse(text, out var instant))
        {
            return false;
        }

        knownAt = instant;
        return true;
    }
}

/// <summary>REST facade of <c>mkt.Rounding.apply</c> (W1-MKT-04 subset).</summary>
[ApiController]
[Route("api/mkt/v1/rounding")]
internal sealed class RoundingController(MarketRoundingService service) : ControllerBase
{
    /// <summary>mkt.Rounding.apply: a pure read sent as POST (no Idempotency-Key).</summary>
    [HttpPost("apply")]
    [SkipIdempotency]
    [Authorize(Policy = MarketPermissions.RoundingApply)]
    public async Task<IResult> ApplyAsync([FromBody] RoundingApplyRequest request, CancellationToken cancellationToken) =>
        Results.Ok(await service.ApplyAsync(request, cancellationToken).ConfigureAwait(false));
}

/// <summary>REST facade of <c>mkt.Pack.list</c> and <c>mkt.Pack.get</c> (REQ-MKT-003 registry subset; SL5-MKT-STATE).</summary>
[ApiController]
[Route("api/mkt/v1/packs")]
internal sealed class PackController(PackRegistryService service) : ControllerBase
{
    /// <summary>mkt.Pack.list: the registered packs with versions and activations (keyset paging by pack id).</summary>
    [HttpGet]
    [Authorize(Policy = MarketPermissions.PackList)]
    public async Task<IResult> ListAsync([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(cursor, limit, cancellationToken).ConfigureAwait(false));

    /// <summary>mkt.Pack.get: one pack by id.</summary>
    [HttpGet("{id}")]
    [Authorize(Policy = MarketPermissions.PackGet)]
    public async Task<IResult> GetAsync(string id, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetAsync(id, cancellationToken).ConfigureAwait(false));
}
