using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Compliance.Contracts.Api;
using CoreIns.Modules.Compliance.Contracts.Events;
using CoreIns.Modules.Compliance.Domain;
using CoreIns.Modules.Compliance.Persistence;
using CoreIns.Modules.Market.Contracts.Spi;
using CoreIns.Platform.Context;
using CoreIns.Platform.Events;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Compliance.Commands;

/// <summary>What a resubmit did to one document.</summary>
internal sealed record ResubmitOutcome(FiscalDocumentRow Row, bool Resubmitted);

/// <summary>What a cancel did: the (now Cancelled) original and, for a Registered original, the linked CANCELLATION document.</summary>
internal sealed record CancelOutcome(FiscalDocumentRow Original, FiscalDocumentRow? Cancellation);

/// <summary>What a correction did: the credit that reverses the original and the new ISSUE document with the corrected lines.</summary>
internal sealed record CorrectOutcome(FiscalDocumentRow Original, FiscalDocumentRow Credit, FiscalDocumentRow Corrected);

/// <summary>
/// The fiscal-document operations of BO-CMP-06 (REQ-CMP-049..052). A fiscal document is never edited: a Rejected or
/// Pending document is sent again as it is (<c>resubmit</c>), a Registered one is reversed by a linked CREDIT and
/// replaced by a new ISSUE (<c>correct</c>) or cancelled at the channel with a linked CANCELLATION document
/// (<c>cancel</c>); a document that never got a MARK is simply withdrawn. Everything runs on the bound
/// <see cref="IFiscalDocumentChannel"/>; the only channel today is the myDATA stub (open user decision #15: the real
/// myDATA / e-invoicing channel stays a stub and the document layouts stay Unverified, so Production has no channel
/// and every operation is CMP-ERR-TRANSPORT-UNAVAILABLE there).
/// </summary>
internal sealed class FiscalDocumentOperations(
    ComplianceDbContext db,
    RequestContext context,
    ILegalEntityDirectory legalEntities,
    IClock clock,
    IEventPublisher events,
    IServiceProvider services)
{
    private static readonly string Registered = FiscalDocuments.Code(FiscalDocumentStatus.Registered);
    private static readonly string Rejected = FiscalDocuments.Code(FiscalDocumentStatus.Rejected);
    private static readonly string Pending = FiscalDocuments.Code(FiscalDocumentStatus.Pending);
    private static readonly string Cancelled = FiscalDocuments.Code(FiscalDocumentStatus.Cancelled);
    private static readonly string Issue = FiscalDocuments.Code(FiscalDocumentRole.Issue);
    private static readonly string Credit = FiscalDocuments.Code(FiscalDocumentRole.Credit);
    private static readonly string Cancellation = FiscalDocuments.Code(FiscalDocumentRole.Cancellation);

    private LegalEntityId LegalEntity =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private IFiscalDocumentChannel? Channel => services.GetService<IFiscalDocumentChannel>();

    private static DomainError Error(string name, string detail) => DomainError.Of(ModuleCode.CMP, name, detail);

    private static DomainError Unavailable() => Error("TRANSPORT-UNAVAILABLE", "No fiscal-document channel is bound for this legal entity.");

    /// <summary>Loads a tracked document of the caller's legal entity, or null.</summary>
    public async Task<FiscalDocumentRow?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var key = new FiscalDocumentId(id);
        return await db.FiscalDocuments.SingleOrDefaultAsync(d => d.FiscalDocumentId == key && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a Rejected or Pending document to the channel again, unchanged: same series, number, lines and fiscal key
    /// (so the channel de-duplicates). An already Registered document is a replay and does nothing; a Cancelled one is
    /// refused. A dry run checks eligibility only.
    /// </summary>
    public async Task<Result<ResubmitOutcome>> ResubmitAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        var row = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        return row is null ? Error("NOT-FOUND", "The fiscal document does not exist in your legal entity.") : await ResubmitAsync(row, reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resubmits a loaded document (see <see cref="ResubmitAsync(Guid, string, CancellationToken)"/>).</summary>
    public async Task<Result<ResubmitOutcome>> ResubmitAsync(FiscalDocumentRow row, string reason, CancellationToken cancellationToken)
    {
        _ = reason; // audited by the command's auditor
        if (row.Status == Registered)
        {
            return new ResubmitOutcome(row, false);
        }

        if (row.Status != Rejected && row.Status != Pending)
        {
            return Error("NOT-REJECTED", "Only a Rejected or Pending fiscal document can be resubmitted.");
        }

        if (Channel is not { } channel)
        {
            return Unavailable();
        }

        if (context.DryRun)
        {
            return new ResubmitOutcome(row, true);
        }

        var now = clock.Now;
        var built = await BuildAsync(channel, row, cancellationToken).ConfigureAwait(false);
        var result = await channel.SubmitAsync(built, FiscalKey(row), cancellationToken).ConfigureAwait(false);
        var correlatedMark = await MarkOfAsync(row.CorrelatedDocumentId, cancellationToken).ConfigureAwait(false);
        row.RejectionCodes = [];
        RequestFiscalDocumentHandler.Apply(events, row, result, now, correlatedMark);
        row.RecordVersion++;
        return new ResubmitOutcome(row, true);
    }

    /// <summary>
    /// Cancels a document. Registered ISSUE: the channel cancels it by its MARK and CMP issues a linked CANCELLATION
    /// document (own gapless series); the original becomes Cancelled and <c>FiscalDocCancelled</c> is published. Rejected
    /// or Pending ISSUE (no MARK yet): withdrawn locally, nothing is sent. Refused once a credit, correction or
    /// cancellation is linked to it. Cancelling a Cancelled document is a replay.
    /// </summary>
    public async Task<Result<CancelOutcome>> CancelAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        var row = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Error("NOT-FOUND", "The fiscal document does not exist in your legal entity.");
        }

        if (row.Status == Cancelled)
        {
            var existing = await db.FiscalDocuments.AsNoTracking().FirstOrDefaultAsync(
                d => d.LegalEntityId == row.LegalEntityId && d.CorrelatedDocumentId == row.FiscalDocumentId.Value && d.Role == Cancellation, cancellationToken).ConfigureAwait(false);
            return new CancelOutcome(row, existing);
        }

        if (row.Role != Issue)
        {
            return Error("NOT-CANCELLABLE", "Only an ISSUE document can be cancelled; reverse a credit through a new document.");
        }

        if (row.Status != Registered && row.Status != Rejected && row.Status != Pending)
        {
            return Error("NOT-CANCELLABLE", "The fiscal document is not in a cancellable state.");
        }

        if (await HasLinkedAsync(row, cancellationToken).ConfigureAwait(false))
        {
            return Error("NOT-CANCELLABLE", "The document was already credited, corrected or cancelled.");
        }

        var now = clock.Now;
        if (row.Status != Registered)
        {
            // Never registered: nothing exists at the channel to cancel; the source is withdrawn.
            row.Status = Cancelled;
            row.RecordVersion++;
            return new CancelOutcome(row, null);
        }

        if (Channel is not { } channel)
        {
            return Unavailable();
        }

        var series = await channel.SeriesAsync(row.DocumentType, cancellationToken).ConfigureAwait(false);
        var seriesId = series.SeriesId + FiscalDocuments.CancellationSeriesSuffix;
        var number = await RequestFiscalDocumentHandler.NextNumberAsync(db, row.LegalEntityId, seriesId, cancellationToken).ConfigureAwait(false);
        var cancellation = NewRow(row, Cancellation, row.Revision, row.Lines, row.Total, row.Currency, seriesId, number, row.FiscalDocumentId.Value, now);

        if (!context.DryRun)
        {
            var result = await channel.CancelAsync(row.Mark!, reason, cancellationToken).ConfigureAwait(false);
            if (result.Status == FiscalSubmissionStatus.Queued)
            {
                return Unavailable();
            }

            if (result.Status != FiscalSubmissionStatus.Registered)
            {
                return Error("NOT-CANCELLABLE", $"The channel refused the cancellation ({(result.Rejections.Count > 0 ? result.Rejections[0].Code : result.Status.ToString())}).");
            }

            var cancellationMark = result.RegistrationId is { Length: > 0 } m ? m : row.Mark!;
            cancellation.Status = Registered;
            cancellation.Mark = cancellationMark;
            cancellation.Uid = result.Uid is { Length: > 0 } u ? u : row.Uid;
            cancellation.QrPayloadRef = $"cmp:fiscal-document:{cancellation.FiscalDocumentId.Value:D}:qr";
            cancellation.RegisteredAt = now;
            events.Publish(new OutgoingEvent(
                EventDescriptor.From(FiscalDocCancelledV1.Descriptor), "FiscalDocument", row.FiscalDocumentId.Value.ToString(),
                new FiscalDocCancelledV1 { CancellationMark = cancellationMark, Reason = reason },
                BusinessKeys.Empty.With("fiscalDocumentId", row.FiscalDocumentId.Value.ToString())) { OccurredAt = now });
        }

        row.Status = Cancelled;
        row.RecordVersion++;
        db.FiscalDocuments.Add(cancellation);
        return new CancelOutcome(row, cancellation);
    }

    /// <summary>
    /// Corrects a Registered ISSUE document: a CREDIT document (the original's lines, correlated to it) reverses it and a
    /// new ISSUE document (next revision, correlated to it) carries the corrected lines. The original is left as it is. If
    /// the credit is not registered nothing is stored (a replacement without its credit would double-bill). A repeated
    /// call returns the existing pair. A Rejected document is resubmitted or cancelled, not corrected.
    /// </summary>
    public async Task<Result<CorrectOutcome>> CorrectAsync(
        Guid id, IReadOnlyList<FiscalDocumentCorrectRequest.CorrectionItem> corrections, string reason, CancellationToken cancellationToken)
    {
        _ = reason; // audited by the command's auditor
        var row = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return Error("NOT-FOUND", "The fiscal document does not exist in your legal entity.");
        }

        var linked = await db.FiscalDocuments.Where(d => d.LegalEntityId == row.LegalEntityId && d.CorrelatedDocumentId == row.FiscalDocumentId.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var existingCredit = linked.FirstOrDefault(d => d.Role == Credit);
        var existingCorrected = linked.FirstOrDefault(d => d.Role == Issue);
        if (existingCredit is not null && existingCorrected is not null)
        {
            return new CorrectOutcome(row, existingCredit, existingCorrected); // replay
        }

        if (row.Role != Issue || row.Status != Registered || linked.Count > 0)
        {
            return Error("NOT-CORRECTABLE", "Only a Registered ISSUE document that was not already credited, corrected or cancelled can be corrected.");
        }

        var currency = Currency.FromCode(row.Currency);
        if (corrections.Any(l => l.Amount.Currency != currency || !l.Amount.IsRoundedToMinorUnits || string.IsNullOrWhiteSpace(l.FiscalCategoryKey)))
        {
            return Error("FISCAL-TOTAL", "Corrected lines must have a category and share the original's currency, rounded to minor units (REQ-CMP-039).");
        }

        var lines = corrections.Select(l => new FiscalDocumentRequestRequest.LineItem
        {
            FiscalCategoryKey = l.FiscalCategoryKey, Amount = l.Amount, ChargeId = l.ChargeId, PaymentId = l.PaymentId,
        }).ToList();
        var total = Money.Sum(lines.Select(l => l.Amount), currency).Amount;

        var replacementRevision = row.Revision + 1;
        var clash = await db.FiscalDocuments.AsNoTracking().AnyAsync(
            d => d.LegalEntityId == row.LegalEntityId && d.SourceType == row.SourceType && d.SourceId == row.SourceId
                && ((d.Role == Credit && d.Revision == row.Revision) || (d.Role == Issue && d.Revision == replacementRevision)),
            cancellationToken).ConfigureAwait(false);
        if (clash)
        {
            return Error("NOT-CORRECTABLE", "A credit or a later revision already exists for the same fiscal source.");
        }

        if (Channel is not { } channel)
        {
            return Unavailable();
        }

        var now = clock.Now;
        var series = (await channel.SeriesAsync(row.DocumentType, cancellationToken).ConfigureAwait(false)).SeriesId;
        var creditSeries = series + FiscalDocuments.CreditSeriesSuffix;
        var creditNumber = await RequestFiscalDocumentHandler.NextNumberAsync(db, row.LegalEntityId, creditSeries, cancellationToken).ConfigureAwait(false);
        var credit = NewRow(row, Credit, row.Revision, row.Lines, row.Total, row.Currency, creditSeries, creditNumber, row.FiscalDocumentId.Value, now);
        var issueNumber = await RequestFiscalDocumentHandler.NextNumberAsync(db, row.LegalEntityId, series, cancellationToken).ConfigureAwait(false);
        var corrected = NewRow(row, Issue, replacementRevision, JsonSerializer.Serialize(lines, SharedKernelJson.Options), total, row.Currency, series, issueNumber, row.FiscalDocumentId.Value, now);

        if (!context.DryRun)
        {
            var creditResult = await channel.SubmitAsync(await BuildAsync(channel, credit, cancellationToken).ConfigureAwait(false), FiscalKey(credit), cancellationToken).ConfigureAwait(false);
            RequestFiscalDocumentHandler.Apply(events, credit, creditResult, now, row.Mark);
            if (credit.Status == Pending)
            {
                return Unavailable();
            }

            if (credit.Status != Registered)
            {
                return Error("NOT-CORRECTABLE", $"The channel rejected the credit ({string.Join(", ", credit.RejectionCodes)}); nothing was stored.");
            }

            var issueResult = await channel.SubmitAsync(await BuildAsync(channel, corrected, cancellationToken).ConfigureAwait(false), FiscalKey(corrected), cancellationToken).ConfigureAwait(false);
            RequestFiscalDocumentHandler.Apply(events, corrected, issueResult, now, row.Mark);
        }

        db.FiscalDocuments.AddRange(credit, corrected);
        return new CorrectOutcome(row, credit, corrected);
    }

    /// <summary>The QR data of a document: its identifiers and, once Registered, the text to encode (layout Unverified, OQ-012).</summary>
    public async Task<FiscalDocumentQrPayloadResponse?> QrPayloadAsync(Guid id, CancellationToken cancellationToken)
    {
        var legalEntity = LegalEntity;
        var key = new FiscalDocumentId(id);
        var row = await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.FiscalDocumentId == key && d.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        // TODO(hardening): the real QR layout (AADE URL with MARK, UID, authentication code) is Unverified (open user
        // decision #15); until it is verified this is a CoreIns placeholder text and `layoutVerified` stays false.
        var payload = row.Status == Registered && row.Mark is not null && row.Uid is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{row.Mark}|{row.Uid}|{row.Series}|{row.Number}|{row.IssueDate}|{row.Total}|{row.Currency}")
            : null;
        return new FiscalDocumentQrPayloadResponse
        {
            FiscalDocumentId = row.FiscalDocumentId,
            Status = Enum.Parse<FiscalDocumentQrPayloadResponse.StatusValue>(FiscalDocumentReaderNames.Pascal(row.Status)),
            Mark = row.Mark,
            Uid = row.Uid,
            Series = row.Series,
            Number = row.Number,
            QrPayloadRef = row.QrPayloadRef,
            QrPayload = payload,
            Stub = row.Stub,
            LayoutVerified = false,
        };
    }

    private async Task<bool> HasLinkedAsync(FiscalDocumentRow row, CancellationToken cancellationToken) =>
        await db.FiscalDocuments.AsNoTracking().AnyAsync(
            d => d.LegalEntityId == row.LegalEntityId && d.CorrelatedDocumentId == row.FiscalDocumentId.Value, cancellationToken).ConfigureAwait(false);

    private async Task<string?> MarkOfAsync(Guid? documentId, CancellationToken cancellationToken)
    {
        if (documentId is not { } id)
        {
            return null;
        }

        var key = new FiscalDocumentId(id);
        return await db.FiscalDocuments.AsNoTracking().Where(d => d.FiscalDocumentId == key).Select(d => d.Mark).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FiscalKey(FiscalDocumentRow row) => $"{row.SourceType}|{row.SourceId}|{row.Role}|{row.Revision}";

    /// <summary>Rebuilds the channel document of a stored row, keeping its series and number.</summary>
    private static async Task<FiscalDocument> BuildAsync(IFiscalDocumentChannel channel, FiscalDocumentRow row, CancellationToken cancellationToken)
    {
        var lines = JsonSerializer.Deserialize<List<FiscalDocumentRequestRequest.LineItem>>(row.Lines, SharedKernelJson.Options) ?? [];
        var source = new FiscalSource
        {
            LegalEntityId = row.LegalEntityId.Value,
            SourceType = row.SourceType,
            SourceId = row.SourceId,
            DocumentType = row.DocumentType,
            IssueDate = row.IssueDate.Value,
            CounterpartyRef = row.CounterpartyPartyId.Value.ToString("D"),
            Lines = [.. lines.Select(l => new FiscalSourceLine(l.FiscalCategoryKey, new SpiMoney(l.Amount.Amount, l.Amount.Currency.Code), []))],
        };
        var built = await channel.BuildAsync(source, cancellationToken).ConfigureAwait(false);
        return built with { Series = row.Series ?? built.Series, Number = row.Number ?? built.Number };
    }

    private FiscalDocumentRow NewRow(
        FiscalDocumentRow original, string role, int revision, string linesJson, decimal total, string currency, string series, long number, Guid correlated, Instant now) =>
        new()
        {
            FiscalDocumentId = FiscalDocumentId.New(),
            LegalEntityId = original.LegalEntityId,
            Jurisdiction = original.Jurisdiction,
            SourceType = original.SourceType,
            SourceId = original.SourceId,
            Role = role,
            Revision = revision,
            CorrelatedDocumentId = correlated,
            Status = Pending,
            DocumentType = original.DocumentType,
            DocumentTypeIsPlaceholder = original.DocumentTypeIsPlaceholder,
            IssueDate = original.IssueDate,
            CounterpartyPartyId = original.CounterpartyPartyId,
            Total = total,
            Currency = currency,
            Lines = linesJson,
            Series = series,
            Number = number.ToString(CultureInfo.InvariantCulture),
            Channel = original.Channel,
            Stub = original.Stub,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RecordVersion = 1,
        };
}

/// <summary>Status code helpers shared by the operations.</summary>
internal static class FiscalDocumentReaderNames
{
    /// <summary>PascalCase of an upper-snake code (<c>REGISTERED</c> becomes <c>Registered</c>).</summary>
    public static string Pascal(string code) =>
        string.Concat(code.Split('_').Select(part => part[..1] + part[1..].ToLowerInvariant()));
}
