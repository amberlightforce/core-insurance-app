namespace CoreIns.Modules.Billing.Persistence;

/// <summary>
/// BO-BIL-09: a payee account's verification-of-payee result may be re-run (<c>bil.PayeeAccount.verify</c>) without superseding the
/// account. The bank details stay frozen; the four verification columns may change only when the check time advances, and never to
/// CONFIRMED (a manual confirmation is not built), so a NO_MATCH cannot be rewritten without a new bank check on record.
/// </summary>
internal static class PayeeVerificationDatabaseSql
{
    public const string Up = """
        CREATE OR REPLACE FUNCTION bil.freeze_payee_account() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.payee_account_id, NEW.legal_entity_id, NEW.party_id, NEW.purpose, NEW.iban_encrypted, NEW.iban_blind_index, NEW.iban_last4, NEW.valid_from, NEW.created_at,
                NEW.cooling_off_until, NEW.is_change, NEW.supersedes_id)
               IS DISTINCT FROM
               (OLD.payee_account_id, OLD.legal_entity_id, OLD.party_id, OLD.purpose, OLD.iban_encrypted, OLD.iban_blind_index, OLD.iban_last4, OLD.valid_from, OLD.created_at,
                OLD.cooling_off_until, OLD.is_change, OLD.supersedes_id) THEN
                RAISE LOG 'SECURITY: change of frozen payee account % columns refused for role %', OLD.payee_account_id, current_user;
                RAISE EXCEPTION 'bil.payee_account bank details are never overwritten: a change supersedes the account (REQ-BIL-343)' USING ERRCODE = 'BL004';
            END IF;
            IF (NEW.verification_status, NEW.vop_result, NEW.vop_suggested_name, NEW.vop_checked_at)
               IS DISTINCT FROM
               (OLD.verification_status, OLD.vop_result, OLD.vop_suggested_name, OLD.vop_checked_at)
               AND (NEW.vop_checked_at IS NULL
                    OR (OLD.vop_checked_at IS NOT NULL AND NEW.vop_checked_at <= OLD.vop_checked_at)
                    OR NEW.verification_status = 'CONFIRMED') THEN
                RAISE LOG 'SECURITY: change of payee account % verification without a new bank check refused for role %', OLD.payee_account_id, current_user;
                RAISE EXCEPTION 'bil.payee_account verification changes only with a newer bank check (REQ-BIL-344)' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;
        """;

    public const string Down = """
        CREATE OR REPLACE FUNCTION bil.freeze_payee_account() RETURNS trigger LANGUAGE plpgsql AS $fn$
        BEGIN
            IF (NEW.payee_account_id, NEW.legal_entity_id, NEW.party_id, NEW.purpose, NEW.iban_encrypted, NEW.iban_blind_index, NEW.iban_last4, NEW.valid_from, NEW.created_at,
                NEW.cooling_off_until, NEW.is_change, NEW.supersedes_id, NEW.verification_status, NEW.vop_result, NEW.vop_suggested_name, NEW.vop_checked_at)
               IS DISTINCT FROM
               (OLD.payee_account_id, OLD.legal_entity_id, OLD.party_id, OLD.purpose, OLD.iban_encrypted, OLD.iban_blind_index, OLD.iban_last4, OLD.valid_from, OLD.created_at,
                OLD.cooling_off_until, OLD.is_change, OLD.supersedes_id, OLD.verification_status, OLD.vop_result, OLD.vop_suggested_name, OLD.vop_checked_at) THEN
                RAISE LOG 'SECURITY: change of frozen payee account % columns refused for role %', OLD.payee_account_id, current_user;
                RAISE EXCEPTION 'bil.payee_account bank details are never overwritten: a change supersedes the account (REQ-BIL-343)' USING ERRCODE = 'BL004';
            END IF;
            RETURN NEW;
        END
        $fn$;
        """;
}
