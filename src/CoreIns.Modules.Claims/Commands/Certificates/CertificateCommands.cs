using System.Globalization;
using System.Text.Json;
using CoreIns.Modules.Claims.Contracts.Api;
using CoreIns.Modules.Claims.Contracts.Events;
using CoreIns.Modules.Claims.Persistence;
using CoreIns.Modules.Claims.Queries;
using CoreIns.Modules.Documents.Contracts;
using CoreIns.Modules.Market.Contracts;
using CoreIns.Modules.Market.Contracts.Api;
using CoreIns.Platform.Audit;
using CoreIns.Platform.Commands;
using CoreIns.Platform.Context;
using CoreIns.Platform.Contracts.Events;
using CoreIns.Platform.Events;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Json;
using CoreIns.SharedKernel.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CoreIns.Modules.Claims.Commands.Certificates;

/// <summary>The look-back N of a claims-history certificate and how far it can be trusted.</summary>
/// <param name="Years">Whole years.</param>
/// <param name="LegalStatus">MKT legal status of the value, or <c>Unverified</c> for the illustrative default.</param>
internal sealed record CertificateLookback(int Years, string LegalStatus);

/// <summary>Views, the look-back resolution and the shared rules of the claims-history certificate (BO-CLM-07).</summary>
internal sealed class CertificateSupport(IMarketConfigurationService market, RequestContext context, IOptions<ClaimsOptions> options)
{
    public const string TemplateKey = "DT-CLAIMS-HISTORY";

    public static DomainError Requester(string message) => DomainError.Of(ModuleCode.CLM, "CERT-REQUESTER", message);

    public static DomainError State(string message) => DomainError.Of(ModuleCode.CLM, "CERT-STATE", message);

    /// <summary>True for the legal statuses Production accepts.</summary>
    public static bool IsConfirmed(string legalStatus) => legalStatus is "Settled" or "NotRegulatory";

    /// <summary>
    /// N from MKT key <c>clm.history.lookback_years</c>. The PRDs state no N, so a missing, malformed or out-of-range value falls back to the
    /// illustrative <see cref="CertificateOptions.DefaultLookbackYears"/> with legal status Unverified (fail closed: Production refuses to issue).
    /// </summary>
    public async Task<CertificateLookback> LookbackAsync(CancellationToken cancellationToken)
    {
        const string key = "clm.history.lookback_years";
        var fallback = new CertificateLookback(options.Value.Certificate.DefaultLookbackYears, "Unverified");
        var resolved = await market.ResolveAsync(
            new ConfigurationResolveRequest
            {
                LegalEntity = (context.LegalEntity ?? throw new InvalidOperationException("The request context has no legal entity.")).Value,
                Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
                Keys = [key],
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (resolved.Values.FirstOrDefault(v => v.Key == key) is not { } item)
        {
            return fallback;
        }

        var years = item.Value.ValueKind switch
        {
            JsonValueKind.Number when item.Value.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(item.Value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
        return years is < 1 or > 50 ? fallback : new CertificateLookback(years, item.LegalStatus.ToString());
    }

    public static CertificateView View(CertificateRequestRow row) => new()
    {
        CertificateId = row.CertificateId,
        CertificateNumber = row.CertificateNumber,
        RequesterPartyId = row.RequesterPartyId,
        PolicyNumbers = [.. row.PolicyNumbers.Select(PolicyNumber.Parse)],
        PeriodFrom = row.PeriodFrom,
        PeriodTo = row.PeriodTo,
        LookbackYears = row.LookbackYears,
        LegalStatus = row.LegalStatus,
        Status = row.Status switch
        {
            CertificateRequestRow.Issued => CertificateView.StatusValue.Issued,
            CertificateRequestRow.Assembled => CertificateView.StatusValue.Assembled,
            _ => CertificateView.StatusValue.Requested,
        },
        Claims = Lines(row),
        DocumentId = row.DocumentId is { } d ? new DocumentId(d) : null,
        IssuedAt = row.IssuedAt,
        RecordVersion = row.RecordVersion,
    };

    public static IReadOnlyList<CertificateClaimLine> Lines(CertificateRequestRow row) =>
        JsonSerializer.Deserialize<List<CertificateClaimLine>>(row.ClaimsJson, SharedKernelJson.Options) ?? [];

    public static async Task<CertificateRequestRow?> FindAsync(ClaimsDbContext db, LegalEntityId legalEntity, Guid certificateId, CancellationToken cancellationToken) =>
        await db.CertificateRequests.SingleOrDefaultAsync(c => c.LegalEntityId == legalEntity && c.CertificateId == certificateId, cancellationToken).ConfigureAwait(false);

    public static CommandAuditFacts Facts(CertificateView view, object? before, object after) => new()
    {
        ObjectRef = new ObjectRef(ModuleCode.CLM, "ClaimsHistoryCertificate", view.CertificateId.ToString("D")),
        ObjectNumber = view.CertificateNumber,
        BusinessKeys = BusinessKeys.Empty.With("certificateId", view.CertificateId.ToString("D")),
        Changes = AuditDiff.Compute(before, after),
    };
}

/// <summary><c>clm.Certificate.request</c>.</summary>
internal sealed record RequestCertificate(CertificateRequestRequest Request) : ICommand<CertificateRequestResponse>;

internal sealed class RequestCertificateValidator : AbstractValidator<RequestCertificate>
{
    public RequestCertificateValidator()
    {
        RuleFor(c => c.Request.PolicyNumbers).Must(p => p is null || p.Count <= 50).WithMessage("At most 50 policies per certificate.");
    }
}

/// <summary>
/// Opens a certificate request for a party (REQ-CLM-008): the requester is a party id, the period defaults to the look-back window ending
/// today and may not reach back further than N years. N is read once here and stored with its legal status.
/// TODO(hardening): confirm the requester exists and is the insured or an authorised representative (PTY), and start the 15-day CLM_HISTORY_STATEMENT clock (CMP).
/// </summary>
internal sealed class RequestCertificateHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IOptions<ClaimsOptions> options, Domain.ClaimProtection protection, CertificateSupport support)
    : ICommandHandler<RequestCertificate, CertificateRequestResponse>
{
    public async Task<Result<CertificateRequestResponse>> HandleAsync(RequestCertificate command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        if (request.RequesterPartyId.Value == Guid.Empty)
        {
            return CertificateSupport.Requester("The requester must be a party id.");
        }

        var lookback = await support.LookbackAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.Now;
        var today = options.Value.DateOf(now);
        var to = request.PeriodTo ?? today;
        var earliest = today.AddYears(-lookback.Years);
        var from = request.PeriodFrom ?? earliest;
        if (to > today || from > to)
        {
            return CertificateSupport.Requester("The period must end no later than today and not end before it starts.");
        }

        if (from < earliest)
        {
            return CertificateSupport.Requester($"The period reaches back further than the look-back of {lookback.Years} years.");
        }

        var row = new CertificateRequestRow
        {
            CertificateId = Guid.CreateVersion7(),
            LegalEntityId = protection.Current(context),
            Jurisdiction = (context.Jurisdiction ?? throw new InvalidOperationException("The request context has no jurisdiction.")).Value,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
            RequesterPartyId = request.RequesterPartyId,
            PolicyNumbers = [.. (request.PolicyNumbers ?? []).Select(p => p.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            PeriodFrom = from,
            PeriodTo = to,
            LookbackYears = lookback.Years,
            LegalStatus = lookback.LegalStatus,
            Status = CertificateRequestRow.Requested,
            UpdatedAt = now,
        };
        db.CertificateRequests.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CertificateRequestResponse { Certificate = CertificateSupport.View(row) };
    }
}

internal sealed class RequestCertificateAuditor : ICommandAuditor<RequestCertificate, CertificateRequestResponse>
{
    public CommandAuditFacts Describe(RequestCertificate command, Result<CertificateRequestResponse>? result) =>
        result is { IsSuccess: true } ok
            ? CertificateSupport.Facts(
                ok.Value.Certificate, null,
                new { requesterPartyId = command.Request.RequesterPartyId, periodFrom = ok.Value.Certificate.PeriodFrom, periodTo = ok.Value.Certificate.PeriodTo, lookbackYears = ok.Value.Certificate.LookbackYears, legalStatus = ok.Value.Certificate.LegalStatus })
            : new CommandAuditFacts();
}

/// <summary><c>clm.Certificate.assemble</c>.</summary>
internal sealed record AssembleCertificate(CertificateAssembleRequest Request) : ICommand<CertificateAssembleResponse>;

/// <summary>
/// Collects the claims of the requester for the period from the claim reader (matched by party id; draft, WITHDRAWN and DUPLICATE claims and
/// claims restricted for a data-subject request are left out) and stores them as the snapshot the certificate shows. Repeatable until issued.
/// </summary>
internal sealed class AssembleCertificateHandler(ClaimsDbContext db, RequestContext context, IClock clock, ClaimReader reader, Domain.ClaimProtection protection)
    : ICommandHandler<AssembleCertificate, CertificateAssembleResponse>
{
    public async Task<Result<CertificateAssembleResponse>> HandleAsync(AssembleCertificate command, CancellationToken cancellationToken)
    {
        var legalEntity = protection.Current(context);
        var row = await CertificateSupport.FindAsync(db, legalEntity, command.Request.CertificateId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ClaimSupport.NotFound("certificate");
        }

        if (row.Status == CertificateRequestRow.Issued)
        {
            return CertificateSupport.State("The certificate is already issued; its claims no longer change.");
        }

        var lines = await reader.CertificateClaimsAsync(legalEntity, row.RequesterPartyId, row.PeriodFrom, row.PeriodTo, row.PolicyNumbers, cancellationToken).ConfigureAwait(false);
        row.ClaimsJson = JsonSerializer.Serialize(lines, SharedKernelJson.Options);
        row.Status = CertificateRequestRow.Assembled;
        row.UpdatedAt = clock.Now;
        row.RecordVersion++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new CertificateAssembleResponse { Certificate = CertificateSupport.View(row) };
    }
}

internal sealed class AssembleCertificateAuditor : ICommandAuditor<AssembleCertificate, CertificateAssembleResponse>
{
    public CommandAuditFacts Describe(AssembleCertificate command, Result<CertificateAssembleResponse>? result) =>
        result is { IsSuccess: true } ok
            ? CertificateSupport.Facts(ok.Value.Certificate, new { status = "REQUESTED" }, new { status = "ASSEMBLED", claimCount = ok.Value.Certificate.Claims.Count })
            : new CommandAuditFacts();
}

/// <summary><c>clm.Certificate.issue</c>.</summary>
internal sealed record IssueCertificate(CertificateIssueRequest Request) : ICommand<CertificateIssueResponse>;

/// <summary>
/// Issues an assembled certificate exactly once: numbers it (PLT), renders <c>DT-CLAIMS-HISTORY</c> (DOC) and publishes
/// <c>ClaimsHistoryCertificateIssued</c> (ordering key certificate_id). A repeat returns the issued certificate and does nothing else.
/// Fail closed (D-REG-04): in Production a certificate whose look-back is not Settled in MKT is refused.
/// TODO(hardening): the document id is only known after the DOC call; a retry after a failed save requests the letter again (DOC de-duplicates on template + request number).
/// </summary>
internal sealed class IssueCertificateHandler(
    ClaimsDbContext db, RequestContext context, IClock clock, IEventPublisher events, INumberingService numbering, IHostEnvironment environment,
    IOptions<ClaimsOptions> options, IServiceProvider services, Domain.ClaimProtection protection)
    : ICommandHandler<IssueCertificate, CertificateIssueResponse>
{
    public async Task<Result<CertificateIssueResponse>> HandleAsync(IssueCertificate command, CancellationToken cancellationToken)
    {
        var row = await CertificateSupport.FindAsync(db, protection.Current(context), command.Request.CertificateId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return ClaimSupport.NotFound("certificate");
        }

        if (row.Status == CertificateRequestRow.Issued)
        {
            return new CertificateIssueResponse { Certificate = CertificateSupport.View(row) };
        }

        if (row.Status != CertificateRequestRow.Assembled)
        {
            return CertificateSupport.State("Assemble the certificate before issuing it.");
        }

        if (environment.IsProduction() && !CertificateSupport.IsConfirmed(row.LegalStatus))
        {
            return CertificateSupport.State(
                $"The look-back of the claims-history certificate is legally {row.LegalStatus}; issuing is refused in Production until MKT holds a Settled value for clm.history.lookback_years.");
        }

        var now = clock.Now;
        var today = options.Value.DateOf(now);
        var number = (await numbering.NextAsync(new NumberRequest(NumberingSchemes.ClaimsCertificate, today), cancellationToken).ConfigureAwait(false)).Value;
        var lines = CertificateSupport.Lines(row);
        Guid documentId;
        if (context.DryRun)
        {
            documentId = Guid.CreateVersion7();
        }
        else if (services.GetService<IDocumentRequestService>() is not { } documents)
        {
            return CertificateSupport.State("Document rendering is not available.");
        }
        else
        {
            var outcome = await documents.RequestAsync(
                new DocumentRequestSpec(
                    CertificateSupport.TemplateKey,
                    row.CertificateId.ToString("D"),
                    new ObjectRef(ModuleCode.CLM, "ClaimsHistoryCertificate", row.CertificateId.ToString("D")),
                    row.RequesterPartyId,
                    command.Request.Language == CertificateIssueRequest.LanguageValue.En ? "en" : "el",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["certificateNumber"] = number,
                        ["periodFrom"] = row.PeriodFrom.ToString(),
                        ["periodTo"] = row.PeriodTo.ToString(),
                        ["issuedOn"] = today.ToString(),
                        ["claimCount"] = lines.Count.ToString(CultureInfo.InvariantCulture),
                        ["claimsSummary"] = lines.Count == 0
                            ? "-"
                            : string.Join("; ", lines.Select(l => $"{l.ClaimNumber} ({l.PolicyNumber}, {l.LossDate}, {l.LossCause})")),
                    }),
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(outcome.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                return CertificateSupport.State("The certificate document could not be rendered; nothing was issued.");
            }

            documentId = outcome.DocumentId.Value;
        }

        row.CertificateNumber = number;
        row.DocumentId = documentId;
        row.IssuedAt = now;
        row.UpdatedAt = now;
        row.Status = CertificateRequestRow.Issued;
        row.RecordVersion++;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return CertificateSupport.State("The certificate changed meanwhile; reload it.");
        }

        events.Publish(new OutgoingEvent(
            EventDescriptor.From(ClaimsHistoryCertificateIssuedV1.Descriptor), "ClaimsHistoryCertificate", row.CertificateId.ToString("D"),
            new ClaimsHistoryCertificateIssuedV1
            {
                CertificateId = row.CertificateId,
                CertificateNumber = number,
                RequesterPartyId = row.RequesterPartyId,
                Period = new DateRange(row.PeriodFrom, row.PeriodTo.AddDays(1)),
                DocumentId = new DocumentId(documentId),
            },
            BusinessKeys.Empty.With("certificateId", row.CertificateId.ToString("D")).With("documentId", documentId.ToString("D")))
        {
            OccurredAt = now,
        });
        return new CertificateIssueResponse { Certificate = CertificateSupport.View(row) };
    }
}

internal sealed class IssueCertificateAuditor : ICommandAuditor<IssueCertificate, CertificateIssueResponse>
{
    public CommandAuditFacts Describe(IssueCertificate command, Result<CertificateIssueResponse>? result) =>
        result is { IsSuccess: true } ok
            ? CertificateSupport.Facts(ok.Value.Certificate, new { status = "ASSEMBLED" }, new { status = "ISSUED", certificateNumber = ok.Value.Certificate.CertificateNumber })
            : new CommandAuditFacts();
}
