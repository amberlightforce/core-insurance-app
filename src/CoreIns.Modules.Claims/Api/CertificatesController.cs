using CoreIns.Modules.Claims.Commands.Certificates;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.Modules.Claims.Domain;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Http;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CoreIns.Modules.Claims.Api;

/// <summary>REST facade of <c>clm.Certificate.*</c> (BO-CLM-07): the claims-history certificate, three commands through the pipeline.</summary>
[ApiController]
[Route("api/clm/v1/certificates")]
internal sealed class CertificatesController : ControllerBase
{
    /// <summary>clm.Certificate.request.</summary>
    [HttpPost("request")]
    [Authorize(Policy = "clm.Certificate.request")]
    public async Task<IResult> RequestAsync(
        [FromBody] CertificateRequestRequest request, [FromServices] ICommandHandler<RequestCertificate, CertificateRequestResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new RequestCertificate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Certificate.assemble.</summary>
    [HttpPost("assemble")]
    [Authorize(Policy = "clm.Certificate.assemble")]
    public async Task<IResult> AssembleAsync(
        [FromBody] CertificateAssembleRequest request, [FromServices] ICommandHandler<AssembleCertificate, CertificateAssembleResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new AssembleCertificate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>clm.Certificate.issue.</summary>
    [HttpPost("issue")]
    [Authorize(Policy = "clm.Certificate.issue")]
    public async Task<IResult> IssueAsync(
        [FromBody] CertificateIssueRequest request, [FromServices] ICommandHandler<IssueCertificate, CertificateIssueResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new IssueCertificate(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);
}
