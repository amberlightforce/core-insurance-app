using System.Text.Json;
using CoreIns.Modules.Documents.Commands;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Documents.Contracts.Api;
using CoreIns.Modules.Documents.Queries;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Documents.Api;

/// <summary>Permission names of the operations (<c>x-permission</c>).</summary>
internal static class DocumentsPermissions
{
    public const string DocumentRequest = "doc.Document.request";
    public const string DocumentGet = "doc.Document.get";
    public const string DocumentListForObject = "doc.Document.listForObject";
    public const string DocumentReprint = "doc.Document.reprint";
    public const string DeliveryStatus = "doc.Delivery.status";
    public const string DeliveryEvidenceFor = "doc.Delivery.evidenceFor";
    public const string EvidencePackGet = "doc.EvidencePack.get";
    public const string ArchiveStore = "doc.Archive.store";
    public const string ArchiveGet = "doc.Archive.get";
    public const string ProviderEventReceive = "doc.ProviderEvent.receive";
    public const string DocumentTypeList = "doc.DocumentType.list";
    public const string TemplateList = "doc.Template.list";
    public const string ClauseGet = "doc.Clause.get";
    public const string BindingResolve = "doc.Binding.resolve";
}

/// <summary>REST facade of the SL6-DOC-CORE operations (contracts/openapi/doc.yaml).</summary>
[ApiController]
[Route("api/doc/v1")]
internal sealed class DocumentsController : ControllerBase
{
    /// <summary>doc.Document.request. The template key is <c>documentType</c>; the request number and placeholders ride in <c>payloadSupplement</c>.</summary>
    [HttpPost("documents/request")]
    [Authorize(Policy = DocumentsPermissions.DocumentRequest)]
    public async Task<IResult> RequestAsync(
        [FromBody] DocumentRequestRequest request,
        [FromServices] ICommandHandler<RequestDocument, DocumentRequestOutcome> handler,
        [FromServices] RequestContext context,
        CancellationToken cancellationToken)
    {
        var supplement = request.PayloadSupplement;
        var requestNo = supplement is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("requestNo", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal);
        if (supplement is { ValueKind: JsonValueKind.Object } sup && sup.TryGetProperty("placeholders", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in p.EnumerateObject())
            {
                placeholders[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : property.Value.GetRawText();
            }
        }

        PartyId? recipient = request.Recipients is { Count: > 0 } r
            && r[0].ValueKind == JsonValueKind.Object && r[0].TryGetProperty("partyId", out var pid) && Guid.TryParse(pid.GetString(), out var guid)
                ? new PartyId(guid)
                : null;
        if (requestNo is null || recipient is null)
        {
            return Problem(DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "payloadSupplement.requestNo and recipients[0].partyId are required."));
        }

        var language = request.LanguageOverride ?? context.Language.ToString().ToLowerInvariant();
        var medium = request.DeliveryInstruction.Channel is { Length: > 0 } c ? c : null;
        var spec = new DocumentRequestSpec(request.DocumentType, requestNo, request.Subject, recipient.Value, language, placeholders, medium);
        var result = await handler.HandleAsync(new RequestDocument(spec), cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? Problem(result.Error!)
            : Results.Ok(new DocumentRequestResponse
            {
                DocumentId = result.Value.DocumentId,
                DocumentNumber = result.Value.DocumentNumber,
                Status = result.Value.Status,
                Channels = [result.Value.Medium],
            });
    }

    /// <summary>doc.Document.get.</summary>
    [HttpGet("documents/{id}")]
    [Authorize(Policy = DocumentsPermissions.DocumentGet)]
    public async Task<IResult> GetAsync(string id, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetAsync(new DocumentId(guid), cancellationToken).ConfigureAwait(false) : null;
        return found is null ? NotFoundProblem("The document does not exist in your legal entity.") : Results.Ok(found);
    }

    /// <summary>doc.Document.listForObject (<c>objectRef</c> is <c>MOD/Type/id</c>).</summary>
    [HttpGet("documents/list-for-object")]
    [Authorize(Policy = DocumentsPermissions.DocumentListForObject)]
    public async Task<IResult> ListForObjectAsync([FromQuery] string objectRef, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        if (!TryParseObjectRef(objectRef, out var subject))
        {
            return Problem(DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "objectRef must be MODULE/Type/id."));
        }

        var items = await reader.ListForObjectAsync(subject, 200, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new { items, nextCursor = (string?)null });
    }

    /// <summary>doc.Document.reprint.</summary>
    [HttpPost("documents/reprint")]
    [Authorize(Policy = DocumentsPermissions.DocumentReprint)]
    public async Task<IResult> ReprintAsync(
        [FromBody] DocumentReprintRequest request,
        [FromServices] ICommandHandler<ReprintDocument, ReprintResult> handler,
        CancellationToken cancellationToken)
    {
        if (request.DocumentId is not { } documentId)
        {
            return Problem(DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "documentId is required."));
        }

        var result = await handler.HandleAsync(new ReprintDocument(documentId, request.Reason?.ToString()), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? Problem(result.Error!) : Results.Ok(new DocumentReprintResponse { Link = DocumentReader.ToJson(result.Value) });
    }

    /// <summary>doc.Delivery.status.</summary>
    [HttpGet("deliveries/status")]
    [Authorize(Policy = DocumentsPermissions.DeliveryStatus)]
    public async Task<IResult> StatusAsync(
        [FromQuery] Guid? documentId, [FromQuery] string? subject, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        var found = await StatusOrNullAsync(documentId, subject, reader, cancellationToken).ConfigureAwait(false);
        return found is null
            ? NotFoundProblem("No such document or subject.")
            : Results.Ok(new DeliveryStatusResponse { Statuses = DocumentReader.ToJson(found.Value.Statuses), Completeness = DocumentReader.ToJson(new { complete = found.Value.Complete }) });
    }

    /// <summary>doc.Delivery.evidenceFor: the statuses plus the provider evidence of the document.</summary>
    [HttpGet("deliveries/evidence-for")]
    [Authorize(Policy = DocumentsPermissions.DeliveryEvidenceFor)]
    public async Task<IResult> EvidenceForAsync(
        [FromQuery] Guid? documentId, [FromQuery] string? subject, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        var found = await StatusOrNullAsync(documentId, subject, reader, cancellationToken).ConfigureAwait(false);
        if (found is null || documentId is null)
        {
            return NotFoundProblem("evidence-for needs an existing documentId.");
        }

        var evidence = await reader.EvidenceAsync(new DocumentId(documentId.Value), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new DeliveryEvidenceForResponse
        {
            Statuses = DocumentReader.ToJson(new { deliveries = found.Value.Statuses, evidence }),
            Completeness = DocumentReader.ToJson(new { complete = found.Value.Complete }),
        });
    }

    /// <summary>doc.EvidencePack.get: the document, its deliveries and every provider callback, with the content hash.</summary>
    [HttpGet("evidence-packs/{id}")]
    [Authorize(Policy = DocumentsPermissions.EvidencePackGet)]
    public async Task<IResult> EvidencePackAsync(string id, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        var document = Guid.TryParse(id, out var guid) ? await reader.GetAsync(new DocumentId(guid), cancellationToken).ConfigureAwait(false) : null;
        if (document is null)
        {
            return NotFoundProblem("The evidence pack does not exist in your legal entity.");
        }

        var evidence = await reader.EvidenceAsync(new DocumentId(guid), cancellationToken).ConfigureAwait(false);
        var final = document.Deliveries.All(d => d.Status is "Delivered" or "Failed" or "Bounced");
        return Results.Ok(new EvidencePackGetResponse { SealedArchive = DocumentReader.ToJson(new { document, evidence, final }) });
    }

    /// <summary>doc.Archive.store.</summary>
    [HttpPost("archives/store")]
    [Authorize(Policy = DocumentsPermissions.ArchiveStore)]
    public async Task<IResult> StoreArchiveAsync(
        [FromBody] ArchiveStoreRequest request, [FromServices] ICommandHandler<StoreArchive, ArchiveStoreResponse> handler, CancellationToken cancellationToken) =>
        (await handler.HandleAsync(new StoreArchive(request), cancellationToken).ConfigureAwait(false)).ToHttpResult(HttpContext);

    /// <summary>doc.Archive.get.</summary>
    [HttpGet("archives/{id}")]
    [Authorize(Policy = DocumentsPermissions.ArchiveGet)]
    public async Task<IResult> GetArchiveAsync(string id, [FromServices] DocumentReader reader, CancellationToken cancellationToken)
    {
        var found = Guid.TryParse(id, out var guid) ? await reader.GetArchiveAsync(guid, cancellationToken).ConfigureAwait(false) : null;
        return found is null ? NotFoundProblem("The archive does not exist in your legal entity.") : Results.Ok(found);
    }

    /// <summary>doc.ProviderEvent.receive: stub provider callbacks (<c>stub-post</c>, <c>stub-email</c>); refused in Production.</summary>
    [HttpPost("provider-events/{provider}")]
    [Authorize(Policy = DocumentsPermissions.ProviderEventReceive)]
    public async Task<IResult> ReceiveProviderEventAsync(
        string provider,
        [FromBody] ProviderEventBody body,
        [FromServices] ICommandHandler<ReceiveProviderEvent, ProviderEventResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ReceiveProviderEvent(provider, body), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? Problem(result.Error!) : Results.Json(result.Value, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>doc.DocumentType.list: the document type catalogue (BO-DOC-05).</summary>
    [HttpGet("document-types")]
    [Authorize(Policy = DocumentsPermissions.DocumentTypeList)]
    public async Task<IResult> ListTypesAsync([FromServices] TemplateCatalogue catalogue)
    {
        var types = await catalogue.ListTypesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var items = types.Select(t => new DocumentTypeListItem
        {
            DocumentType = t.DocumentType, DefaultMedium = t.DefaultMedium, TitleEl = t.TitleEl, TitleEn = t.TitleEn, RequiredFields = t.RequiredFields,
        }).ToList();
        return Results.Ok(new DocumentTypeListPage { Items = items, NextCursor = null });
    }

    /// <summary>doc.Template.list: template versions; <c>filters</c> is <c>documentType</c> or <c>documentType:status</c> (e.g. <c>DT-REFUND-ADVICE:APPROVED</c>).</summary>
    [HttpGet("templates")]
    [Authorize(Policy = DocumentsPermissions.TemplateList)]
    public async Task<IResult> ListTemplatesAsync([FromQuery] string? filters, [FromServices] TemplateCatalogue catalogue)
    {
        var parts = filters?.Split(':', 2);
        var templates = await catalogue.ListTemplatesAsync(parts is { Length: > 0 } ? parts[0] : null, parts is { Length: 2 } ? parts[1] : null, HttpContext.RequestAborted).ConfigureAwait(false);
        var items = templates.Select(t => new TemplateListItem
        {
            TemplateId = new TemplateId(t.TemplateId), DocumentType = t.DocumentType, Version = t.Version, Status = t.Status,
            ValidFrom = new BusinessDate(t.ValidFrom),
            ValidTo = t.ValidTo is { } to ? new BusinessDate(to) : null,
            ProductCode = t.ProductCode, LineOfBusiness = t.LineOfBusiness, Jurisdiction = t.Jurisdiction, TitleEl = t.TitleEl, TitleEn = t.TitleEn,
        }).ToList();
        return Results.Ok(new TemplateListPage { Items = items, NextCursor = null });
    }

    /// <summary>doc.Clause.get (by clause id or code).</summary>
    [HttpGet("clauses/{id}")]
    [Authorize(Policy = DocumentsPermissions.ClauseGet)]
    public async Task<IResult> GetClauseAsync(string id, [FromServices] TemplateCatalogue catalogue, CancellationToken cancellationToken)
    {
        var clause = await catalogue.GetClauseAsync(id, cancellationToken).ConfigureAwait(false);
        return clause is null
            ? NotFoundProblem("The clause does not exist.")
            : Results.Ok(new ClauseGetResponse
            {
                ClauseId = new ClauseId(clause.ClauseId), Code = clause.Code, TitleEl = clause.TitleEl, TextEl = clause.TextEl, TitleEn = clause.TitleEn, TextEn = clause.TextEn, Status = clause.Status,
            });
    }

    /// <summary>doc.Binding.resolve: the approved template version in force at <c>validAt</c> (default today, UTC).</summary>
    [HttpPost("bindings/resolve")]
    [Authorize(Policy = DocumentsPermissions.BindingResolve)]
    public async Task<IResult> ResolveBindingAsync(
        [FromBody] BindingResolveRequest request, [FromQuery] string? validAt, [FromServices] TemplateCatalogue catalogue, [FromServices] IClock clock, [FromServices] RequestContext context, CancellationToken cancellationToken)
    {
        var at = clock.Now.ToBusinessDate(TimeZoneInfo.Utc).Value;
        if (validAt is { Length: > 0 } && !DateOnly.TryParse(validAt.Length >= 10 ? validAt[..10] : validAt, System.Globalization.CultureInfo.InvariantCulture, out at))
        {
            return Problem(DomainError.Of(ModuleCode.DOC, "PAYLOAD-INVALID", "validAt must be a date."));
        }

        var template = await catalogue.ResolveAsync(new TemplateScope(request.DocumentType, at, request.ProductCode, request.LineOfBusiness, request.Jurisdiction), cancellationToken).ConfigureAwait(false);
        return template is null
            ? Problem(DomainError.Of(ModuleCode.DOC, "BINDING-MISSING", "No approved template version is in force for this document type and scope."))
            : Results.Ok(new BindingResolveResponse { TemplateId = new TemplateId(template.TemplateId), DocumentType = template.DocumentType, Version = template.Version, Language = request.Language });
    }

    internal static bool TryParseObjectRef(string? text, out ObjectRef subject)
    {
        subject = null!;
        var parts = text?.Split('/', 3);
        if (parts is not { Length: 3 } || !Enum.TryParse<ModuleCode>(parts[0], ignoreCase: false, out var module) || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return false;
        }

        subject = new ObjectRef(module, parts[1], parts[2]);
        return true;
    }

    private static Task<(IReadOnlyList<DeliveryView> Statuses, bool Complete)?> StatusOrNullAsync(
        Guid? documentId, string? subject, DocumentReader reader, CancellationToken cancellationToken) =>
        reader.StatusAsync(documentId is { } id ? new DocumentId(id) : null, TryParseObjectRef(subject, out var parsed) ? parsed : null, cancellationToken);

    private IResult NotFoundProblem(string detail) => Problem(DomainError.Of(ModuleCode.DOC, "NOT-FOUND", detail));

    private IResult Problem(DomainError error) =>
        HttpContext.RequestServices.GetRequiredService<ProblemDetailsMapper>().ToResult(error, HttpContext);
}
