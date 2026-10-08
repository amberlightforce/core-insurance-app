using CoreIns.Modules.Claims.Domain;
using Microsoft.EntityFrameworkCore;

namespace CoreIns.Modules.Claims.Persistence;

/// <summary>EF model of the claim financial engine (SL2-CLM-MONEY): reserve lines, transaction sets, transactions, payments, payee account view.</summary>
internal static class ClaimsFinancialModel
{
    private const string MoneyType = "numeric(19,4)";

    public static void Map(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReserveLineRow>(entity =>
        {
            entity.ToTable("reserve_line", table =>
            {
                ClaimsDbContext.CommonChecks(table, "reserve_line");
                table.HasCheckConstraint("ck_reserve_line_cost_type", Codes.CheckSql<CostType>("cost_type"));
                table.HasCheckConstraint("ck_reserve_line_currency", "currency ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(e => e.ReserveLineId).HasName("pk_reserve_line");
            entity.Property(e => e.ReserveLineId).HasColumnName("reserve_line_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            entity.Property(e => e.CostType).HasColumnName("cost_type");
            entity.Property(e => e.CostCategory).HasColumnName("cost_category");
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.FinalFlag).HasColumnName("final_flag");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.ExposureId, e.CostType, e.CostCategory, e.Currency }).IsUnique().HasDatabaseName("ux_reserve_line_key");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_reserve_line_claim");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_reserve_line_claim").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExposureRow>().WithMany().HasForeignKey(e => e.ExposureId).HasConstraintName("fk_reserve_line_exposure").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TransactionSetRow>(entity =>
        {
            entity.ToTable("transaction_set", table =>
            {
                ClaimsDbContext.CommonChecks(table, "transaction_set");
                table.HasCheckConstraint("ck_transaction_set_status", Codes.CheckSql<SetStatus>("status"));
                table.HasCheckConstraint("ck_transaction_set_approved_shape", "status NOT IN ('APPROVED', 'POSTED') OR approved_at IS NOT NULL");
                table.HasCheckConstraint("ck_transaction_set_rejected_shape", "status <> 'REJECTED' OR rejection_reason IS NOT NULL");
                table.HasCheckConstraint("ck_transaction_set_pending_shape", "status <> 'PENDING_APPROVAL' OR (approval_request_id IS NOT NULL AND approval_payload_hash IS NOT NULL)");
            });
            entity.HasKey(e => e.SetId).HasName("pk_transaction_set");
            entity.Property(e => e.SetId).HasColumnName("set_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.ContentHash).HasColumnName("content_hash").HasColumnType("char(64)");
            entity.Property(e => e.BasisHash).HasColumnName("basis_hash").HasColumnType("char(64)");
            entity.Property(e => e.Submitter).HasColumnName("submitter");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            entity.Property(e => e.ApprovalType).HasColumnName("approval_type");
            entity.Property(e => e.ApprovalSubjectType).HasColumnName("approval_subject_type");
            entity.Property(e => e.ApprovalSubjectId).HasColumnName("approval_subject_id");
            entity.Property(e => e.ApprovalPayloadHash).HasColumnName("approval_payload_hash").HasColumnType("char(64)");
            entity.Property(e => e.ApprovalAuthorityType).HasColumnName("approval_authority_type");
            entity.Property(e => e.ApprovalAuthorityAmount).HasColumnName("approval_authority_amount").HasColumnType(MoneyType);
            entity.Property(e => e.ApprovalAuthorityCostType).HasColumnName("approval_authority_cost_type");
            entity.Property(e => e.ReferralRole).HasColumnName("referral_role");
            entity.Property(e => e.AuthorityCheckIds).HasColumnName("authority_check_ids");
            entity.Property(e => e.FourEyes).HasColumnName("four_eyes");
            entity.Property(e => e.Approver).HasColumnName("approver");
            entity.Property(e => e.ApproverUserId).HasColumnName("approver_user_id");
            entity.Property(e => e.RejectionReason).HasColumnName("rejection_reason");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamptz");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.Property(e => e.CreatedTxid).HasColumnName("created_txid").HasDefaultValueSql("txid_current()").ValueGeneratedOnAdd();
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_transaction_set_claim");
            entity.HasIndex(e => e.ApprovalRequestId).IsUnique().HasFilter("approval_request_id IS NOT NULL").HasDatabaseName("ux_transaction_set_approval_request");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_transaction_set_claim").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancialTransactionRow>(entity =>
        {
            entity.ToTable("financial_transaction", table =>
            {
                ClaimsDbContext.CommonChecks(table, "financial_transaction");
                table.HasCheckConstraint("ck_financial_transaction_kind", Codes.CheckSql<TransactionKind>("kind"));
                table.HasCheckConstraint("ck_financial_transaction_amount", "amount <> 0");
                table.HasCheckConstraint("ck_financial_transaction_payment_shape",
                    "kind <> 'PAYMENT' OR (amount > 0 AND eroding IS NOT NULL AND payment_type IS NOT NULL AND claim_payment_id IS NOT NULL)");
                table.HasCheckConstraint("ck_financial_transaction_reserve_shape", "kind <> 'RESERVE' OR (eroding IS NULL AND payment_type IS NULL AND claim_payment_id IS NULL)");

                // D-SL2-06: one currency in the slice; the three-currency columns exist and are filled.
                table.HasCheckConstraint("ck_financial_transaction_currencies", "currency ~ '^[A-Z]{3}$' AND functional_currency ~ '^[A-Z]{3}$' AND group_currency ~ '^[A-Z]{3}$'");
                table.HasCheckConstraint("ck_financial_transaction_sequence", "sequence >= 1");
            });
            entity.HasKey(e => e.TxnId).HasName("pk_financial_transaction");
            entity.Property(e => e.TxnId).HasColumnName("txn_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.ReserveLineId).HasColumnName("reserve_line_id");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            entity.Property(e => e.Sequence).HasColumnName("sequence");
            entity.Property(e => e.TxnNumber).HasColumnName("txn_number");
            entity.Property(e => e.Kind).HasColumnName("kind");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType(MoneyType);
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.FunctionalAmount).HasColumnName("functional_amount").HasColumnType(MoneyType);
            entity.Property(e => e.FunctionalCurrency).HasColumnName("functional_currency").HasColumnType("char(3)");
            entity.Property(e => e.GroupAmount).HasColumnName("group_amount").HasColumnType(MoneyType);
            entity.Property(e => e.GroupCurrency).HasColumnName("group_currency").HasColumnType("char(3)");
            entity.Property(e => e.FxRateId).HasColumnName("fx_rate_id");
            entity.Property(e => e.Eroding).HasColumnName("eroding");
            entity.Property(e => e.PaymentType).HasColumnName("payment_type");
            entity.Property(e => e.ClaimPaymentId).HasColumnName("claim_payment_id");
            entity.Property(e => e.ReversesTxnId).HasColumnName("reverses_txn_id");
            entity.Property(e => e.ReasonCode).HasColumnName("reason_code");
            entity.Property(e => e.Proposed).HasColumnName("proposed");
            entity.Property(e => e.TransactionDate).HasColumnName("transaction_date");
            entity.HasIndex(e => new { e.ClaimId, e.Sequence }).IsUnique().HasDatabaseName("ux_financial_transaction_claim_sequence");
            entity.HasIndex(e => e.SetId).HasDatabaseName("ix_financial_transaction_set");
            entity.HasIndex(e => e.ReserveLineId).HasDatabaseName("ix_financial_transaction_line");
            entity.HasOne<TransactionSetRow>().WithMany().HasForeignKey(e => e.SetId).HasConstraintName("fk_financial_transaction_set").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReserveLineRow>().WithMany().HasForeignKey(e => e.ReserveLineId).HasConstraintName("fk_financial_transaction_line").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FinancialTransactionRow>().WithMany().HasForeignKey(e => e.ReversesTxnId).HasConstraintName("fk_financial_transaction_reverses").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClaimPaymentRow>(entity =>
        {
            entity.ToTable("claim_payment", table =>
            {
                ClaimsDbContext.CommonChecks(table, "claim_payment");
                table.HasCheckConstraint("ck_claim_payment_status", Codes.CheckSql<PaymentStatus>("status"));
                table.HasCheckConstraint("ck_claim_payment_type", Codes.CheckSql<PaymentType>("payment_type"));
                table.HasCheckConstraint("ck_claim_payment_amount", "amount > 0");
                table.HasCheckConstraint("ck_claim_payment_hold_shape", "status <> 'ON_HOLD' OR hold_reason IS NOT NULL");
                table.HasCheckConstraint("ck_claim_payment_submitted_shape", "status NOT IN ('SUBMITTED', 'ISSUED', 'CLEARED') OR disbursement_id IS NOT NULL");
            });
            entity.HasKey(e => e.ClaimPaymentId).HasName("pk_claim_payment");
            entity.Property(e => e.ClaimPaymentId).HasColumnName("claim_payment_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.ExposureId).HasColumnName("exposure_id");
            entity.Property(e => e.PayeePartyId).HasColumnName("payee_party_id");
            entity.Property(e => e.PayeeAccountId).HasColumnName("payee_account_id");
            entity.Property(e => e.MaskedAccount).HasColumnName("masked_account");
            entity.Property(e => e.Method).HasColumnName("method");
            entity.Property(e => e.PaymentType).HasColumnName("payment_type");
            entity.Property(e => e.Amount).HasColumnName("amount").HasColumnType(MoneyType);
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.HoldReason).HasColumnName("hold_reason");
            entity.Property(e => e.DisbursementId).HasColumnName("disbursement_id");
            entity.Property(e => e.DisbursementContentHash).HasColumnName("disbursement_content_hash").HasColumnType("char(64)");
            entity.Property(e => e.ApprovalEvidenceRef).HasColumnName("approval_evidence_ref");
            entity.Property(e => e.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamptz");
            entity.Property(e => e.IssuedAt).HasColumnName("issued_at").HasColumnType("timestamptz");
            entity.Property(e => e.ClearedAt).HasColumnName("cleared_at").HasColumnType("timestamptz");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasIndex(e => e.ClaimId).HasDatabaseName("ix_claim_payment_claim");
            entity.HasIndex(e => e.SetId).HasDatabaseName("ix_claim_payment_set");
            entity.HasIndex(e => e.DisbursementId).IsUnique().HasFilter("disbursement_id IS NOT NULL").HasDatabaseName("ux_claim_payment_disbursement");
            entity.HasOne<TransactionSetRow>().WithMany().HasForeignKey(e => e.SetId).HasConstraintName("fk_claim_payment_set").OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExposureRow>().WithMany().HasForeignKey(e => e.ExposureId).HasConstraintName("fk_claim_payment_exposure").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SetApprovalRow>(entity =>
        {
            entity.ToTable("set_approval", table =>
            {
                ClaimsDbContext.CommonChecks(table, "set_approval");
                table.HasCheckConstraint("ck_set_approval_status", "status IN ('PENDING', 'APPROVED', 'REJECTED')");
                table.HasCheckConstraint("ck_set_approval_amount", "authority_amount > 0");
            });
            entity.HasKey(e => e.ApprovalRequestId).HasName("pk_set_approval");
            entity.Property(e => e.ApprovalRequestId).HasColumnName("approval_request_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.SetId).HasColumnName("set_id");
            entity.Property(e => e.ApprovalType).HasColumnName("approval_type");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type");
            entity.Property(e => e.SubjectId).HasColumnName("subject_id");
            entity.Property(e => e.PayloadHash).HasColumnName("payload_hash").HasColumnType("char(64)");
            entity.Property(e => e.AuthorityType).HasColumnName("authority_type");
            entity.Property(e => e.AuthorityCostType).HasColumnName("authority_cost_type");
            entity.Property(e => e.AuthorityAmount).HasColumnName("authority_amount").HasColumnType(MoneyType);
            entity.Property(e => e.Currency).HasColumnName("currency").HasColumnType("char(3)");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Checker).HasColumnName("checker");
            entity.Property(e => e.CheckerUserId).HasColumnName("checker_user_id");
            entity.Property(e => e.DecidedAt).HasColumnName("decided_at").HasColumnType("timestamptz");
            entity.HasIndex(e => new { e.SetId, e.AuthorityType, e.AuthorityCostType }).IsUnique().HasDatabaseName("ux_set_approval_bucket");
            entity.HasOne<TransactionSetRow>().WithMany().HasForeignKey(e => e.SetId).HasConstraintName("fk_set_approval_set").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PayeeAccountViewRow>(entity =>
        {
            entity.ToTable("payee_account_view", table => ClaimsDbContext.CommonChecks(table, "payee_account_view"));
            entity.HasKey(e => new { e.ClaimId, e.PayeeAccountId }).HasName("pk_payee_account_view");
            entity.Property(e => e.PayeeAccountId).HasColumnName("payee_account_id");
            ClaimsDbContext.MapCommon(entity);
            entity.Property(e => e.ClaimId).HasColumnName("claim_id");
            entity.Property(e => e.PartyId).HasColumnName("party_id");
            entity.Property(e => e.MaskedIban).HasColumnName("masked_iban");
            entity.Property(e => e.VerificationStatus).HasColumnName("verification_status");
            entity.Property(e => e.CoolingOffUntil).HasColumnName("cooling_off_until");
            entity.Property(e => e.IsChange).HasColumnName("is_change");
            entity.Property(e => e.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamptz");
            entity.HasOne<ClaimRow>().WithMany().HasForeignKey(e => e.ClaimId).HasConstraintName("fk_payee_account_view_claim").OnDelete(DeleteBehavior.Restrict);
        });
    }
}
