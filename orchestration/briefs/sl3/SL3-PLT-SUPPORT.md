# Brief SL3-PLT-SUPPORT — dev clock advance, Staff.BillingManager, refund and effective-date authority (S0)

**Runs on Sonnet 5.5 (model: sonnet), D-USR-16.** Batch S0. Review: **deep (security)**. Estimate: 2.5 h.

- **WP / PRD:** SL3-PLT-SUPPORT.
  - Requirements: REQ-PLT-332 (subset: non-production time offset for time-shifted tests); REQ-BIL-188, REQ-BIL-326
    (authority type `BIL.Refund`); PRD-05 §12 (`POL.EffectiveDateOverride`).
  - Sources: PRD-14 (PLT) digest `orchestration/digests/PRD-14.md`; PRD-18 §17.1 (time-shifted environment).
  - Decisions: D-SL3-12, D-SL3-14, D-SL3-08, D-SL2-03 (illustrative grants pattern).
- **Scope:**
  1. **Dev clock advance (D-SL3-12).**
     - Endpoint `POST /dev/clock/advance { "days": n, "hours": n }`, registered only when the environment is
       Development **and** `Platform:Time:Mode=Shiftable`; otherwise 404.
     - Requires Platform.Admin and an Idempotency-Key, and is audited.
     - **Forward-only:** a non-positive advance is refused with 400.
     - The offset is stored in a new single-row table `plt.dev_clock` (PLT migration; you are the PLT migration owner).
       `ShiftableClock` in **both** api and worker reads it with a cache of 1 s or less.
     - Production: the existing startup refusal must stay. Add a test that the endpoint and the table reader are never
       registered in Production, even with Shiftable configured (startup fails, as today).
     - `GET /dev/clock` returns the current offset and now.
  2. **Role Staff.BillingManager** and dev user `billingmgr` (display name "Billing Manager"), in the dev-users file
     created by D-PRG-21. `superuser` gains the role; SoD is unchanged: superuser still cannot approve its own requests.
  3. **Authority types:**
     - `BIL.Refund`: dimensions amount, currency, payee changed. Illustrative grants: Staff.Billing approves up to
       500.00 (auto-approval limit); Staff.BillingManager up to 5,000.00; above that, DENY. Values are in configuration,
       marked **illustrative**, and dropped in Production like D-SL2-03.
     - `POL.EffectiveDateOverride`: dimensions product, transaction type, days. No grants in the slice: the 30-day
       underwriter back-dating is a configuration limit, not an override.
  4. Permissions: `bil.Refund.decide` for Staff.BillingManager; refund read for Staff.Billing. Put them in the
     **BIL/PLT permission files**, append only.
- **Depends on / provides:**
  - **Depends on:** contracts only. `/dev/*` routes sit outside the public contract set, like `/dev/sign-in`.
  - **Provides:** what SL3-E2E-HARNESS and the E2E specs use to advance time; what SL3-BIL-REFUND uses for approvals.
- **Files you own:**
  - `src/CoreIns.Platform/Time/**` and the PLT persistence for `dev_clock` (migration owner);
  - the dev-users file; PLT authority registration;
  - `src/CoreIns.Host/Program.cs` (clock wiring only; **you are its only slice-3 owner**);
  - `tests/CoreIns.Platform.Tests` and `tests/CoreIns.IntegrationTests/Platform/DevClock/**`.
- **Security probes the reviewer will run, so test them first:**
  - the endpoint as a non-admin → 403;
  - with Mode=System → 404;
  - Production with Shiftable → startup fails;
  - a negative or zero advance → 400;
  - two concurrent advances → the offsets add up, never go backwards;
  - the worker sees the new offset within 1 s;
  - `billingmgr` cannot approve a refund it requested (enforced by SL3-BIL-REFUND, but the grant must not imply it).
- **PITFALLS to self-check (likely):** 4 (the client never sets authority or role), 5 (the superuser principal stays
  subject to maker ≠ checker), 18 (nothing personal in audit or idempotency for these calls), 35.
- **Workflow:** `briefs/sl3/_COMMON.md`: merge main, quick gate, push,
  `gh pr create --base main --title "SL3-PLT-SUPPORT: dev clock advance, billing manager, refund authority"`, do not
  merge.
