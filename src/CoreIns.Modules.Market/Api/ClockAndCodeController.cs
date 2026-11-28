using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Modules.Market.Services;
using CoreIns.Platform.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Market.Api;

/// <summary><c>mkt.StatutoryClockSet.list/get</c> (REQ-MKT-009): the statutory clock values with their legal status.</summary>
[ApiController]
[Route("api/mkt/v1/statutory-clock-sets")]
internal sealed class StatutoryClockSetsController(StatutoryClockAndCodeService service) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "mkt.StatutoryClockSet.list")]
    public IResult List([FromQuery] string? clockCode, [FromQuery] string? jurisdiction) => Results.Ok(service.ListClocks(clockCode, jurisdiction));

    [HttpGet("{id}")]
    [Authorize(Policy = "mkt.StatutoryClockSet.get")]
    public IResult Get(string id) => Results.Ok(service.GetClock(id));
}

/// <summary><c>mkt.RegimeCode.list/get/validate/crosswalk</c> (REQ-MKT-007).</summary>
[ApiController]
[Route("api/mkt/v1/regime-codes")]
internal sealed class RegimeCodesController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "mkt.RegimeCode.list")]
    public IActionResult List([FromQuery] string? regime) => Ok(StatutoryClockAndCodeService.ListCodes(regime));

    [HttpGet("crosswalk")]
    [Authorize(Policy = "mkt.RegimeCode.crosswalk")]
    public IActionResult Crosswalk([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? code) => Ok(StatutoryClockAndCodeService.Crosswalk(from, to, code));

    [HttpGet("{id}")]
    [Authorize(Policy = "mkt.RegimeCode.get")]
    public IActionResult Get(string id) => Ok(StatutoryClockAndCodeService.GetCodes(id));

    /// <summary>A pure read sent as POST, so it takes no Idempotency-Key.</summary>
    [HttpPost("validate")]
    [SkipIdempotency]
    [Authorize(Policy = "mkt.RegimeCode.validate")]
    public IActionResult Validate([FromBody] RegimeCodeValidateRequest request) => Ok(StatutoryClockAndCodeService.Validate(request));
}
