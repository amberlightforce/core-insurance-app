# HANDOVER — Greek P&C Core Insurance

**Updated:** 2026-10-08 · **Repo:** https://github.com/amberlightforce/core-insurance-app (private, branch `main`) ·
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD

## 0. Status in one paragraph

Foundations (Phase 1) and the **thin end-to-end slice** (Phase 2) are done. One motor product can be quoted, rated,
underwritten and bound into a policy. Billing then:
- invoices it, with a **stub** myDATA fiscal document;
- takes an exact payment;
- posts **balanced journals** in finance.

All of it runs locally with `docker compose`. It is proven by an automated E2E-01 test, at API and browser level, that
runs in GitHub CI, and it was accepted after a manual walkthrough in a real Chrome (D-SLC-21). CI on `main` is green.

The rest of the Motor MVP is **not built**: renewals, changes, cancellations, claims, reinsurance, documents, portals,
migration and reporting, plus the parts of every module the slice did not need. About 10–15% of the 3,711 Must
requirements are covered, most of them partially.

**The programme is waiting for the user to choose the next step** (§12).

Read this with `orchestration/STATUS.md`, `PLAN.md`, `SLICE-PLAN.md` and `DECISIONS.md` (about 180 rulings). Those
files are the programme's memory.

---

## 1. What exists

| Area | State |
|---|---|
| Specifications | Outside the repo, in `../core-insurance-prds` and `../core-insurance-infra`: 18 PRDs, system contract v1.12 (frozen), Aegean design guide, 2 infra specs. Summaries are in `orchestration/digests/` |
| Plan and backlog | `PLAN.md` (waves W1–W9); `backlog/backlog.json` + `BACKLOG.md` with 134 work packages that hold all 3,711 Must requirements; `SLICE-PLAN.md` for the slice |
| Foundations | Built, reviewed, merged (§5) |
| Slice modules | Party, Market, Product, Rating, Underwriting, Policy, Billing, Compliance (fiscal stub only), Finance. Each covers only what E2E-01 needs (§6) |
| Staff UI | Greek-first React app with these screens: customer search/create/view, 7-step quote wizard, policy view, billing account/invoice/payment, journals |
| Not started | Claims, Reinsurance, Documents, Work management, Channels/portals, Data/reporting, Migration. Their projects exist with generated contract types only |
| Tests | 10 .NET test projects with about 1,700 tests, 297 of them Testcontainers integration tests; 1,200 web tests (Vitest + axe); Playwright E2E-01 (API + UI) |

### Modules and schemas

| Module | Schema | Slice scope |
|---|---|---|
| Platform | `plt` | Command pipeline, outbox, audit, idempotency, numbering, permissions, data keys, dev sign-in |
| Party | `pty` | Person create/get/search (accent-insensitive, ELOT 743 Latin forms, AFM), masked P2 data with an audited reveal, intermediaries, producer codes |
| Market | `mkt` | Legal entity GR-TEST, configuration resolver (configuration authority, D-SLC-15), rounding, GR pack values from the PRDs with legalStatus |
| Product | `pfc` | Motor Private Car (MOTOR-GR 1.0): MTPL + own damage + windscreen, MOTOR-RISK question set, charge types, write-once versions resolved by date |
| Rating | `rat` | Decision-table rating on the shared rule engine (illustrative tariff, D-SLC-04), IPT from MKT, content-addressed worksheets |
| Underwriting | `uw` | PRE_QUOTE / PRE_BIND rule sets: accept, refer or decline (illustrative thresholds) |
| Policy | `pol` | Submission → draft → quote → bind; policy/term/transaction/segment with bitemporal storage, exclusion constraints and append-only triggers; gapless policy numbers |
| Billing | `bil` | Account, charge intake, ANNUAL invoice (gapless number), payment, allocation, sealed sub-ledger, `BillingEntryPosted` |
| Compliance | `cmp` | Fiscal document request through the GR **stub** channel (never bound in Production) |
| Finance | `fin` | Posting rules as data, intake from `BillingEntryPosted` only (D-SLC-12), balanced append-only sealed journals, gapless journal numbers |

## 2. How to run and test it

### Toolchain (D-ARC-30)
Everything runs **natively on Windows**: .NET SDK 10.0.401, Node 24 / npm 11, Docker Desktop and Python 3.12. WSL is no
longer used. If Windows Smart App Control ever blocks a native build again, stop and report it.

### Build and test
```bash
dotnet build CoreIns.sln -c Release          # warnings are errors
dotnet test --project tests/CoreIns.IntegrationTests -c Release   # Testcontainers PostgreSQL 17 (Docker must run)
cd web && npm ci && npm run format:check && npm run lint && npm run lint:css && npm run lint:i18n && npm run typecheck && npm test && npm run build
dotnet run --project tools/CoreIns.ContractGen -- --check   # generated C# must match contracts/
```
- Test executables can hang on exit when their output is piped, so redirect it to a file.
- To regenerate an npm lock file, use `npx npm@11.19.0 install --package-lock-only` (D-ARC-29).

### Local stack
```bash
cp infra/local/.env.example infra/local/.env         # set passwords; on this machine host ports are in the 25000 range
docker compose -p coreins -f infra/local/compose.yaml up -d --build
python infra/local/seed-demo.py http://127.0.0.1:25000   # optional demo data: one paid policy + one open quote
docker compose -p coreins -f infra/local/compose.yaml down   # add -v to wipe the database
```
- **Start order:** postgres → bootstrap → migrate → api + worker. Migrations never run at api/worker start-up.
- **Web app:** `http://localhost:<API port>`; on the dev machine that is http://localhost:25000.
- **Other UIs:** Aspire dashboard (logs/traces) on :28888 and Mailpit on :28025 on the dev machine.
- **Dev sign-in (D-SLC-03, Development only):** `/dev/sign-in`. Users:

  | User | Role | Can do |
  |---|---|---|
  | `underwriter` | Staff.Underwriter | Customers, quotes, bind, policy; reads invoices (D-SLC-20) |
  | `billing` | Staff.Billing | Accounts, invoices, payments |
  | `finance` | Staff.Finance | Journals |
  | `admin` | Platform.Admin | Product import and the rest |

  The token is per tab and is invalidated when the api restarts; a 401 sends you back to sign-in.
- `infra/local/smoke.sh` and `infra/local/README.md` have curl examples.
- **Valid test AFMs:** 094123454, 112233441, 045678904, 135792468, 102030408. 123456783 is already taken by the smoke-test customer.

### Automated E2E-01
```bash
tests/e2e/run-e2e01.sh          # fresh "coreins-e2e" stack (ports 26000+), API + Playwright UI specs, then down -v
KEEP_STACK=1 tests/e2e/run-e2e01.sh   # leave it running;  SKIP_BUILD=1 reuses the image
```
- **Isolation:** own project name, image tag and env file, so it never touches a developer's `coreins` stack.
- **What it asserts:** the full money path in integer cents (premium, IPT, total, invoice items = policy charge lines, PAID, every journal balanced, clearing accounts net to zero) and as-of reads.
- **UI spec:** calls `alive(page)` after every navigation to catch render loops.

### CI (`.github/workflows/`)
- **`ci.yml`:**
  - .NET build and tests (including Testcontainers), web gates (including Prettier `format:check`), Bicep plus the Key Vault least-privilege guard, container image and Trivy scan, SBOMs.
  - **e2e01** job: compose stack plus Playwright Chromium.
- **`codeql.yml`:** scans C#, TypeScript and Actions. Results go to a SARIF artifact, and the job fails on any finding of security-severity ≥ 7. GitHub code scanning is not enabled on this private repo (D-ARC-31).
- **`deploy.yml`:** dispatch-only, OIDC to Azure. Never run; no Azure environment exists.
- **Dependabot** is weekly. It ignores the Roslyn analyzer packages, which are pinned to the SDK.

## 3. Demo: what you can click through

1. **Customers:** search "ΝΙΚΟΛΑΟΥ" (no accents) or "papadakis". Create a person (AFM checked with mod-11). Personal data is masked; **Reveal** asks for a purpose and is audited.
2. **Policies → New quote.** Steps:
   1. Customer (single click) and product.
   2. Vehicle (value needed for own damage).
   3. Driver.
   4. Covers. MTPL is compulsory at €1,300,000.
   5. Questions. Business use is **referred**; hire or reward is **declined**; a driver under 21 is **referred at bind**.
   6. Premium: €430.00 + IPT €64.51 = **€494.51** for the demo car, with the *illustrative tariff* and *provisional tax* banners.
   7. Bind, after ticking the confirmation.
3. **Policy view:** status (Scheduled / In force / Expired from "as of"), covers, charge lines, invoices.
4. **Billing** (as `billing`): invoice with the **stub** fiscal document notice. Record an exact payment and it becomes **Paid**. Paying a different amount goes to suspense rather than being guessed.
5. **Finance** (as `finance`): journals per policy number, each balanced. Accounts carry *Illustrative (PRD-09)* or *Technical placeholder* badges.

## 4. Architecture rules every builder must follow

- **The infra specs are binding:** .NET 10 modular monolith, PostgreSQL 17, EF Core + Dapper, Hangfire, transactional outbox (no broker), React 19, Entra ID (dev sign-in locally), Gotenberg, Azure Container Apps, Bicep.
- **PRD wording → built as (DECISIONS §B):**

  | PRD says | Built as |
  |---|---|
  | broker | outbox |
  | workflow engine | state machines + deadline rows + Hangfire scanners |
  | own IdP | Entra |
  | lakehouse | `rpt_*` schemas |
  | in-house PDF engine | Gotenberg + finaliser |
  | PG18 | PG17 exclusion constraints |

- **The module pattern is `docs/module-pattern.md`; copy it:**
  - Layout: one schema per module; a DbContext per module on the shared `DbSession`; per-module EF migrations applied only by the `migrate` role.
  - Commands go through the platform pipeline: validation → transaction → idempotency → audit → authority → handler.
  - Events go through the outbox in the same transaction.
  - Modules call each other only through generated `*.Contracts` interfaces (architecture tests enforce this).
- **Personal data (P2+):**
  - Field-encrypted (AES-256-GCM) with a blind index for lookups. Masked by default; reveal needs a permission plus a purpose and is audited.
  - **Never in URLs** (D-SLC-05): search terms go in POST bodies.
  - **Never in the idempotency store:** audited reads use `Idempotent = false`.
  - Never in events, ledger dimensions or rating worksheets (derived age bands only).
- **Money:**
  - `decimal`/`NUMERIC` only (analyser COREINS001 bans float/double); precision loss raises an error; rounding is explicit and comes from MKT rules.
  - Ledgers are append-only and **sealed at their creating transaction** (D-ARC-34): a balance trigger, a seal trigger and app-role regression tests.
  - FIN posts only from `BillingEntryPosted` (D-SLC-12).
- **Time:**
  - Only through `IClock` (COREINS002).
  - Valid time is `validAt` and record time is `knownAt` (D-API-08).
  - A date-form `validAt` means **end of that business day, Europe/Athens** (D-SLC-13).
  - Bitemporal rows are protected by exclusion constraints and by triggers that refuse retroactive record closing.
- **Concurrency:**
  - Gapless numbers come from `INumberingService` inside the caller's transaction.
  - Racing commands return `*-ERR-STALE`, never 500 or "illegal transition".
  - Outbox handlers are idempotent and order-aware per aggregate (D-ARC-26).
- **Regulatory and commercial values:**
  - Only from the PRDs, each with a `legalStatus`; anything not Settled is refused in Production and flagged `provisional` elsewhere (D-REG-01..07).
  - Contradictions stay absent and fail closed, e.g. the levy split (D-REG-06a).
  - Tariffs and UW thresholds are marked **illustrative test data**.
- **Contracts:**
  - Interface-first and pre-release (D-API-06/06a); owners type their own operations; additive changes only during a wave; regenerate with ContractGen and keep `--check` green.
  - MKT is the configuration authority, and the envelope hash equals the payload hash (D-SLC-15).
- **UI:**
  - Design-system components only; Stylelint enforces token-only styling.
  - Greek and English, with empty, loading, error and no-permission states.
  - No float maths on money (BigInt minor units).
  - An Idempotency-Key per user action, reused on retry.
  - Never pass a fresh array literal into DataTable props without memoising. The table now guards against render loops, but don't rely on it.

## 5. Foundations (Phase 1)

| WP | What | Review |
|---|---|---|
| F-1a Scaffold | Host roles api/worker/migrate, 16 module + Contracts projects, analysers, architecture tests, secure DB bootstrap, Bicep, CI/CD | PASS on attempt 3 |
| F-1b Kernel + platform | Money/dates/ids/state machines, IClock, DbSession, outbox (2,380 ev/s), hash-chained audit, idempotency, Problem Details, command pipeline | PASS on attempt 2 |
| F-1c Contracts | 304 event schemas, 1,113 OpenAPI operations, 40 SPIs, generated C# + fakes | PASS |
| F-1d Design system | Aegean tokens, 35/39 components, app shell, Greek formatters, i18n, 1,100+ tests with axe | PASS on attempt 2 |
| F-1e Greek cross-cutting | AFM, ELOT 743, Greek search SQL (ICU `el_gr_ci_ai`), field encryption + blind index + key rotation | PASS on attempt 3 |
| F-1f Spikes + engines | PDF/A + PAdES via Gotenberg, CEL-subset decimal rule engine (883 tests), outbox throughput | PASS on attempt 3 |

## 6. The thin slice (Phase 2): what each package delivered

| WP | Model | Review outcome | Notable |
|---|---|---|---|
| SL-0 Platform wiring + Party | strongest | **FAIL → fixed.** Decrypted P2 was stored in plaintext in `plt.idempotency_record` on reveal; fixed with the `Idempotent` flag | Module pattern, dev sign-in, numbering, compose stack |
| SL-FIX-WEB | Sonnet | Spot-check | Compact numbers independent of the ICU version |
| SL-MKT | Sonnet | PASS (light) | 70/30 levy split removed (D-REG-06a) |
| SL-PFC | Sonnet | PASS (light) | MTPL €1.3M from PRD-02, write-once artefacts |
| SL-RAT-UW | Sonnet | **FAIL → fixed.** The IPT line claimed Settled although the motor class is Verify, and a missing class fell back to a default; now weakest-status-wins and fail-closed | Money hand-checked: 430.00 + 64.51 = 494.51 |
| SL-POL | strongest | **FAIL → fixed.** (1) The app role could retroactively close record periods, a hidden delete; (2) Policy.get lost its status outside the term | Exactly one of 4 concurrent binds wins; numbers stay gapless |
| SL-FIN | strongest | **FAIL → fixed.** Balanced lines could be appended to a posted journal | Became rule D-ARC-34 |
| SL-BIL | strongest | **FAIL → fixed.** (1) The same appendable-ledger hole; (2) concurrent intake could strand a set unbilled | No over-allocation; stub never bound in Production |
| SL-UI | Sonnet | PASS (light) | Screens. Render loops were found later in real Chrome and fixed in DataTable |
| SL-E2E | Sonnet | Integration WP | MKT config authority, provisional tax onto invoices, typed ledger dimensions, quote warnings, E2E-01 API + UI, CI job |

**Every deep first-round review found a real defect**, which justified the policy (D-USR-09). The light reviews found
none that blocked, but **a real browser found two render-loop freezes that jsdom unit tests could not**. Hence the
browser-level E2E and the `alive(page)` checks.

## 7. Key decisions index (see DECISIONS.md for the full text)

- **Programme and process:**
  - D-USR-04..09: lighter reviews; Sonnet by default; deep review on the strongest model only for security, money and temporal code, first round only.
  - D-USR-06/07/08: thin slice, pause rules, Docker.
  - D-PRG-15: builders never spawn sub-agents.
  - D-PRG-17/18: the merge gate is the build + **every** .NET test project + every web gate (including `format:check`), all before pushing.
- **Environment:** D-ARC-29 (npm lock version), D-ARC-30 (native Windows), D-ARC-31 (CodeQL without code scanning), D-ARC-36 (wall-clock test bounds are sanity checks only).
- **Slice rulings:** D-SLC-01..21. Among them:
  - 05: no P2 in URLs;
  - 10: ANNUAL plan;
  - 12: FIN posts only from BIL;
  - 13: as-of semantics;
  - 15: MKT is the configuration authority;
  - 16: one vehicle, one driver;
  - 17: DECLINE stays Draft;
  - 20: underwriter reads invoices;
  - 21: acceptance and follow-ups.
- **Architecture:** D-ARC-34 (ledger sealing), D-ARC-26 (outbox ordering), D-ARC-27 (Money precision), D-CON-08b/c (Job state model).
- **Regulatory:** D-REG-01..07, D-REG-06a (levy split absent); §G lists the open legal questions.

## 8. Known gaps, deferrals and follow-ups

- **From slice acceptance (D-SLC-21):**
  - The dev sign-in Select is intermittently unreliable under remote-driven clicks.
  - "Open policy" after bind was not re-verified by hand.
  - Rows on policy and billing pages open only by double-click.
  - Tables at about 840 px wide scroll sideways and truncate status pills.
  - The policy journal view omits the cash-receipt journal; it needs a billing-account view.
  - A malformed bearer token returns 500 instead of 401.
  - Customers with policies still show PROSPECT.
  - The wizard should require vehicle value when own damage is selected.
- **Platform:**
  - An out-of-range JSON number gives 500 (D-ARC-33).
  - A failed nested idempotent command leaves an InProgress record (D-ARC-35).
  - Ledger tampering raises no security event (D-SLC-18b).
  - The Key Vault key provider and Azure.Identity are deferred (D-SLC-07); outside Development the Host refuses to start with only the local key provider.
- **Slice simplifications:**
  - ANNUAL only, no instalments or down payment.
  - One vehicle and driver; annual terms only (no proration).
  - DECLINE stays Draft, with no decline letter.
  - The fiscal channel is a synchronous stub with placeholder codes (`UNMAPPED-OQ-012`, `STUB-`).
  - Only the IFRS17 book (no Solvency II).
  - EUR only.
  - Product and rating artefacts are seeded, with no authoring or approval UI.
  - Numbering prefixes (P, Q, POL, INV, BA, JNL) are technical defaults (D-SLC-08).
- **Contracts:**
  - Many operations outside the slice are still minimal, and owning WPs must type them.
  - D-CON-34 follow-ups.
  - PolicyBound `accountId` and `producerOfRecord` are optional until accounts and producer-of-record exist (D-SLC-14).
- **Design system (D-FE-16/24):** relationship graph, AI surfaces, pdf.js viewer, DataTable extras; the Vite bundle has one chunk over 500 kB.
- **Scope decisions:** mobile deferred (D-USR-01), OCR deferred (D-USR-03), bancassurance out of P1 (D6), motor RI assumed XoL-only (A-01), migration scenario B.

## 9. Open questions for the user or business

- **Legal opinion D2 (the go-live gate):**
  - IPT liability point and the **motor IPT class** (currently `Verify`).
  - **Auxiliary Fund levy split.** PRD-17 says 4.2/1.8 (70/30); REQ-FIN-190 says 4.5/1.5 (75/25).
  - Stamp-duty rate.
  - Validity dates of the tax and levy values.
  - **myDATA document types/codes** (OQ-012) and the Information Centre channel.
  - 16 unverified statutory clock values; Friendly Settlement limits; retention durations (§G).
- **Business/product:**
  - The real Greek **chart of accounts** (ΕΛΠ) and GL mapping; the slice uses PRD-09 illustrative codes and placeholders.
  - **Policy, invoice and customer number formats.**
  - MTPL limits and indexation; regulatory codes for own damage and windscreen.
  - Instalment plans and down payment; motor RI basis (XoL-only?); card acquirer and banks; aggregators.
  - DPO confirmation of the PII classes on vehicle and driver fields.
  - Glossary sign-off for 118 Greek status labels.
  - Leap-day `ageAt` rule (OQ-ORC-01).

## 10. How the orchestration works

- **Memory files:**
  - `STATUS.md`: current state and per-WP status.
  - `PLAN.md` and `SLICE-PLAN.md`.
  - `DECISIONS.md`: append only; supersede with a new ID; never rewrite.
  - `digests/` and `backlog/`.
  - `briefs/BUILDER-TEMPLATE.md` and `briefs/REVIEWER-TEMPLATE.md`.
- **Tracker:**
  - `backlog/backlog.json` is the source of truth; update it with `python orchestration/tracker/set_status.py <WP> <status> [owner] [blockers;…]`.
  - Push to the live page with ArtifactData `update` on collection `wps` (doc = WP id) or `meta/summary`, passing `if_version`.
- **Work loop per WP:**
  1. Brief from the template.
  2. The builder works in an isolated git worktree (`.claude/worktrees/`, git-ignored). It builds natively, merges main when told, and commits to its own branch only.
  3. A separate reviewer gives PASS/FAIL. Light for routine code; deep (strongest model) on the first round for security, money and temporal code.
  4. On FAIL, the builder is resumed with the defect list, and the fix is re-checked light. Escalate to the user after 3 failures.
  5. Merge `--no-ff`, then run the **full gate** (D-PRG-17/18), then push and watch CI.
  6. On solution or appsettings conflicts, keep both sides.
  7. Tell running builders to merge main.
- **Parallelism:** at most 4 agents. Start the next batch early on top of an unreviewed reference branch when the review's likely findings won't change the pattern being copied (this saved hours in the slice). Code against generated fakes (interface-first); an integration WP wires the real modules at the end.
- **Lessons learned:**
  - Deep reviews pay off on security, money and temporal code. Every one found a real defect, and two modules made the same ledger mistake, which became a rule.
  - Unit tests with mocks miss real-data UI bugs. Test against the real stack in a real browser before calling UI done.
  - Run every test project before merging; a skipped suite turned CI red twice.
  - Keep wall-clock assertions loose; machine load caused flakes.
  - Briefs must not contradict the PRD. The FIN posting source was wrong in the brief, and the builder caught it.
- **Cost and time:**
  - Foundations took about 16–20M agent tokens over about 1.5 days.
  - The slice took about 6–7M sub-agent tokens over about 10–11 wall-clock hours: builders 300k–720k each, deep reviews about 150k–215k, light reviews about 70k–90k.
  - The full MVP is still estimated at weeks of agent time and 100M+ tokens at the current rigour.

## 11. Dev-machine notes

- The user's stack runs as compose project `coreins` with host ports 25000+ (`infra/local/.env`, git-ignored).
- Several unrelated containers run on this machine: never stop them.
- Claude in Chrome: remote-driven clicks on React Aria Select popovers are unreliable. Pick from the hidden native select via form input, or test with Playwright.
- Chrome copies sessionStorage into tabs opened from an existing tab, so a new tab may carry a stale dev token.

## 12. What comes next (the user decides)

1. **Recommended: a second thin slice, claims (E2E-03/04 style).** FNOL → coverage check → reserve → payment → FIN posting. It proves money going out as well as coming in before widening. It touches CLM (new), DOC (minimal), WRK (activities), plus BIL/FIN disbursement.
2. **Widen wave by wave (W1 → W9)** toward the full Motor MVP, finishing each backlog WP the slice touched (D-SLC-02 tracks partial coverage) and then the untouched modules.
3. **Harden what exists:** the D-SLC-21 follow-ups, platform items (D-ARC-33/35), design-system polish, Azure environment and deploy.

**To start any new slice:** write a `SLICE-PLAN`-style table (batches, models, review depth), brief builders from the
template, point them at `docs/module-pattern.md`, and finish with an integration/E2E WP that adds a browser-level E2E
test to CI.
