using System.Globalization;
using CoreIns.Modules.Billing.Contracts.Api;
using CoreIns.Modules.Billing.Domain;
using CoreIns.Modules.Billing.Persistence;
using CoreIns.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Billing.Queries;

/// <summary>Read side of payer instruments and SEPA mandates (BO-BIL-06a): masked IBAN only, never the IBAN.</summary>
internal sealed class MandateReader(BillingDbContext db)
{
    public static PaymentInstrumentView View(PaymentInstrumentRow row) => new()
    {
        InstrumentId = row.InstrumentId,
        PartyId = row.PartyId,
        Kind = PaymentInstrumentView.KindValue.BankAccount,
        MaskedIban = IbanMask.Mask(row.IbanLast4),
        Holder = row.HolderName,
        Status = Codes.Api<PaymentInstrumentView.StatusValue>(Codes.Parse<InstrumentStatus>(row.Status)),
        CreatedAt = row.CreatedAt.ToUtcDateTime().ToString("O", CultureInfo.InvariantCulture),
    };

    /// <summary>The verification outcome of a stored code as the contract enum of a response.</summary>
    public static TApi? Vop<TApi>(string? code)
        where TApi : struct, Enum =>
        code is null ? null : Codes.Api<TApi>(Codes.Parse<VopOutcome>(code));

    /// <summary>The view of a mandate row; a native mandate shows the masked IBAN of its instrument, a migrated one the masked IBAN it was imported with.</summary>
    public async Task<MandateView> ViewAsync(MandateRow row, CancellationToken cancellationToken)
    {
        var masked = row.IbanMasked;
        if (row.InstrumentId is { } instrument)
        {
            var last4 = await db.PaymentInstruments.AsNoTracking().Where(i => i.InstrumentId == instrument).Select(i => i.IbanLast4).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            masked = last4 is null ? null : IbanMask.Mask(last4);
        }

        return new MandateView
        {
            MandateId = row.MandateId,
            BillingAccountId = row.BillingAccountId,
            PayerPartyId = row.PayerPartyId,
            InstrumentId = row.InstrumentId,
            MandateReference = row.MandateReference,
            Scheme = MandateView.SchemeValue.SepaCore,
            SequenceType = Codes.Api<MandateView.SequenceTypeValue>(Codes.Parse<MandateSequence>(row.SequenceType)),
            Status = Codes.Api<MandateView.StatusValue>(Codes.Parse<MandateStatus>(row.Status)),
            MaskedIban = masked,
            SignatureDate = row.SignatureDate,
            SignatureEvidenceId = row.SignatureEvidenceId,
            AmendmentCount = row.AmendmentCount,
            Origin = row.Origin == "MIGRATION" ? MandateView.OriginValue.Migration : MandateView.OriginValue.Native,
        };
    }

    /// <summary><c>bil.Mandate.get</c>.</summary>
    public async Task<MandateView?> GetAsync(LegalEntityId legalEntity, Guid mandateId, CancellationToken cancellationToken)
    {
        var row = await db.Mandates.AsNoTracking().SingleOrDefaultAsync(m => m.MandateId == mandateId && m.LegalEntityId == legalEntity, cancellationToken).ConfigureAwait(false);
        return row is null ? null : await ViewAsync(row, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>bil.PaymentInstrument.list</c>: the instruments of a party (active and removed), oldest first; the UUIDv7 id is the cursor.</summary>
    public async Task<PaymentInstrumentListPage> ListInstrumentsAsync(LegalEntityId legalEntity, PartyId party, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? 50, 1, 200);
        var rows = (await db.PaymentInstruments.AsNoTracking().Where(i => i.LegalEntityId == legalEntity && i.PartyId == party).ToListAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(i => i.InstrumentId).ToList();
        if (Guid.TryParse(cursor, out var after))
        {
            rows = [.. rows.Where(i => i.InstrumentId.CompareTo(after) > 0)];
        }

        var window = rows.Take(size + 1).ToList();
        var page = window.Take(size).ToList();
        return new PaymentInstrumentListPage
        {
            Items = [.. page.Select(r => new PaymentInstrumentListItem { Instrument = View(r), VopResult = Vop<PaymentInstrumentListItem.VopResultValue>(r.VopResult) })],
            NextCursor = window.Count > size ? page[^1].InstrumentId.ToString("D") : null,
            Limit = size,
        };
    }
}
