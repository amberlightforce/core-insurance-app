using CoreIns.Modules.Market.Contracts.Spi;

namespace CoreIns.IntegrationTests.Finance.Servicing;

/// <summary>
/// A <c>TaxCalculator.treatment</c> double with the Greece rows of REQ-MKT-331 (all PendingOpinion, D-SL3-05): IPT on
/// cancellation by the policyholder, endorsement credit, return premium and refund is KEEP_NOT_REDUCED; new business,
/// endorsement debit and fee are APPLY; a distance withdrawal void is REVERSE_AS_VOID. Levy and stamp categories and any
/// other cancellation source have no row, so they raise RULE_MISSING exactly as MKT does (no core default, D-SL3-06).
/// The rows are test data of this double, not regulatory values.
/// </summary>
internal sealed class FakeTaxCalculator : ITaxCalculator
{
    /// <summary>When set, every call throws it (a calculator that is down or refuses).</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, a Business policyholder gets APPLY: a treatment that depends on a field BIL does not carry (D8).</summary>
    public bool DependsOnPolicyholderType { get; set; }

    /// <summary>Requests received, for assertions on what FIN asks.</summary>
    public List<TaxTreatmentRequest> Requests { get; } = [];

    public ValueTask<TaxCalculationResult> CalculateAsync(TaxCalculationRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<TaxTreatmentResult> TreatmentAsync(TaxTreatmentRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        (TreatmentAction Action, string Rule)? row = request.Category == TaxCategory.Tax ? (request.TransactionKind, request.CancellationSource) switch
        {
            (TaxTransactionKind.Cancellation, CancellationSources.Policyholder) => (TreatmentAction.KeepNotReduced, "GR-TRT-IPT-CANCEL-POLICYHOLDER"),
            (TaxTransactionKind.EndorsementCredit, _) => (TreatmentAction.KeepNotReduced, "GR-TRT-IPT-ENDORSEMENT-CREDIT"),
            (TaxTransactionKind.ReturnPremium, _) => (TreatmentAction.KeepNotReduced, "GR-TRT-IPT-RETURN-PREMIUM"),
            (TaxTransactionKind.Refund, _) => (TreatmentAction.KeepNotReduced, "GR-TRT-IPT-REFUND"),
            (TaxTransactionKind.NewBusiness, _) => (TreatmentAction.Apply, "GR-TRT-IPT-NEW-BUSINESS"),
            (TaxTransactionKind.EndorsementDebit, _) => (TreatmentAction.Apply, "GR-TRT-IPT-ENDORSEMENT-DEBIT"),
            (TaxTransactionKind.Fee, _) => (TreatmentAction.Apply, "GR-TRT-IPT-FEE"),
            (TaxTransactionKind.DistanceWithdrawalVoid, _) => (TreatmentAction.ReverseAsVoid, "GR-TRT-IPT-WITHDRAWAL-VOID"),
            _ => null,
        }
        : null;
        if (row is not { } found)
        {
            throw new SpiException(
                new SpiError(SpiErrorCategory.RuleMissing, "RULE_MISSING"),
                $"No treatment rule for {request.Category} {request.TransactionKind} {request.CancellationSource ?? "ANY"}.");
        }

        if (DependsOnPolicyholderType && request.PolicyholderType == PolicyholderType.Business)
        {
            found = (TreatmentAction.Apply, "GR-TRT-IPT-BUSINESS");
        }

        var reduces = found.Action == TreatmentAction.ReverseAsVoid;
        return ValueTask.FromResult(new TaxTreatmentResult
        {
            ChargeType = request.ChargeType,
            Action = found.Action,
            CustomerCredit = reduces ? CustomerCredit.Full : CustomerCredit.None,
            AuthorityLiability = reduces ? AuthorityLiability.Reduce : AuthorityLiability.NotReduce,
            FiscalDocument = reduces ? FiscalDocumentTreatment.CreditNote : FiscalDocumentTreatment.None,
            RuleId = found.Rule,
            RuleVersion = "0.1.0",
            LegalStatus = TreatmentLegalStatus.Pending,
            LegalSourceRef = "test double of PRD-17 REQ-MKT-331",
        });
    }
}
