# Slice plan 6 - remaining E2E journeys (E2E-05, -07, -08, -09, -10, -11)

Planned 2026-10-19 by the slice-6 planner. Process: `briefs/LIGHT-WORKFLOW.md` (D-USR-20): working system first, main-path
tests only, Sonnet builders push straight to main, no review agents, up to ~12 concurrent. Briefs: `briefs/sl6/SL6-<WP>.md`.

## 1. Goal and scope

Prove six journeys end to end on isolated Docker stacks (pattern of `run-e2e03.sh`), each with a CI job:

| Journey | What it needs that does not exist on main |
|---|---|
| E2E-07 non-payment cancellation | CMP clock engine, DOC (notice + delivery proof), BIL delinquency, POL non-payment cancellation |
| E2E-08 distance withdrawal | CHN withdrawal function, `pol.Withdrawal.submit` (void + full refund), DOC acknowledgement |
| E2E-09 DSAR | CMP DSAR orchestration, a participant per module, DAT erasure |
| E2E-10 AI kill switch | PLT AI control plane, CMP AI register, one AI feature per module, DAT bias test |
| E2E-11 out-of-sequence change | POL reverse-and-reapply, FIN closed-period guard, BIL re-spread, RI cession delta |
| E2E-05 migration | MIG framework, importers for POL, BIL/FIN, CLM/RI/CMP |

E2E-12 and E2E-01..04, -06 are done or in flight (slices 3-5); this slice does not touch them.

## 2. What exists on origin/main (read 2026-10-19, c49195d)

- Built modules: PTY, MKT, PFC, RAT, UW, POL (change, cancellation, renewal), BIL (payments, refunds, disbursements),
  CLM, RI (registry; recovery in progress), FIN, CMP **fiscal documents only**, PLT (approvals, audit, outbox, DevClock).
- **Empty shells** (only a `*Module.cs`): DOC, WRK, CHN, MIG, DAT. PLT lives in `src/CoreIns.Platform` (no Modules.Platform).
- Generated contracts already exist for every op named below (`contracts/openapi`, `contracts/events`), including
  `bil.Delinquency.*`, `chn.Withdrawal.*`, `cmp.Clock.*`, `cmp.Dsar.*`, `plt.Ai.*`, `mig.*`, `pol.Withdrawal.submit`.
  Nothing implements them. New OpenAPI paths are only small internal ops named in the briefs.
- No web module yet for compliance, channels, platform AI, migration, documents.
- Not duplicated here: SL4 RI recovery/registry, FIN v4, BIL receivables, CMP receipt, E2E harness (other builders are
  active on these; this plan depends on them where noted).

## 3. Waves and WPs

Est. = one Sonnet builder. A module with several migration owners: later WPs regenerate their migration after merging.

| WP | Module: files owned (short) | Depends on | Wave | Est. |
|---|---|---|---|---|
| SL6-CMP-CLOCK | CMP: src/CoreIns.Modules.Compliance/{Clocks/**,Api/Clock*,Persistence/** (CMP migration owne... | - | 1 | 4h |
| SL6-DOC-CORE | DOC: src/CoreIns.Modules.Documents/** (all new | - | 1 | 4.5h |
| SL6-CHN-WITHDRAW | CHN: src/CoreIns.Modules.Channels/** (new | - | 1 | 3.5h |
| SL6-PLT-AI | PLT: src/CoreIns.Platform/Ai/** (new), Platform persistence migration (PLT migration owner W... | - | 1 | 4h |
| SL6-POL-WITHDRAW | POL: src/CoreIns.Modules.Policy/Commands/Withdrawal/** (new), Api/WithdrawalController.cs, P... | - | 1 | 4h |
| SL6-POL-OOS | POL: src/CoreIns.Modules.Policy/Commands/Change/Oos/** (new), Domain/Servicing/Oos*, Api/Oos... | - | 1 | 5h |
| SL6-MIG-FRAME | MIG: src/CoreIns.Modules.Migration/** (new | - | 1 | 5h |
| SL6-DAT-CORE | DAT: src/CoreIns.Modules.Data/** (new | - | 1 | 3.5h |
| SL6-DSAR-SPI | SHARED: src/CoreIns.SharedKernel/Dsar/** (new), one-line Host registration helper, tests/CoreIn... | - | 1 | 2h |
| SL6-BIL-DELINQ | BIL: src/CoreIns.Modules.Billing/{Commands/Delinquency*,Api/Delinquency*,Domain/Delinquency*... | CMP-CLOCK first commit (IComplianceClockService), DOC-CORE first commit (IDocumentRequestService); until then use local fakes | 1 | 5h |
| SL6-POL-NONPAY | POL: src/CoreIns.Modules.Policy/Commands/Cancellation/NonPayment/** (new), consumer file, mi... | BIL-DELINQ event schema; POL-WITHDRAW merged (migration) | 2 | 4h |
| SL6-CMP-AIREG | CMP: src/CoreIns.Modules.Compliance/AiRegister/** (new), migration (after CMP-CLOCK), cmp.ya... | PLT-AI interface; CMP-CLOCK merged (migration) | 2 | 3h |
| SL6-CMP-DSAR | CMP: src/CoreIns.Modules.Compliance/Dsar/** (new), migration (after CMP-CLOCK), cmp.yaml (Ds... | DSAR-SPI; CMP-CLOCK merged | 2 | 5h |
| SL6-DSAR-PART-A | PTY, POL, UW, RAT (export only): one participant file per module under its Services/Dsar/** (new), a registration line i... | DSAR-SPI | 2 | 3h |
| SL6-DSAR-PART-B | BIL, FIN, CLM, RI: one participant file per module under its Services/Dsar/** (new), a registration line i... | DSAR-SPI | 2 | 3h |
| SL6-DSAR-PART-C | CHN, DOC, PLT, MIG, CMP own data: one participant file per module under its Services/Dsar/** (new), a registration line i... | DSAR-SPI | 2 | 3h |
| SL6-AI-FEATURES | CLM, PTY, BIL, CHN, UW: small file sets: Claims/Services/Ai/**, Party/Services/Ai/**, Billing/Services/Ai/**, C... | PLT-AI (IAiGate); CHN-WITHDRAW merged (CHN migration) | 2 | 4.5h |
| SL6-MIG-POL-IMPORT | POL: src/CoreIns.Modules.Policy/Migration/** (new), migration (regenerate), pol.yaml (pol.*.... | MIG-FRAME; POL-OOS merged (migration) | 2 | 4.5h |
| SL6-MIG-BIL-IMPORT | BIL, FIN: src/CoreIns.Modules.Billing/Migration/** (new), src/CoreIns.Modules.Finance/Migration/*... | MIG-FRAME; BIL-DELINQ merged (BIL migration); FIN-OOS-GUARD merged (FIN migration) | 2 | 4.5h |
| SL6-FIN-OOS-GUARD | FIN: src/CoreIns.Modules.Finance/Posting/Oos*, Domain/PostingRules.cs (edit), permissions fi... | POL-OOS event shape; b-finv4 merged | 2 | 3h |
| SL6-RI-CESSION-DELTA | RI: src/CoreIns.Modules.Reinsurance/Cession/** (new), ReinsuranceModule.cs (DI append), RI ... | POL-OOS event shape; SL4-RI-RECOVERY merged | 2 | 3.5h |
| SL6-MIG-CLM-RI-IMPORT | CLM, RI, CMP: src/CoreIns.Modules.Claims/Migration/** (new), CMP cmp.Clock.import + cmp.Bureau.handov... | MIG-FRAME; CMP-CLOCK; RI-CESSION-DELTA | 3 | 4h |
| SL6-BIL-RESPREAD | BIL: src/CoreIns.Modules.Billing/Commands/Respread*, Events/PolicyChangeHandlers.cs (append) | POL-OOS; FIN-OOS-GUARD | 3 | 3h |
| SL6-UI-BIL-DELINQ | web: web/src/modules/billing/delinquency/** | its backend WP merged, or build against generated types | 3 | 3h |
| SL6-UI-CHN-WITHDRAW | web: web/src/modules/channels/withdrawal/** | its backend WP merged, or build against generated types | 3 | 3h |
| SL6-UI-CMP-DSAR | web: web/src/modules/compliance/dsar/** | its backend WP merged, or build against generated types | 3 | 3h |
| SL6-UI-PLT-AI | web: web/src/modules/platform/ai/** | its backend WP merged, or build against generated types | 3 | 3h |
| SL6-UI-MIG | web: web/src/modules/migration/** | its backend WP merged, or build against generated types | 3 | 3h |
| SL6-E2E-07 | tests/e2e: tests/e2e/tests/e2e07*.spec.ts, stack/compose.e2e07.yaml + e2e07.env, run-e2e07.sh, ci.... | CMP-CLOCK, DOC-CORE, BIL-DELINQ, POL-NONPAY | 4 | 3.5h |
| SL6-E2E-08 | tests/e2e: tests/e2e/tests/e2e08*.spec.ts, compose.e2e08.yaml, run-e2e08.sh, ci.yml job e2e08, see... | CHN-WITHDRAW, POL-WITHDRAW, DOC-CORE | 4 | 3h |
| SL6-E2E-09 | tests/e2e: tests/e2e/tests/e2e09*.spec.ts, compose.e2e09.yaml, run-e2e09.sh, ci.yml job e2e09, see... | CMP-DSAR, DSAR-PART-A/B/C, DAT-CORE | 4 | 4h |
| SL6-E2E-10 | tests/e2e: tests/e2e/tests/e2e10*.spec.ts, compose.e2e10.yaml, run-e2e10.sh, ci.yml job e2e10 | PLT-AI, CMP-AIREG, DAT-CORE, AI-FEATURES | 4 | 3h |
| SL6-E2E-11 | tests/e2e: tests/e2e/tests/e2e11*.spec.ts, compose.e2e11.yaml, run-e2e11.sh, ci.yml job e2e11, see... | POL-OOS, FIN-OOS-GUARD, RI-CESSION-DELTA, BIL-RESPREAD | 4 | 4h |
| SL6-E2E-05 | tests/e2e: tests/e2e/tests/e2e05*.spec.ts, compose.e2e05.yaml, run-e2e05.sh, ci.yml job e2e05, fix... | MIG-FRAME, MIG-POL-IMPORT, MIG-BIL-IMPORT, MIG-CLM-RI-IMPORT | 4 | 4.5h |

Counts: wave 1 = 10 WPs, wave 2 = 12, wave 3 = 7 (5 UI), wave 4 = 6 E2E (34 WPs, about 130 builder-hours). Waves are
gates by real dependency, not by calendar: a WP starts as soon as the WPs it names are merged, so waves overlap.
Critical paths:
- E2E-07: CMP-CLOCK + DOC-CORE -> BIL-DELINQ (w1, fakes until the two SPI interfaces land) -> POL-NONPAY -> E2E-07.
- E2E-05: MIG-FRAME -> MIG-POL-IMPORT / MIG-BIL-IMPORT -> MIG-CLM-RI-IMPORT -> E2E-05 (longest, about 4 sequential WPs).
- E2E-10: PLT-AI -> CMP-AIREG + AI-FEATURES -> E2E-10.

## 4. Defaults taken for open questions (append to DECISIONS.md as D-SL6-nn)

| ID | Decision | Basis / fail-closed reason |
|---|---|---|
| D-SL6-01 | **No WRK.** Where a PRD step creates a WRK activity (clock warning, returned letter, CMP-253 safety net) the module writes a `WorkRequested` outbox record and stops. E2E asserts the record only | WRK is an empty shell; its own wave |
| D-SL6-02 | **F-401 (IPT/levy on distance withdrawal):** customer refund = total paid incl. IPT and levies, deduction 0.00. The void posts a fiscal credit with source `DistanceWithdrawal`; the GR treatment row carries `legalStatus SECONDARY_SOURCE` and `tax_position TAX_OPINION_PENDING`. A net-of-tax refund is never offered | Law 5317/2026 Art 72, consumer pays nothing; the customer outcome is the same under either reading, only the insurer tax position is open (PRD-18 E2E-08 step 6) |
| D-SL6-03 | **OI-DOC-03:** non-payment notice by REGISTERED_POST only (D-263). Electronic proof is not accepted. No delivery proof means no notification date and **no clock**; a returned letter yields `DeliveryFailed`, case NOTICE_RETURNED | PRD: unproven delivery starts no clock |
| D-SL6-04 | **Non-payment mechanics.** Waiting period = one calendar month from the notification date, due at end of day Europe/Athens (`BIL_NONPAY_NOTICE`, `legalStatus SECONDARY_SOURCE`). Cancellation effective at the start of the next local day. Tolerance 5.00 EUR and grace 5 days are illustrative config. An open claim is not a cancellation exception; a vulnerable-customer flag or hold is, and goes to a PSR queue | R-60, K-01; INV-11 needs a tolerance, none specified |
| D-SL6-05 | **OI-MIG-12 / F-406 (rollback).** Reversal = each module's `IMigrationReverser`, allowed only for `origin=MIGRATION` objects with no post-migration activity; otherwise `MIG-ERR-REVERSAL-BLOCKED` per object. Modules without a reverser (UW, DOC, CMP, WRK, CHN) make a batch holding their objects `NOT_REVERSIBLE`. E2E-05 step 10 asserts both outcomes, labelled `OI-MIG-12 default` | Owners have not named reversal operations; refuse rather than guess |
| D-SL6-06 | **Withdrawal window** = pack config `withdrawal.days`, default 14, last day ends 23:59:59 Europe/Athens. The builder verifies the number in PRD-05/PRD-17 and tags it with its `legalStatus` (illustrative if unverified). Late declarations are recorded and declined | PRD E2E-08 step 1 |
| D-SL6-07 | **F-404 retention + DSAR.** Retention durations are unset: ledger, invoice, claim file, policy and fiscal document, bureau fact are **restrict-only** with `retentionEnd = UNSET_F404`; the rest may be erased or anonymised. Every record class returns ERASED, ANONYMISED, RESTRICTED, NOT_HELD or NOT_BUILT, never a silent skip. A NOT_BUILT participant (WRK) blocks auto-completion. Map = the 15 rows of PRD-18 section 9.2.2 (CR-CMP-03). Erase/restrict after approval by a DPO who is not the case creator | Fail closed: retain rather than destroy |
| D-SL6-08 | **AI governance.** Enabling a feature requires CMP register status APPROVED; with no register or an unreadable state the gate answers Disabled. Gate cache <= 5 s plus event invalidation, so the kill switch is complete in < 60 s. Disabled calls return 503 `AI-ERR-DISABLED` with a neutral message and the manual path unchanged. AI output is a suggestion; nothing writes state from it | PLT-220; D4 adds `AiSystemStatusChanged` -> disable |
| D-SL6-09 | **No real AI provider.** `plt.Ai.invoke` uses a deterministic fake provider. No metered API calls | Memory: metered quota |
| D-SL6-10 | **OOS.** NET mode is final for P1-P2; GROSS is a flag covered by unit tests only. Locked FIN month: the correction posts in the first open period with `correctionOf` (G5, REQ-POL-108), never into the closed period. Re-rating uses the term's pinned artefacts. The DAT written-premium assertion is deferred (DAT-CORE has no marts); E2E-11 asserts POL premium delta equality in both modes | D4, D6, D-104 |
| D-SL6-11 | **Providers are stubs** (registered post, bank, fiscal), driven by dev-only endpoints never reachable in Production. E2E advances the dev clock; no wall-clock sleeps | PITFALLS time handling |
| D-SL6-12 | **Documents are HTML** (PDF only if a library is already in the repo), stored in the database; templates in el and en. No signatures, no e-mail provider beyond a stub | Thin slice |
| D-SL6-13 | **Contracts rule.** Each WP edits only its own module's yaml and generated folder. Cross-module events live in the emitting module's yaml. No central contracts WP. Interfaces other WPs depend on are pushed as the first commit (named in the briefs) | Avoids a serial contracts bottleneck; conflicts regenerate |
| D-SL6-14 | **Slice-6 process.** 34 WPs, 4 waves, light workflow. UI WPs follow the Aegean mockup but have no review gate; screenshots go in `orchestration/ux/<wp>/` as FYI. Each E2E WP owns its compose file, run script and `ci.yml` job (appended) | D-USR-20 |

## 5. File ownership and merge hotspots

Disjoint by module. Shared files that WILL conflict (merge additively): Host module registration and `Program.cs` lines,
`permissions/*.json`, `routes.tsx`, i18n json, `ci.yml`, `seed-demo.py`, generated contracts, EF snapshots.

| Module | Migration owners in order |
|---|---|
| CMP | CMP-CLOCK (w1) -> CMP-AIREG, CMP-DSAR (w2, the second regenerates) -> MIG-CLM-RI-IMPORT small additions (w3) |
| POL | POL-WITHDRAW, POL-OOS (w1, second regenerates) -> POL-NONPAY, MIG-POL-IMPORT (w2, second regenerates) |
| BIL | BIL-DELINQ (w1) -> MIG-BIL-IMPORT (w2) -> BIL-RESPREAD (w3, probably none) |
| CHN | CHN-WITHDRAW (w1) -> AI-FEATURES (w2) |
| FIN, RI, DOC, MIG, DAT, PLT | one owner each |

External gates (other sessions active now): FIN-OOS-GUARD after the FIN v4 work (b-finv4) merges; RI-CESSION-DELTA after
the RI recovery work (b-rirecovery) merges; E2E WPs reuse the E2E harness (b-e2eharness) helpers and must not edit them.

## 6. Order of play for the orchestrator

1. Start all of wave 1 at once (10 builders). BIL-DELINQ starts with local fakes and swaps to the real SPI interfaces
   when CMP-CLOCK and DOC-CORE push their first commits.
2. As each wave-1 WP merges, release its dependants. Wave 2 starts piecemeal: DSAR-PART-A/B/C need only DSAR-SPI, and
   CMP-DSAR needs DSAR-SPI and CMP-CLOCK, so they start in the first hours.
3. Wave 3 UI WPs may start once the backend contract is generated; they never block E2E.
4. Each E2E WP starts when its named dependencies are merged and runs its CI job; failures go back to the owning WP unless
   the cause is in the spec.
5. After E2E-05..11 are green, the standing goal (E2E-01..12 green) is met modulo section 7.

## 7. Known gaps accepted by this slice

- WRK, DAT marts, lakehouse, real AI provider, real post/e-mail/fiscal providers: not built; stubs only.
- Retention periods (F-404), IPT/levy tax position (F-401), reversal ownership (OI-MIG-12): defaults above; they need owner
  decisions before go-live (G1/G2).
- E2E-11 DAT assertion, GROSS mode end to end, CY pack variants.
- No finance-grade hardening: each builder lists `TODO(hardening)` items in its final report.

## 8. Open questions (defaults already taken; the user may overrule)

| # | Question | Default |
|---|---|---|
| Q1 | Is electronic proof acceptable for the non-payment notice? | No (D-SL6-03) |
| Q2 | Who owns reversal of migrated objects? | POL, BIL, FIN, CLM, RI, PTY, untouched objects only (D-SL6-05) |
| Q3 | Retention periods per record class? | Restrict, never erase, for regulated classes (D-SL6-07) |
| Q4 | Insurer tax position on distance-withdrawal refunds? | Customer refunded in full, flagged pending (D-SL6-02) |
