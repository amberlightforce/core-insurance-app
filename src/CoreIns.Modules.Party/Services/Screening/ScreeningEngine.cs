using CoreIns.Modules.Party.Domain;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Persistence;
using CoreIns.Platform.Context;
using CoreIns.Platform.Errors;
using CoreIns.Platform.Numbering;
using CoreIns.Platform.Time;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CoreIns.Modules.Party.Services.Screening;

/// <summary>Status codes stored in <c>pty.screening_case</c>.</summary>
internal static class ScreeningStatus
{
    public const string Open = "OPEN";
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Cleared = "CLEARED";
    public const string TrueMatch = "TRUE_MATCH";

    public static bool IsOpen(string status) => status is Open or PendingApproval;
}

/// <summary>
/// The screening rules of BO-PTY-07, shared by <c>pty.Screening.screen</c> and <c>pty.Screening.bulk</c>. A subject with a TRUE_MATCH case is
/// Blocked for good; a subject with an open case stays a PotentialHit (and keeps its payment block) until the case is decided; a CLEARED case lets
/// the same matches pass later (a different match, for example after a list update, opens a new case). A hit opens a case, numbers it through PLT
/// and raises the WRK review task. Without a registered <see cref="IScreeningProvider"/> nothing is screened and the call fails closed.
/// TODO(hardening): a cleared case never expires; re-screen on a list version change and expire a clearance after a configured period.
/// TODO(hardening): identifiers (AFM, passport) of a party are not sent to the provider yet, only names and birth date; an unknown party id is
/// screened with no names (Clear on the demo provider), a real adapter must refuse it.
/// </summary>
internal sealed class ScreeningEngine(
    PartyDbContext db, RequestContext context, IClock clock, PartyProtection protection, INumberingService numbering, IServiceProvider services, ScreeningWorkTasks work)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<ScreeningScreenResponse>> ScreenAsync(ScreeningScreenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ScreenPartyValidator.InputProblem(request) is { } problem)
        {
            return problem;
        }

        if (services.GetService<IScreeningProvider>() is not { } provider)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-AVAILABLE", "Sanctions screening is not connected in this environment; the subject cannot be screened.");
        }

        var legalEntity = protection.Current(context);
        var (subject, key, name, country) = await ResolveAsync(legalEntity, request, cancellationToken).ConfigureAwait(false);
        var prior = await db.ScreeningCases.AsNoTracking()
            .Where(c => c.LegalEntityId == legalEntity && c.SubjectKey == key)
            .OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (prior.FirstOrDefault(c => c.Status == ScreeningStatus.TrueMatch) is { } trueMatch)
        {
            return Existing(trueMatch, ScreeningScreenResponse.ResultValue.Blocked);
        }

        if (prior.FirstOrDefault(c => ScreeningStatus.IsOpen(c.Status)) is { } open)
        {
            return Existing(open, ScreeningScreenResponse.ResultValue.PotentialHit);
        }

        var result = await provider.ScreenAsync(subject, cancellationToken).ConfigureAwait(false);
        if (result.Matches.Count == 0)
        {
            return Clear(result.ListVersions);
        }

        var fingerprint = Fingerprint(result.Matches);
        if (prior.Any(c => c.Status == ScreeningStatus.Cleared && c.MatchFingerprint == fingerprint))
        {
            return Clear(result.ListVersions);
        }

        var now = clock.Now;
        var today = new BusinessDate(DateOnly.FromDateTime(now.ToUtcDateTime()));
        var number = await numbering.NextAsync(new NumberRequest(NumberingSchemes.ScreeningCase, today), cancellationToken).ConfigureAwait(false);
        var row = new ScreeningCaseRow
        {
            CaseId = EntityIds.NewGuid(),
            LegalEntityId = legalEntity,
            CaseNumber = number.Value,
            PartyId = request.PartyId,
            SubjectKey = key,
            SubjectName = name,
            Country = country,
            Status = ScreeningStatus.Open,
            MatchFingerprint = fingerprint,
            Evidence = JsonSerializer.Serialize(
                result.Matches.Select(m => new ScreeningCaseView.EvidenceItem { ListName = m.ListName, MatchedName = m.MatchedName, Score = m.Score, Reason = m.Reason }), Json),
            ListVersions = string.Join(',', result.ListVersions),
            CallerModule = request.CallerRef.Module.ToString(),
            CallerType = request.CallerRef.Type,
            CallerId = request.CallerRef.Id,
            CreatedAt = now,
            CreatedBy = context.Actor.ToString(),
        };
        db.ScreeningCases.Add(row);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await work.RaiseAsync(row.CaseId, row.CaseNumber, cancellationToken).ConfigureAwait(false);
        return Existing(row, ScreeningScreenResponse.ResultValue.PotentialHit);
    }

    private static ScreeningScreenResponse Clear(IReadOnlyList<string> listVersions) =>
        new() { Result = ScreeningScreenResponse.ResultValue.Clear, ListVersions = listVersions, PaymentBlock = false };

    private static ScreeningScreenResponse Existing(ScreeningCaseRow row, ScreeningScreenResponse.ResultValue result) => new()
    {
        Result = result,
        CaseNumber = row.CaseNumber,
        ListVersions = row.ListVersions.Length == 0 ? [] : row.ListVersions.Split(','),
        PaymentBlock = true,
    };

    /// <summary>Hash of what matched (list and listed name), order-independent.</summary>
    private static string Fingerprint(IReadOnlyList<ScreeningMatch> matches) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', matches.Select(m => $"{m.ListName}|{m.MatchedName}").Order(StringComparer.Ordinal)))));

    private async Task<(ScreeningSubject Subject, string Key, string? Name, string? Country)> ResolveAsync(
        LegalEntityId legalEntity, ScreeningScreenRequest request, CancellationToken cancellationToken)
    {
        if (request.AdHocPayee is { } payee)
        {
            var identifiers = payee.Identifiers?.Select(i => (i.Scheme, i.Value)).ToList() ?? [];
            var text = string.Join('|', payee.Name.Trim().ToUpperInvariant(), payee.BirthDate?.ToString() ?? string.Empty, payee.Country ?? string.Empty);
            var adHocKey = "A:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
            return (new ScreeningSubject([payee.Name.Trim()], payee.BirthDate, payee.Country, payee.Nationality, identifiers), adHocKey, payee.Name.Trim(), payee.Country);
        }

        var partyId = request.PartyId!.Value;
        var names = await db.Names.AsNoTracking().Where(n => n.LegalEntityId == legalEntity && n.PartyId == partyId && n.RecordedTo == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        List<string> forms = [];
        foreach (var n in names)
        {
            var person = string.Join(' ', new[] { n.GivenNames, n.FamilyName }.Where(p => !string.IsNullOrWhiteSpace(p)));
            forms.AddRange(new[] { person, n.OrganisationName, n.TradeName }.Where(p => !string.IsNullOrWhiteSpace(p))!);
        }

        forms = [.. forms.Distinct(StringComparer.OrdinalIgnoreCase)];
        var envelope = await db.Parties.AsNoTracking().Where(p => p.LegalEntityId == legalEntity && p.PartyId == partyId).Select(p => p.BirthDateEncrypted)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        BusinessDate? birthDate = envelope is null ? null : await protection.DecryptBirthDateAsync(legalEntity, partyId.Value, envelope, cancellationToken).ConfigureAwait(false);
        return (new ScreeningSubject(forms, birthDate, null, null, []), "P:" + partyId.Value.ToString("N"), forms.FirstOrDefault(), null);
    }
}
