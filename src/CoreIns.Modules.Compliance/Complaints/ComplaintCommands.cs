using System.Globalization;
using CoreIns.Modules.Compliance.Clocks;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Compliance.Complaints;

internal sealed record CreateComplaint(ComplaintCreateRequest Request) : ICommand<ComplaintCreateResponse>;

internal sealed record ClassifyComplaint(ComplaintClassifyRequest Request) : ICommand<ComplaintClassifyResponse>;

internal sealed record LinkComplaint(ComplaintLinkRequest Request) : ICommand<ComplaintLinkResponse>;

internal sealed record AcknowledgeComplaint(ComplaintAcknowledgeRequest Request) : ICommand<ComplaintAcknowledgeResponse>;

internal sealed record AddComplaintAction(ComplaintAddActionRequest Request) : ICommand<ComplaintAddActionResponse>;

internal sealed record SubmitComplaintReply(ComplaintSubmitReplyRequest Request) : ICommand<ComplaintSubmitReplyResponse>;

internal sealed record ApproveComplaintReply(ComplaintApproveReplyRequest Request) : ICommand<ComplaintApproveReplyResponse>;

internal sealed record EscalateComplaint(ComplaintEscalateRequest Request) : ICommand<ComplaintEscalateResponse>;

internal sealed record CloseComplaint(ComplaintCloseRequest Request) : ICommand<ComplaintCloseResponse>;

internal sealed class CreateComplaintValidator : AbstractValidator<CreateComplaint>
{
    public CreateComplaintValidator()
    {
        RuleFor(c => c.Request.Description).NotEmpty().MaximumLength(4000);
        RuleFor(c => c.Request.Channel).MaximumLength(32);
        RuleFor(c => c.Request.ProductLine).MaximumLength(64);
    }
}

internal sealed class ClassifyComplaintValidator : AbstractValidator<ClassifyComplaint>
{
    public ClassifyComplaintValidator()
    {
        RuleFor(c => c.Request.Category).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Cause).MaximumLength(64);
        RuleFor(c => c.Request.ProductLine).MaximumLength(64);
    }
}

internal sealed class LinkComplaintValidator : AbstractValidator<LinkComplaint>
{
    public LinkComplaintValidator() => RuleFor(c => c.Request.Links).NotEmpty();
}

internal sealed class AddComplaintActionValidator : AbstractValidator<AddComplaintAction>
{
    public AddComplaintActionValidator()
    {
        RuleFor(c => c.Request.Kind).NotEmpty().MaximumLength(64);
        RuleFor(c => c.Request.Note).MaximumLength(1000);
    }
}

internal sealed class SubmitComplaintReplyValidator : AbstractValidator<SubmitComplaintReply>
{
    public SubmitComplaintReplyValidator()
    {
        RuleFor(c => c.Request.ReplyText).NotEmpty().MaximumLength(8000);
        RuleFor(c => c.Request.RootCause).MaximumLength(128);
        RuleFor(c => c.Request.RedressAmount)
            .Must(a => a is null || (decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d >= 0))
            .WithMessage("The redress amount must be a decimal number, zero or more.");
    }
}

internal sealed class CreateComplaintHandler(ComplaintEngine engine) : ICommandHandler<CreateComplaint, ComplaintCreateResponse>
{
    public async Task<Result<ComplaintCreateResponse>> HandleAsync(CreateComplaint command, CancellationToken cancellationToken) =>
        (await engine.CreateAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintCreateResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class ClassifyComplaintHandler(ComplaintEngine engine) : ICommandHandler<ClassifyComplaint, ComplaintClassifyResponse>
{
    public async Task<Result<ComplaintClassifyResponse>> HandleAsync(ClassifyComplaint command, CancellationToken cancellationToken) =>
        (await engine.ClassifyAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintClassifyResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class LinkComplaintHandler(ComplaintEngine engine) : ICommandHandler<LinkComplaint, ComplaintLinkResponse>
{
    public async Task<Result<ComplaintLinkResponse>> HandleAsync(LinkComplaint command, CancellationToken cancellationToken) =>
        (await engine.LinkAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintLinkResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class AcknowledgeComplaintHandler(ComplaintEngine engine) : ICommandHandler<AcknowledgeComplaint, ComplaintAcknowledgeResponse>
{
    public async Task<Result<ComplaintAcknowledgeResponse>> HandleAsync(AcknowledgeComplaint command, CancellationToken cancellationToken) =>
        (await engine.AcknowledgeAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintAcknowledgeResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class AddComplaintActionHandler(ComplaintEngine engine) : ICommandHandler<AddComplaintAction, ComplaintAddActionResponse>
{
    public async Task<Result<ComplaintAddActionResponse>> HandleAsync(AddComplaintAction command, CancellationToken cancellationToken) =>
        (await engine.AddActionAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintAddActionResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class SubmitComplaintReplyHandler(ComplaintEngine engine) : ICommandHandler<SubmitComplaintReply, ComplaintSubmitReplyResponse>
{
    public async Task<Result<ComplaintSubmitReplyResponse>> HandleAsync(SubmitComplaintReply command, CancellationToken cancellationToken) =>
        (await engine.SubmitReplyAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintSubmitReplyResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class ApproveComplaintReplyHandler(ComplaintEngine engine) : ICommandHandler<ApproveComplaintReply, ComplaintApproveReplyResponse>
{
    public async Task<Result<ComplaintApproveReplyResponse>> HandleAsync(ApproveComplaintReply command, CancellationToken cancellationToken) =>
        (await engine.ApproveReplyAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintApproveReplyResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class EscalateComplaintHandler(ComplaintEngine engine) : ICommandHandler<EscalateComplaint, ComplaintEscalateResponse>
{
    public async Task<Result<ComplaintEscalateResponse>> HandleAsync(EscalateComplaint command, CancellationToken cancellationToken) =>
        (await engine.EscalateAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintEscalateResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

internal sealed class CloseComplaintHandler(ComplaintEngine engine) : ICommandHandler<CloseComplaint, ComplaintCloseResponse>
{
    public async Task<Result<ComplaintCloseResponse>> HandleAsync(CloseComplaint command, CancellationToken cancellationToken) =>
        (await engine.CloseAsync(command.Request, cancellationToken).ConfigureAwait(false)).Map(c =>
        {
            var (complaint, number, deadlines) = ComplaintViews.Of(c);
            return new ComplaintCloseResponse { Complaint = complaint, Number = number, Deadlines = deadlines };
        });
}

/// <summary>Read side: <c>cmp.Complaint.get</c> and <c>cmp.Complaint.query</c>.</summary>
internal sealed class ComplaintReader(ComplianceDbContext db, ComplaintEngine engine, RequestContext context, ILegalEntityDirectory legalEntities)
{
    private const int MaxPage = 200;

    public Task<ComplaintCase?> GetAsync(Guid id, CancellationToken ct) => engine.FindAsync(id, ct);

    public async Task<(List<ComplaintCase> Items, string? Next, int Limit)> QueryAsync(string? cursor, int? limit, string? status, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, MaxPage);
        var legalEntity = ClockLookup.LegalEntity(context, legalEntities);
        var query = db.Set<ComplaintRow>().AsNoTracking().Where(r => r.LegalEntityId == legalEntity);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = status.Trim().ToUpperInvariant();
            query = query.Where(r => r.Status == wanted);
        }

        // TODO(hardening): keyset paging in SQL; the strongly typed id is compared in memory here (as in DSAR).
        var all = await query.OrderBy(r => r.ComplaintId).ToListAsync(ct).ConfigureAwait(false);
        var rows = (Guid.TryParse(cursor, out var after) ? all.Where(r => r.ComplaintId.Value.CompareTo(after) > 0) : all).Take(take + 1).ToList();
        var page = rows.Take(take).ToList();
        var clockRows = await engine.ClocksOfAsync(page, ct).ConfigureAwait(false);
        var items = page.Select(r => new ComplaintCase(r, [.. clockRows.Where(c => r.ReplyClockId == c.ClockId.Value)])).ToList();
        return (items, rows.Count > take ? page[^1].ComplaintId.Value.ToString("D", CultureInfo.InvariantCulture) : null, take);
    }

    /// <summary>
    /// The published complaint-handling procedure (REQ-CMP-141). TODO(hardening): the text is a stand-in until the entity publishes
    /// its own (versioned, per legal entity); the 50-day period is the secondary-source value of the reply clock.
    /// </summary>
    public static ComplaintProcedureTextResponse ProcedureText(string? language)
    {
        var greek = string.Equals(language, "el", StringComparison.OrdinalIgnoreCase) || (language?.StartsWith("el-", StringComparison.OrdinalIgnoreCase) ?? false);
        return greek
            ? new ComplaintProcedureTextResponse
            {
                Language = "el",
                Version = "0.1",
                Text = "Διαδικασία διαχείρισης παραπόνων: το παράπονο καταγράφεται, επιβεβαιώνεται η παραλαβή του, διερευνάται και απαντάται εγγράφως και αιτιολογημένα εντός 50 ημερολογιακών ημερών. Αν δεν συμφωνείτε με την απάντηση, μπορείτε να προσφύγετε σε φορέα εξωδικαστικής επίλυσης διαφορών.",
            }
            : new ComplaintProcedureTextResponse
            {
                Language = "en",
                Version = "0.1",
                Text = "Complaint-handling procedure: your complaint is recorded, its receipt acknowledged, investigated, and answered in writing with reasons within 50 calendar days. If you disagree with the reply you may refer the matter to an alternative dispute resolution body.",
            };
    }
}
