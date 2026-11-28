using System.Text.Json;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Platform.Commands;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;

namespace CoreIns.Modules.Work.Inbound;

/// <summary><c>wrk.InboundDocument.submit</c>.</summary>
internal sealed record SubmitInbound(InboundDocumentSubmitRequest Request) : ICommand<InboundDocumentSubmitResponse>;

internal sealed class SubmitInboundHandler(InboundService inbound) : ICommandHandler<SubmitInbound, InboundDocumentSubmitResponse>
{
    public async Task<Result<InboundDocumentSubmitResponse>> HandleAsync(SubmitInbound command, CancellationToken cancellationToken)
    {
        var result = await inbound.SubmitAsync(command.Request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var (envelopeId, rows) = result.Value;
        return new InboundDocumentSubmitResponse
        {
            EnvelopeId = envelopeId,
            DocumentIds = [.. rows.Select(r => new DocumentId(r.InboundDocumentId))],
            InboundNumbers = JsonSerializer.SerializeToElement(rows.Select(r => r.InboundNumber)),
        };
    }
}

internal sealed class SubmitInboundAuditor : ICommandAuditor<SubmitInbound, InboundDocumentSubmitResponse>
{
    public CommandAuditFacts Describe(SubmitInbound command, Result<InboundDocumentSubmitResponse>? result) =>
        new() { ObjectRef = result?.IsSuccess == true && result.Value.Value.EnvelopeId is { } e ? new ObjectRef(ModuleCode.WRK, "InboundEnvelope", e.ToString("D")) : null };
}

/// <summary><c>wrk.InboundDocument.verify</c>.</summary>
internal sealed record VerifyInbound(InboundVerifyBody Request) : ICommand<InboundDocumentVerifyResponse>;

internal sealed class VerifyInboundHandler(InboundService inbound) : ICommandHandler<VerifyInbound, InboundDocumentVerifyResponse>
{
    public async Task<Result<InboundDocumentVerifyResponse>> HandleAsync(VerifyInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.VerifyAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentVerifyResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class VerifyInboundAuditor : ICommandAuditor<VerifyInbound, InboundDocumentVerifyResponse>
{
    public CommandAuditFacts Describe(VerifyInbound command, Result<InboundDocumentVerifyResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.split</c>: the response document holds the parent and its children.</summary>
internal sealed record SplitInbound(InboundSplitBody Request) : ICommand<InboundDocumentSplitResponse>;

internal sealed class SplitInboundHandler(InboundService inbound) : ICommandHandler<SplitInbound, InboundDocumentSplitResponse>
{
    public async Task<Result<InboundDocumentSplitResponse>> HandleAsync(SplitInbound command, CancellationToken cancellationToken)
    {
        var result = await inbound.SplitAsync(command.Request, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var (parent, children) = result.Value;
        var element = JsonSerializer.SerializeToElement(new { parent = inbound.View(parent), children = children.Select(inbound.View).ToList() }, SharedKernelJson.Options);
        return new InboundDocumentSplitResponse { Document = element };
    }
}

internal sealed class SplitInboundAuditor : ICommandAuditor<SplitInbound, InboundDocumentSplitResponse>
{
    public CommandAuditFacts Describe(SplitInbound command, Result<InboundDocumentSplitResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.link</c>.</summary>
internal sealed record LinkInbound(InboundLinkBody Request) : ICommand<InboundDocumentLinkResponse>;

internal sealed class LinkInboundHandler(InboundService inbound) : ICommandHandler<LinkInbound, InboundDocumentLinkResponse>
{
    public async Task<Result<InboundDocumentLinkResponse>> HandleAsync(LinkInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.LinkAsync(command.Request, relink: false, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentLinkResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class LinkInboundAuditor : ICommandAuditor<LinkInbound, InboundDocumentLinkResponse>
{
    public CommandAuditFacts Describe(LinkInbound command, Result<InboundDocumentLinkResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.relink</c> (needs a reason).</summary>
internal sealed record RelinkInbound(InboundLinkBody Request) : ICommand<InboundDocumentRelinkResponse>;

internal sealed class RelinkInboundHandler(InboundService inbound) : ICommandHandler<RelinkInbound, InboundDocumentRelinkResponse>
{
    public async Task<Result<InboundDocumentRelinkResponse>> HandleAsync(RelinkInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.LinkAsync(command.Request, relink: true, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentRelinkResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class RelinkInboundAuditor : ICommandAuditor<RelinkInbound, InboundDocumentRelinkResponse>
{
    public CommandAuditFacts Describe(RelinkInbound command, Result<InboundDocumentRelinkResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.reject</c>.</summary>
internal sealed record RejectInbound(InboundRejectBody Request) : ICommand<InboundDocumentRejectResponse>;

internal sealed class RejectInboundHandler(InboundService inbound) : ICommandHandler<RejectInbound, InboundDocumentRejectResponse>
{
    public async Task<Result<InboundDocumentRejectResponse>> HandleAsync(RejectInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.RejectAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentRejectResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class RejectInboundAuditor : ICommandAuditor<RejectInbound, InboundDocumentRejectResponse>
{
    public CommandAuditFacts Describe(RejectInbound command, Result<InboundDocumentRejectResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.redact</c>.</summary>
internal sealed record RedactInbound(InboundRedactBody Request) : ICommand<InboundDocumentRedactResponse>;

internal sealed class RedactInboundHandler(InboundService inbound) : ICommandHandler<RedactInbound, InboundDocumentRedactResponse>
{
    public async Task<Result<InboundDocumentRedactResponse>> HandleAsync(RedactInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.RedactAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentRedactResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class RedactInboundAuditor : ICommandAuditor<RedactInbound, InboundDocumentRedactResponse>
{
    public CommandAuditFacts Describe(RedactInbound command, Result<InboundDocumentRedactResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.reprocess</c>.</summary>
internal sealed record ReprocessInbound(InboundReprocessBody Request) : ICommand<InboundDocumentReprocessResponse>;

internal sealed class ReprocessInboundHandler(InboundService inbound) : ICommandHandler<ReprocessInbound, InboundDocumentReprocessResponse>
{
    public async Task<Result<InboundDocumentReprocessResponse>> HandleAsync(ReprocessInbound command, CancellationToken cancellationToken)
    {
        var row = await inbound.ReprocessAsync(command.Request, cancellationToken).ConfigureAwait(false);
        return row.IsFailure ? row.Error! : new InboundDocumentReprocessResponse { Document = inbound.View(row.Value).ToElement() };
    }
}

internal sealed class ReprocessInboundAuditor : ICommandAuditor<ReprocessInbound, InboundDocumentReprocessResponse>
{
    public CommandAuditFacts Describe(ReprocessInbound command, Result<InboundDocumentReprocessResponse>? result) => new() { ObjectRef = InboundAudit.Ref(command.Request.Id) };
}

/// <summary><c>wrk.InboundDocument.export</c>: CSV metadata, returned as <c>document = {fileName, contentType, rows, content}</c>.</summary>
internal sealed record ExportInbound(InboundExportBody Request) : ICommand<InboundDocumentExportResponse>;

internal sealed class ExportInboundHandler(InboundService inbound) : ICommandHandler<ExportInbound, InboundDocumentExportResponse>
{
    public async Task<Result<InboundDocumentExportResponse>> HandleAsync(ExportInbound command, CancellationToken cancellationToken)
    {
        var export = await inbound.ExportAsync(command.Request, cancellationToken).ConfigureAwait(false);
        if (export.IsFailure)
        {
            return export.Error!;
        }

        return new InboundDocumentExportResponse
        {
            Document = JsonSerializer.SerializeToElement(new { fileName = "inbound-documents.csv", contentType = "text/csv", rows = export.Value.Rows, content = export.Value.Csv }),
        };
    }
}

internal sealed class ExportInboundAuditor : ICommandAuditor<ExportInbound, InboundDocumentExportResponse>
{
    public CommandAuditFacts Describe(ExportInbound command, Result<InboundDocumentExportResponse>? result) => new();
}

internal static class InboundAudit
{
    public static ObjectRef? Ref(Guid? id) => id is { } g && g != Guid.Empty ? new ObjectRef(ModuleCode.WRK, "InboundDocument", g.ToString("D")) : null;
}
