using CoreIns.Modules.Party.Commands;
using CoreIns.Modules.Party.Contracts.Api;
using CoreIns.Modules.Party.Domain;
using CoreIns.Modules.Party.Persistence;
using CoreIns.SharedKernel;
using CoreIns.SharedKernel.Identifiers;
using CoreIns.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Party.Queries;

/// <summary>
/// <c>pty.CommunicationPreference.resolve</c> (BO-PTY-06): the effective channel of a party for a purpose on a date. Candidates in order: the
/// preference of the purpose, the DEFAULT preference, EMAIL (only when the party has an email contact point), POST, then (for a purpose that
/// needs consent) every channel with a granted consent. A channel is skipped when its newest consent record is a withdrawal or an objection,
/// or when the purpose needs consent and none is granted. A vulnerable party gets the PAPER medium and no marketing, profiling or sharing.
/// TODO(hardening): check the contact point of the chosen channel is verified and not expired; consent validity dates and re-consent periods.
/// </summary>
internal sealed class CommunicationResolver(PartyDbContext db)
{
    public async Task<Result<CommunicationPreferenceResolveResponse>> ResolveAsync(
        LegalEntityId legalEntity, PartyId partyId, string purpose, BusinessDate validAt, CancellationToken cancellationToken)
    {
        var party = await db.Parties.AsNoTracking().SingleOrDefaultAsync(p => p.LegalEntityId == legalEntity && p.PartyId == partyId, cancellationToken)
            .ConfigureAwait(false);
        if (party is null)
        {
            return DomainError.Of(ModuleCode.PTY, "NOT-FOUND", "The party does not exist.");
        }

        var needsConsent = ConsentCatalogue.ConsentRequired.Contains(purpose);
        var vulnerable = await db.Vulnerabilities.AsNoTracking().AnyAsync(v => v.PartyId == partyId && v.LegalEntityId == legalEntity && v.Flag, cancellationToken)
            .ConfigureAwait(false);
        if (vulnerable && needsConsent)
        {
            return new DomainError(ErrorCode.For(ModuleCode.PTY, "NO-CONSENT"), "The party is flagged vulnerable; this purpose is not allowed.");
        }

        var consents = ConsentSupport.Current(await db.Consents.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.PartyId == partyId && r.Purpose == purpose).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => r.Channel, StringComparer.Ordinal);

        var prefs = await db.CommunicationPreferences.AsNoTracking()
            .Where(r => r.LegalEntityId == legalEntity && r.PartyId == partyId && r.RecordedTo == null && (r.Purpose == purpose || r.Purpose == ConsentCatalogue.Default))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        prefs = [.. prefs.Where(r => new DateRange(r.ValidFrom, r.ValidTo).Contains(validAt))];

        var candidates = new List<(string Channel, string Language, string Medium, string Proof)>();
        foreach (var (name, tag) in new[] { (purpose, "PREFERENCE"), (ConsentCatalogue.Default, "DEFAULT-PREFERENCE") })
        {
            if (prefs.FirstOrDefault(r => r.Purpose == name) is { } p)
            {
                candidates.Add((p.Channel, p.Language, p.Medium, $"{tag}:{p.PreferenceId:D}"));
            }
        }

        var hasEmail = await db.ContactPoints.AsNoTracking()
            .AnyAsync(c => c.PartyId == partyId && c.LegalEntityId == legalEntity && c.Type == "EMAIL" && c.RecordedTo == null && c.ValidTo == null, cancellationToken)
            .ConfigureAwait(false);
        if (hasEmail)
        {
            candidates.Add(("EMAIL", party.PreferredLanguage, "DIGITAL", "FALLBACK:EMAIL"));
        }

        candidates.Add(("POST", party.PreferredLanguage, "PAPER", "FALLBACK:POST"));
        if (needsConsent)
        {
            candidates.AddRange(consents.Values.Where(c => c.State == ConsentSupport.Granted)
                .Select(c => (c.Channel, party.PreferredLanguage, ConsentCatalogue.DefaultMedium(c.Channel), "CONSENT-CHANNEL")));
        }

        foreach (var (channel, language, medium, proof) in candidates)
        {
            consents.TryGetValue(channel, out var consent);
            var allowed = needsConsent ? consent is { State: ConsentSupport.Granted } : consent is null or { State: ConsentSupport.Granted };
            if (!allowed)
            {
                continue;
            }

            return new CommunicationPreferenceResolveResponse
            {
                Channel = channel, Language = language, Medium = vulnerable ? "PAPER" : medium,
                Proof = (consent is null ? proof : $"{proof};CONSENT:{consent.ConsentId:D}") + (vulnerable ? ";VULNERABLE" : string.Empty),
            };
        }

        return new DomainError(ErrorCode.For(ModuleCode.PTY, "NO-CONSENT"), "No channel is available: the consent for this purpose is missing or withdrawn.");
    }
}
