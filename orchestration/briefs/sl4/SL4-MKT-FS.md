# Brief SL4-MKT-FS — Friendly Settlement clearing stub and capability switch (wave 2)

Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

Wave 2, **batched** with SL4-CMP-RECEIPT (different modules, D-PRG-22). Review: light, plus a regulatory checklist
(PITFALLS 36/37). Estimate: 2.5 h.

**Gate:** start only after **SL3-MKT-TREATMENT and SL5-MKT-STATE have both merged**. In slice 3, SL3-MKT-TREATMENT owns
`CountryPacks.GR/Configuration/**` and the MKT tax services. In slice 5, SL5-MKT-STATE owns `Modules.Market/**`,
`CountryPacks.GR/Configuration/**` and `CountryPacks.CY/**`, and is the MKT migration owner.

**Cross-slice:**
- You own only the new `CountryPacks.GR/Claims/**` and `CountryPacks.CY/Claims/**` folders and
  `Modules.Market/Services/Capability*`.
- Append the capability row to the pack version that SL5-MKT-STATE's registry marks current, using the pack data path
  it defines (read its merged code first).
- Never touch SL5-MKT-ROLLBACK's `Commands/Packs*` or pack activation/rollback code, and add **no MKT migration**.

- **WP / PRD:**
  - REQ-MKT-004 (capability switch);
  - REQ-MKT-109 / -313 (`FriendlySettlementClearing` with `evaluateEligibility`, `submit`, `receiveNotification`,
    `settlementStatement`; R-42/R-87);
  - REQ-CLM-155 / -156 (eligibility facts, switch);
  - PRD-17 GR-06 (status **Verify**), C-01, OI-CLM-01/02.

  Sources: `orchestration/digests/PRD-17.md` (SPI table row 23, capability table `cap.clm.friendly_settlement`
  REVERSIBLE, off by default, on GR), `orchestration/digests/PRD-07.md` §9.1 (FS limits conflict), and
  `contracts/openapi/spi.md` §23. Decisions: **D-SL4-02, D-SL4-03, D-SL4-18**, D-SL2-05 (stubs never in Production).
- **Scope:**
  1. **GR stub adapter** `CountryPacks.GR/Claims/FriendlySettlementClearingStub.cs`, implementing
     `IFriendlySettlementClearing` (typed by SL4-CONTRACTS). It registers only when `!IsProduction()`; in Production
     it is unbound. Its operations:
     - **`EvaluateEligibilityAsync(claimFacts)`:**
       - Eligible only when all of these hold:
         - our insurer and the counterparty are both on the stub member list (a configuration list of organisation
           party ids, seeded in tests);
         - the accident is in Greece;
         - there are exactly 2 vehicles;
         - the damage type is material damage only;
         - the liability allocation is 0/100;
         - the claimed material damage is ≤ **5,000.00**.
       - Every reason that fails is listed.
       - The result carries `ruleId`, `ruleVersion`, `legalStatus = Unverified` (or the enum's nearest value; never
         Settled), `provisional = true`, and the clearing value basis `ACTUAL`.
       - The 5,000.00 limit is the **lower** of the two conflicting PRD candidates (C-01: 6,500 press vs 5,000/15,000
         prompt). Put that sentence in a code comment and in the result's `legalSourceRef`.
     - **`SubmitAsync`:** stores the receivable claim in an in-memory or table store (prefer a small `mkt`-free store
       inside the pack: an in-memory singleton is acceptable for a stub, documented) and returns a clearing reference
       `STUB-FS-…`.
     - **`ReceiveNotificationAsync(message)`:** parses the documented JSON test format (at-fault notification: our
       policy number, loss date, counterparty insurer party id, clearing reference, amount, damage type) into the
       typed notification. On a malformed message it returns a typed error, never an exception to the caller.
     - **`SettlementStatementAsync(period, counterparty?)`:** builds a statement from the stub store: receivables
       submitted by us and payables notified to us in the period, one line each (clearing ref, direction, amount),
       plus the net per counterparty. Deterministic statement id per (period, counterparty).
     - `SubmitDisputeAsync`/`RecordReplyAsync` return `NotImplemented` (out of scope, OI-CLM-02).
  2. **CY stub** `CountryPacks.CY/Claims/`: every operation returns `NotApplicable`.
  3. **Capability `cap.clm.friendly_settlement`:**
     - resolve it through the MKT capability service (`mkt.Capability.get`; implement the minimal resolve for this key
       if it is not yet implemented): GR **on** outside Production, CY off;
     - in Production the key may resolve on, but the SPI is unbound, so callers get `CLM-ERR-FS-DISABLED`. Write a
       test that proves the binding is absent in Production;
     - load validation refuses a capability key the code never looks up (PITFALLS 37).
  4. **Binding:** append one line to `src/CoreIns.Host/Hosting/CountryPackBinding.cs`, in the same style as the fiscal
     stub.
  5. **Document the JSON test formats** (notification, statement) in `CountryPacks.GR/Claims/README.md`. SL4-E2E and
     SL4-CLM-FS-* use them.
- **Files you own:**
  - `src/CoreIns.CountryPacks.GR/Claims/**`, `src/CoreIns.CountryPacks.CY/Claims/**`;
  - `src/CoreIns.Modules.Market/Services/Capability*`;
  - `tests/CoreIns.CountryPacks.Tests/FriendlySettlement/**`.
- **Shared (append only):** `CountryPackBinding.cs`, the GR/CY configuration (one capability row each).
- **Tests:**
  - eligible case (4,000.00);
  - each failing reason separately (7,000.00 → "above material damage limit", 3 vehicles, injury, not a member, abroad,
    50/50 liability);
  - `provisional`/`legalStatus` present on every result;
  - the exact limit 5,000.00 is eligible and 5,000.01 is not;
  - CY returns NotApplicable;
  - Production: the stub is not bound, and the capability is resolved, but a call fails closed;
  - statement determinism and net per counterparty (payable 3,000, receivable 1,200 → net payable 1,800);
  - a malformed notification is a typed error.
- **PITFALLS to self-check (likely):**
  - 10 (fail closed);
  - 24 (the PRD outranks this brief);
  - 36 (the Production gate is `== Settled`; nothing here is Settled);
  - 37.
- **Workflow:** follow `briefs/sl4/_COMMON.md` (batched). Merge main, run the quick gate, push your branch and report
  it. Do not open a PR.
