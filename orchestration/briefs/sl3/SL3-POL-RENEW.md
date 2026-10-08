# Brief SL3-POL-RENEW — manual renewal with explicit acceptance (S1)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S1. Review: **deep (temporal)**. Estimate: 4 h.

- **WP / PRD:** SL3-POL-RENEW.
  - Requirements:
    - REQ-POL-245 (window check only: expiry − now ≤ `pol.renewal.lead_days` = 45, illustrative; no batch);
    - REQ-POL-246 (copy the risk tree as valid at expiry; resolve the product version at the new term start);
    - REQ-POL-249 (subset: RENEWAL rating, PRE_BIND UW evaluation; no bonus-malus);
    - REQ-POL-250 (subset: `RenewalOffered`, job `Quoted.Offered`, no document, no notice clock);
    - REQ-POL-253 (explicit acceptance only), REQ-POL-257 (record channel, actor, time);
    - REQ-POL-258 ("Renew now");
    - REQ-POL-263 (`RenewalCreated/Offered/Bound`);
    - REQ-POL-033 (pin artefacts on the new term);
    - REQ-POL-201 (UW referral decisions flow back without re-entry; D-UW-01).
  - E2E-04 steps 1–7 (`SLICE-PLAN-3.md` §3.2). PRD-05 §5.8, §6 SCR-POL-17.
  - Decisions: D-SL3-03, D-SL3-10, D-SL3-11, D-UW-01.
- **Scope:**
  1. `pol.Renewal.create(termId)`:
     - the term is InForce or Scheduled, inside the window, has no open renewal (409), and no next term exists;
     - creates a Renewal job (Draft) with `expiring_term_id` and `base_transaction_id` = expiring head;
     - copies the risk tree of the segment valid at the expiry instant (static locators kept);
     - resolves the product version at the new term start (1.1 from SL3-PFC-MOTOR11);
     - new term period = [expiry, expiry + P12M);
     - emits `RenewalCreated`.
  2. **Quote/offer.** RAT RENEWAL mode under the artefact active at the new term start, then the UW PRE_BIND evaluation
     through the existing in-process path. The referral flag works exactly as for new business; `uwsenior` decides
     referrals with D-UW-01 SoD intact.
     - `pol.Renewal.offer` → `Quoted.Offered`, `RenewalOffered` (premium, acceptance mode EXPLICIT, no deadline clock).
       Re-offer after edits keeps the prior offers (REQ-POL-255 behaviour; minimal).
  3. **`pol.Renewal.accept` (channel STAFF, accepted_by, accepted_at), then bind in the same command:**
     - under the policy lock;
     - the expiring head must still equal the base, else `POL-ERR-REBASE-REQUIRED`;
     - not Referred;
     - creates term n+1 (Scheduled, `predecessor_term_id`, pinned product version, artefact hashes, configuration
       hash, currency, day count);
     - the first segment and charge lines through SL3-POL-ENGINE `NewTerm` and RAT tax lines (NEW_BUSINESS → `APPLY`);
     - transaction kind Renewal;
     - `RenewalBound` and `ChargeDeltaEmitted`.
     - The policy number is unchanged (REQ-POL-031).
  4. `pol.Policy.get` and `pol.Term.timeline` show both terms.
  5. Register in `PolicyModule.cs` (append) and add `Api/RenewalController.cs`. Add permissions
     `pol.renewal.manage` and `pol.Renewal.*` for Staff.Underwriter (append).
- **Depends on / provides:**
  - **Depends on:** **merged** SL3-POL-TEMPORAL and SL3-POL-ENGINE; contracts for RAT, PFC and MKT.
  - **Provides:** `RenewalBound` and deltas for SL3-BIL-CREDIT (term n+1 invoice); the API for SL3-UI-POL-JOBS.
- **Files you own:** `src/CoreIns.Modules.Policy/Commands/Renewal/**`, `Api/RenewalController.cs`,
  `tests/CoreIns.IntegrationTests/Policy/Renewal/**`.
- **Shared (append only):** `PolicyModule.cs` and the POL permission file.
- **Do not touch:** `Persistence/**` (no migrations), `Queries/**`, `Domain/Servicing/**`, or the Change/Cancellation
  folders.
- **Tests:**
  - "Renew now" inside the window → offer → accept → term 2 Scheduled, contiguous with term 1 (no gap, no overlap: the
    exclusion constraint holds); Σ term-2 deltas = term-2 written;
  - outside the window → refused;
  - a referred renewal cannot be accepted until approved by `uwsenior`; the creator cannot approve (D-UW-01);
  - a change bound on term 1 after the offer → accept refused with `POL-ERR-REBASE-REQUIRED`;
  - accept twice → idempotent replay or 409, never two terms;
  - the snapshot of term 1 is unaffected;
  - a cancelled term cannot be renewed.
- **PITFALLS to self-check (likely):** 5 (every renewal job participant is collected for UW SoD), 6, 12, 13, 14
  (term boundary half-open at expiry), 15, 17.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-POL-RENEW: manual renewal with explicit acceptance"`, do not merge.
