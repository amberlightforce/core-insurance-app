using System.Globalization;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Work.Contracts.Api;
using CoreIns.Modules.Work.Domain;
using CoreIns.Modules.Work.Notes;
using CoreIns.Modules.Work.Persistence;
using CoreIns.Modules.Work.Queries;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Work.Inbound;

internal sealed record InboundVerifyBody(Guid? Id, string? DocumentClass, JsonElement? Data, string? ObjectRef, int? RecordVersion);

internal sealed record InboundSplitPart(string? PageRange, string? DocumentClass);

internal sealed record InboundSplitBody(Guid? Id, List<InboundSplitPart>? Parts);

internal sealed record InboundLinkBody(Guid? Id, string? ObjectRef, string? Reason);

internal sealed record InboundRejectBody(Guid? Id, string? Reason);

internal sealed record InboundRedactBody(Guid? Id, string? Reason, string? Content, string? FileName);

internal sealed record InboundReprocessBody(Guid? Id);

internal sealed record InboundExportBody(string? State, string? ObjectRef);

/// <summary>An inbound document as the staff UI sees it. The bytes are not part of it.</summary>
internal sealed record InboundView(
    Guid Id, Guid EnvelopeId, string InboundNumber, string FileName, string ContentType, long SizeBytes, string ContentHash, string State,
    string? ScanVerdict, string? ScanSignature, string? Scanner, string Channel, string? SuggestedClass, string? DocumentClass, string? ObjectRef,
    JsonElement KeyedData, JsonElement Sender, Guid? ParentId, string? PageRange, string? RejectReason, Guid? ArchiveId, Guid? RedactedArchiveId,
    bool LegalHold, bool Redacted, string CreatedBy, Instant CreatedAt, Instant UpdatedAt, string? VerifiedBy, Instant? VerifiedAt, int RecordVersion)
{
    public JsonElement ToElement() => JsonSerializer.SerializeToElement(this, SharedKernelJson.Options);
}

internal sealed record InboundPage(IReadOnlyList<InboundView> Items, string? NextCursor, int Limit);

/// <summary>
/// The inbound-document rules (BO-WRK-11): intake with a malware scan (QUARANTINED until clean, infected files never stored), manual keying
/// (verify), split, link, redact (original kept under hold) and export. Mutations run in the command pipeline's transaction.
/// TODO(hardening): split records page ranges only (no byte-level page split); redact takes a redacted file from the clerk (no redaction tool);
/// the DOC archive hold of the original is a flag on the row, not a DOC retention class; DSAR participant does not yet cover inbound documents;
/// scanning is synchronous in the request (a queue + worker would let large batches scan in the background).
/// </summary>
internal sealed class InboundService(
    WorkDbContext db, RequestContext context, ILegalEntityDirectory legalEntities, IClock clock, WorkCatalogue catalogue, IServiceProvider services)
{
    public const int MaxFiles = 20;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxLimit = 200;
    public const int DefaultLimit = 25;
    public const int MaxExportRows = 5000;
    public const int MaxParts = 20;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
    };

    private static readonly string[] States =
        [InboundDocumentRow.Quarantined, InboundDocumentRow.Clean, InboundDocumentRow.Infected, InboundDocumentRow.Verified, InboundDocumentRow.Rejected, InboundDocumentRow.Split];

    private LegalEntityId LegalEntity =>
        legalEntities.Resolve(context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity."));

    private IMalwareScanner? Scanner => services.GetService<IMalwareScanner>();

    private IDocumentArchiveService? Archive => services.GetService<IDocumentArchiveService>();

    public async Task<Result<(Guid EnvelopeId, IReadOnlyList<InboundDocumentRow> Rows)>> SubmitAsync(InboundDocumentSubmitRequest request, CancellationToken cancellationToken)
    {
        if (Scanner is not { } scanner || Archive is not { } archive)
        {
            return WorkErrors.Of(WorkErrors.CapabilityOff, "No malware scanner is bound; inbound documents are refused until one is.");
        }

        if (request.Files is not { ValueKind: JsonValueKind.Array } filesElement || filesElement.GetArrayLength() == 0)
        {
            return WorkErrors.Invalid("files is a non-empty array of {fileName, contentType, content (base64)}.");
        }

        if (filesElement.GetArrayLength() > MaxFiles)
        {
            return WorkErrors.Of(WorkErrors.FileSize, $"One intake holds at most {MaxFiles} files.");
        }

        var channel = request.Channel is { ValueKind: JsonValueKind.String } c ? c.GetString()!.Trim().ToUpperInvariant() : "STAFF_UPLOAD";
        if (channel.Length is 0 or > 32 || !channel.All(ch => ch is (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9')))
        {
            return WorkErrors.Invalid("channel is 1..32 characters of A-Z, 0-9 and underscore.");
        }

        var suggested = request.SuggestedClass is { ValueKind: JsonValueKind.String } sc ? sc.GetString()!.Trim() : null;
        if (suggested is { Length: > 64 } || suggested?.Any(char.IsControl) == true)
        {
            return WorkErrors.Invalid("suggestedClass is at most 64 characters.");
        }

        string? link = null;
        if (request.Link is { ValueKind: JsonValueKind.String } l)
        {
            var parsed = NoteService.ParseObject(l.GetString());
            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            link = $"{parsed.Value.Module}/{parsed.Value.Type}/{parsed.Value.Id}";
        }

        var sender = JsonText(request.Sender, 2000);
        var envelope = JsonText(request.EnvelopeMetadata, 4000);
        if (sender is null || envelope is null)
        {
            return WorkErrors.Invalid("sender and envelopeMetadata are JSON of at most 2000 and 4000 characters.");
        }

        var files = new List<(string Name, string Type, byte[] Bytes)>();
        foreach (var file in filesElement.EnumerateArray())
        {
            var checkedFile = ReadFile(file);
            if (checkedFile.IsFailure)
            {
                return checkedFile.Error!;
            }

            files.Add(checkedFile.Value);
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var envelopeId = Guid.NewGuid();
        var rows = new List<InboundDocumentRow>();
        foreach (var (name, type, bytes) in files)
        {
            var row = NewRow(envelopeId, name, type, bytes, channel, sender, envelope, suggested, link, actor.Actor.Id);
            await ApplyScanAsync(row, bytes, scanner, archive, cancellationToken).ConfigureAwait(false);
            db.InboundDocuments.Add(row);
            rows.Add(row);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return (envelopeId, rows);
    }

    public async Task<Result<InboundDocumentRow>> VerifyAsync(InboundVerifyBody body, CancellationToken cancellationToken)
    {
        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State != InboundDocumentRow.Clean)
        {
            return Wrong(row, "Only a clean document can be verified.");
        }

        if (body.RecordVersion is { } expected && expected != row.RecordVersion)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The document changed meanwhile.");
        }

        var documentClass = body.DocumentClass?.Trim();
        if (string.IsNullOrEmpty(documentClass) || documentClass.Length > 64 || body.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return WorkErrors.Of(WorkErrors.RequiredMissing, "documentClass (at most 64 characters) and data (an object) are required.");
        }

        var text = data.GetRawText();
        if (text.Length > 8000)
        {
            return WorkErrors.Invalid("data is at most 8000 characters.");
        }

        if (body.ObjectRef is not null)
        {
            var link = ParseLink(body.ObjectRef);
            if (link.IsFailure)
            {
                return link.Error!;
            }

            row.LinkObject = link.Value;
        }

        if (row.LinkObject is null)
        {
            return WorkErrors.Of(WorkErrors.RequiredMissing, "The document must be linked to a business object before it is verified.");
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        row.DocumentClass = documentClass;
        row.KeyedData = text;
        row.State = InboundDocumentRow.Verified;
        row.VerifiedBy = actor.Actor.Id;
        row.VerifiedAt = clock.Now;
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<(InboundDocumentRow Parent, IReadOnlyList<InboundDocumentRow> Children)>> SplitAsync(InboundSplitBody body, CancellationToken cancellationToken)
    {
        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State != InboundDocumentRow.Clean)
        {
            return Wrong(row, "Only a clean, unverified document can be split.");
        }

        if (body.Parts is not { Count: >= 2 } parts || parts.Count > MaxParts
            || parts.Any(p => string.IsNullOrWhiteSpace(p.PageRange) || p.PageRange.Length > 64 || p.DocumentClass?.Length > 64))
        {
            return WorkErrors.Invalid($"parts is 2..{MaxParts} entries of {{pageRange, documentClass}}.");
        }

        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var children = new List<InboundDocumentRow>();
        foreach (var part in parts)
        {
            var child = NewRow(row.EnvelopeId, row.FileName, row.ContentType, null, row.Channel, row.Sender, row.EnvelopeMetadata, row.SuggestedClass, row.LinkObject, actor.Actor.Id);
            child.SizeBytes = row.SizeBytes;
            child.ContentHash = row.ContentHash;
            child.ArchiveId = row.ArchiveId;
            child.State = InboundDocumentRow.Clean;
            child.ScanVerdict = row.ScanVerdict;
            child.Scanner = row.Scanner;
            child.ScannedAt = row.ScannedAt;
            child.ParentId = row.InboundDocumentId;
            child.PageRange = part.PageRange!.Trim();
            child.DocumentClass = null;
            child.SuggestedClass = part.DocumentClass?.Trim() ?? row.SuggestedClass;
            db.InboundDocuments.Add(child);
            children.Add(child);
        }

        row.State = InboundDocumentRow.Split;
        row.UpdatedAt = now;
        row.RecordVersion++;
        var saved = await TrySaveAsync(cancellationToken).ConfigureAwait(false);
        if (saved is not null)
        {
            return saved;
        }

        return (row, children);
    }

    public async Task<Result<InboundDocumentRow>> LinkAsync(InboundLinkBody body, bool relink, CancellationToken cancellationToken)
    {
        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State is InboundDocumentRow.Rejected or InboundDocumentRow.Infected or InboundDocumentRow.Split)
        {
            return Wrong(row, "A rejected, infected or split document cannot be linked.");
        }

        var link = ParseLink(body.ObjectRef);
        if (link.IsFailure)
        {
            return link.Error!;
        }

        if (relink)
        {
            if (row.LinkObject is null || string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 1000)
            {
                return WorkErrors.Of(WorkErrors.RequiredMissing, "Relinking needs an existing link and a reason of at most 1000 characters.");
            }
        }
        else if (row.LinkObject is not null)
        {
            return Wrong(row, "The document is already linked; use relink.");
        }

        row.LinkObject = link.Value;
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<InboundDocumentRow>> RejectAsync(InboundRejectBody body, CancellationToken cancellationToken)
    {
        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State is not (InboundDocumentRow.Quarantined or InboundDocumentRow.Clean or InboundDocumentRow.Infected))
        {
            return Wrong(row, "Only a received (not yet verified) document can be rejected.");
        }

        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 1000)
        {
            return WorkErrors.Of(WorkErrors.RequiredMissing, "A reason of at most 1000 characters is required.");
        }

        row.State = InboundDocumentRow.Rejected;
        row.RejectReason = body.Reason.Trim();
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores a redacted copy next to the original and puts the original on hold; the original is never replaced.</summary>
    public async Task<Result<InboundDocumentRow>> RedactAsync(InboundRedactBody body, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        if (!actor.IsManager)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only a manager may redact a document.");
        }

        if (Scanner is not { } scanner || Archive is not { } archive)
        {
            return WorkErrors.Of(WorkErrors.CapabilityOff, "No malware scanner is bound; inbound documents are refused until one is.");
        }

        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State is not (InboundDocumentRow.Clean or InboundDocumentRow.Verified) || row.ArchiveId is null)
        {
            return Wrong(row, "Only a clean or verified document can be redacted.");
        }

        if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Length > 1000)
        {
            return WorkErrors.Of(WorkErrors.RequiredMissing, "A reason of at most 1000 characters is required.");
        }

        var file = ReadFile(JsonSerializer.SerializeToElement(new { fileName = body.FileName ?? row.FileName, content = body.Content }));
        if (file.IsFailure)
        {
            return file.Error!;
        }

        var scan = await scanner.ScanAsync(file.Value.Bytes, cancellationToken).ConfigureAwait(false);
        if (scan.Verdict != ScanVerdict.Clean)
        {
            return WorkErrors.Invalid("The redacted file did not pass the malware scan.");
        }

        var stored = await archive.StoreAsync(file.Value.Bytes, file.Value.Type, JsonSerializer.Serialize(new { inbound = row.InboundNumber, redactionOf = row.ArchiveId }), cancellationToken)
            .ConfigureAwait(false);
        row.RedactedArchiveId = stored.ArchiveId;
        row.RedactionReason = body.Reason.Trim();
        row.LegalHold = true;
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Scans a quarantined document again (after a scanner outage).</summary>
    public async Task<Result<InboundDocumentRow>> ReprocessAsync(InboundReprocessBody body, CancellationToken cancellationToken)
    {
        if (Scanner is not { } scanner || Archive is not { } archive)
        {
            return WorkErrors.Of(WorkErrors.CapabilityOff, "No malware scanner is bound; inbound documents are refused until one is.");
        }

        var row = await FindAsync(body.Id, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound();
        }

        if (row.State != InboundDocumentRow.Quarantined || row.ArchiveId is not { } archiveId)
        {
            return Wrong(row, "Only a quarantined document can be reprocessed.");
        }

        var content = await archive.ReadAsync(archiveId, cancellationToken).ConfigureAwait(false);
        if (content is null)
        {
            return WorkErrors.Invalid("The archived bytes of this document are missing.");
        }

        await ApplyScanAsync(row, content.Content, scanner, archive, cancellationToken).ConfigureAwait(false);
        return await SaveAsync(row, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<InboundView>> GetAsync(string id, CancellationToken cancellationToken)
    {
        var row = Guid.TryParse(id, out var guid) ? await FindAsync(guid, cancellationToken, tracking: false).ConfigureAwait(false) : null;
        return row is null ? NotFound() : View(row);
    }

    public async Task<Result<InboundPage>> ListAsync(string? state, string? objectRef, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var skip = 0;
        if (limit is < 1 or > MaxLimit || (cursor is not null && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out skip) || skip < 0)))
        {
            return WorkErrors.Invalid($"cursor is malformed or limit is outside 1..{MaxLimit}.");
        }

        var query = Filter(state, objectRef);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        // TODO(hardening): keyset cursor; an offset cursor can skip or repeat rows while documents arrive.
        var take = limit ?? DefaultLimit;
        var rows = await query.Value.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.InboundDocumentId).Skip(skip).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new InboundPage([.. rows.Take(take).Select(View)], rows.Count > take ? (skip + take).ToString(CultureInfo.InvariantCulture) : null, take);
    }

    /// <summary>CSV of the document metadata (managers); never the bytes.</summary>
    public async Task<Result<(string Csv, int Rows)>> ExportAsync(InboundExportBody body, CancellationToken cancellationToken)
    {
        var actor = await catalogue.ActorAsync(cancellationToken).ConfigureAwait(false);
        if (!actor.IsManager)
        {
            return WorkErrors.Of(WorkErrors.Permission, "Only a manager may export inbound documents.");
        }

        var query = Filter(body.State, body.ObjectRef);
        if (query.IsFailure)
        {
            return query.Error!;
        }

        var rows = await query.Value.OrderBy(d => d.CreatedAt).ThenBy(d => d.InboundDocumentId).Take(MaxExportRows + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > MaxExportRows)
        {
            return WorkErrors.Of(WorkErrors.LimitExceeded, $"An export holds at most {MaxExportRows} documents; narrow it with a filter.");
        }

        var csv = new StringBuilder("inboundNumber,fileName,state,scanVerdict,channel,documentClass,object,hash,legalHold,createdAt\r\n");
        foreach (var d in rows)
        {
            csv.Append(string.Join(
                ',',
                d.InboundNumber, Cell(d.FileName), d.State, d.ScanVerdict ?? string.Empty, d.Channel, Cell(d.DocumentClass ?? string.Empty), Cell(d.LinkObject ?? string.Empty),
                d.ContentHash, d.LegalHold ? "true" : "false", d.CreatedAt.ToString())).Append("\r\n");
        }

        return (csv.ToString(), rows.Count);
    }

    public InboundView View(InboundDocumentRow r) => new(
        r.InboundDocumentId, r.EnvelopeId, r.InboundNumber, r.FileName, r.ContentType, r.SizeBytes, r.ContentHash, r.State, r.ScanVerdict, r.ScanSignature, r.Scanner,
        r.Channel, r.SuggestedClass, r.DocumentClass, r.LinkObject, Parse(r.KeyedData), Parse(r.Sender), r.ParentId, r.PageRange, r.RejectReason, r.ArchiveId,
        r.RedactedArchiveId, r.LegalHold, r.RedactedArchiveId is not null, r.CreatedBy, r.CreatedAt, r.UpdatedAt, r.VerifiedBy, r.VerifiedAt, r.RecordVersion);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DomainError NotFound() => DomainError.Of(ModuleCode.WRK, PlatformErrors.NotFound, "The inbound document does not exist.");

    private static DomainError Wrong(InboundDocumentRow row, string detail) => WorkErrors.Of(WorkErrors.Stale, $"{detail} It is {row.State}.");

    private static string? JsonText(JsonElement? element, int max)
    {
        if (element is not { } e || e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "{}";
        }

        var text = e.GetRawText();
        return text.Length <= max ? text : null;
    }

    private static string Cell(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return '"' + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static Result<string> ParseLink(string? text)
    {
        var parsed = NoteService.ParseObject(text);
        return parsed.IsFailure ? parsed.Error! : $"{parsed.Value.Module}/{parsed.Value.Type}/{parsed.Value.Id}";
    }

    private static Result<(string Name, string Type, byte[] Bytes)> ReadFile(JsonElement file)
    {
        if (file.ValueKind != JsonValueKind.Object
            || !file.TryGetProperty("fileName", out var nameElement) || nameElement.ValueKind != JsonValueKind.String
            || !file.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.String)
        {
            return WorkErrors.Invalid("Each file is {fileName, content (base64)}.");
        }

        var name = Path.GetFileName(nameElement.GetString()!.Replace('\\', '/')).Trim();
        if (name.Length is 0 or > 200 || name.Any(char.IsControl))
        {
            return WorkErrors.Invalid("fileName is 1..200 characters.");
        }

        var base64 = contentElement.GetString()!;
        if (base64.Length > MaxFileBytes * 4 / 3 + 4)
        {
            return WorkErrors.Of(WorkErrors.FileSize, "A file may be up to 5 MB.");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return WorkErrors.Invalid("content is not valid base64.");
        }

        if (bytes.Length == 0 || bytes.Length > MaxFileBytes)
        {
            return WorkErrors.Of(WorkErrors.FileSize, "A file holds 1 byte to 5 MB.");
        }

        if (!Types.TryGetValue(Path.GetExtension(name), out var type) || !MagicMatches(Path.GetExtension(name).ToLowerInvariant(), bytes))
        {
            return WorkErrors.Of(WorkErrors.FileType, "Accepted: pdf, png, jpg, tif, docx, xlsx, txt and csv, with matching content.");
        }

        return (name, type, bytes);
    }

    private static bool Starts(byte[] b, params byte[] prefix) => b.AsSpan().StartsWith(prefix);

    private static bool MagicMatches(string extension, byte[] b) => extension switch
    {
        ".pdf" => b.AsSpan().StartsWith("%PDF-"u8),
        ".png" => Starts(b, 0x89, 0x50, 0x4E, 0x47),
        ".jpg" or ".jpeg" => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        ".tif" or ".tiff" => Starts(b, 0x49, 0x49, 0x2A, 0x00) || Starts(b, 0x4D, 0x4D, 0x00, 0x2A),
        ".docx" or ".xlsx" => Starts(b, 0x50, 0x4B, 0x03, 0x04),
        _ => !b.AsSpan().Contains((byte)0),
    };

    private InboundDocumentRow NewRow(
        Guid envelopeId, string name, string type, byte[]? bytes, string channel, string sender, string envelope, string? suggested, string? link, string actor)
    {
        var now = clock.Now;
        var id = Guid.NewGuid();
        return new InboundDocumentRow
        {
            InboundDocumentId = id,
            LegalEntityId = LegalEntity,
            EnvelopeId = envelopeId,
            InboundNumber = $"INB-{now.UtcDate.Value:yyyyMMdd}-{id.ToString("N", CultureInfo.InvariantCulture)[^8..].ToUpperInvariant()}",
            FileName = name,
            ContentType = type,
            SizeBytes = bytes?.Length ?? 0,
            ContentHash = bytes is null ? string.Empty : Sha256Hash.Compute(bytes).Value,
            Channel = channel,
            Sender = sender,
            EnvelopeMetadata = envelope,
            SuggestedClass = suggested,
            LinkObject = link,
            State = InboundDocumentRow.Quarantined,
            CreatedBy = actor,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Scans and sets the state: only an explicit clean verdict makes the row CLEAN; infected bytes are not stored; an unavailable scanner leaves it quarantined.</summary>
    private async Task ApplyScanAsync(InboundDocumentRow row, byte[] bytes, IMalwareScanner scanner, IDocumentArchiveService archive, CancellationToken cancellationToken)
    {
        var outcome = await scanner.ScanAsync(bytes, cancellationToken).ConfigureAwait(false);
        row.Scanner = outcome.Scanner;
        row.ScannedAt = clock.Now;
        row.UpdatedAt = row.ScannedAt.Value;
        if (outcome.Verdict == ScanVerdict.Infected)
        {
            row.ScanVerdict = InboundDocumentRow.VerdictInfected;
            row.ScanSignature = outcome.Signature is { Length: > 200 } s ? s[..200] : outcome.Signature;
            row.State = InboundDocumentRow.Infected;
            row.ArchiveId = null;
            return;
        }

        if (row.ArchiveId is null)
        {
            var stored = await archive.StoreAsync(bytes, row.ContentType, JsonSerializer.Serialize(new { inbound = row.InboundNumber, source = "wrk.InboundDocument" }), cancellationToken)
                .ConfigureAwait(false);
            row.ArchiveId = stored.ArchiveId;
        }

        if (outcome.Verdict == ScanVerdict.Clean)
        {
            row.ScanVerdict = InboundDocumentRow.VerdictClean;
            row.State = InboundDocumentRow.Clean;
        }
        else
        {
            row.ScanVerdict = InboundDocumentRow.VerdictUnavailable;
            row.State = InboundDocumentRow.Quarantined;
        }
    }

    private IQueryable<InboundDocumentRow> Base()
    {
        var legalEntity = LegalEntity;
        return db.InboundDocuments.Where(d => d.LegalEntityId == legalEntity);
    }

    private Result<IQueryable<InboundDocumentRow>> Filter(string? state, string? objectRef)
    {
        var query = Base().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(state))
        {
            var s = state.Trim().ToUpperInvariant();
            if (!States.Contains(s))
            {
                return WorkErrors.Invalid("state is QUARANTINED, CLEAN, INFECTED, VERIFIED, REJECTED or SPLIT.");
            }

            query = query.Where(d => d.State == s);
        }

        if (!string.IsNullOrWhiteSpace(objectRef))
        {
            var link = ParseLink(objectRef);
            if (link.IsFailure)
            {
                return link.Error!;
            }

            var value = link.Value;
            query = query.Where(d => d.LinkObject == value);
        }

        return Result.Success(query);
    }

    private async Task<InboundDocumentRow?> FindAsync(Guid? id, CancellationToken cancellationToken, bool tracking = true)
    {
        if (id is null || id == Guid.Empty)
        {
            return null;
        }

        var query = Base().Where(d => d.InboundDocumentId == id);
        return await (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<InboundDocumentRow>> SaveAsync(InboundDocumentRow row, CancellationToken cancellationToken)
    {
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The document changed meanwhile.");
        }

        return row;
    }

    private async Task<DomainError?> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return WorkErrors.Of(WorkErrors.Stale, "The document changed meanwhile.");
        }
    }
}
