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
    public const string RoundingApply = "mkt.Rounding.apply";
}

/// <summary>REST facade of <c>mkt.Configuration.resolve</c> and <c>mkt.Configuration.currentHash</c> (W1-MKT-01 subset).</summary>
[ApiController]
[Route("api/mkt/v1/configuration")]
internal sealed class ConfigurationController(MarketConfigurationService service) : ControllerBase
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
