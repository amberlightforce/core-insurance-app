# HANDOVER — Greek P&C Core Insurance

> **Verified takeover checkpoint — 2026-10-09:** Claims #37, refunds #39/#34, servicing #28/#46, tax identity #44, and pack history/roles #43 have merged after all required checks. Expanded real servicing acceptance now passes all 8 tests; integration #45 awaits final CI. Reinsurance #47/#48 and product fallback #49 are in independent review with corrections under test. See [CODEX-TAKEOVER.md](CODEX-TAKEOVER.md) for current evidence, ownership, dependencies and remaining work. Older paragraphs below are historical.


**Updated:** 2026-10-08 · **Repo:** https://github.com/amberlightforce/core-insurance-app (private, branch `main`) ·
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD

## 0. Status in one paragraph

Foundations (Phase 1), **slice 1** (Phase 2, E2E-01) and **slice 2 — claims** (Phase 3, E2E-02a) are done.
- **Slice 1:** one motor product is quoted, rated, underwritten and bound; Billing invoices it (with a **stub** myDATA
  document), takes an exact payment, and Finance posts **balanced journals**.
- **Slice 2:** a claims handler reports an own-damage claim; cover is verified on the policy as it stood at the loss date;
  reserves and payments go through **authority limits and maker ≠ checker approvals**; the payment is paid out through a
  BIL disbursement (stub screening, VoP and bank); Finance posts claim journals; the claim closes.
- **Also added on 2026-10-08:** underwriting referral decisions (a senior underwriter approves a referred quote so it can
  bind, D-UW-01), a Development-only `superuser` with every role, and the **Aegean restyle** of the whole staff UI to
  match the mockup (D-USR-11/13), including the claim file screen.

All of it runs locally with `docker compose`, proven by automated **E2E-01 and E2E-02a** at API and browser level.
**GitHub CI is not running** (Actions blocked by account billing); the user chose to gate everything locally
(D-USR-12) — see §2. About 15–20% of the 3,711 Must requirements are covered, most partially.

Read this with `orchestration/STATUS.md`, `PLAN.md`, `SLICE-PLAN.md`, `SLICE-PLAN-2.md` and `DECISIONS.md` (about
200 rulings). Those files are the programme's memory.

---

## 1. What exists

| Area | State |
|---|---|
| Specifications | Outside the repo, in `../core-insurance-prds` and `../core-insurance-infra`: 18 PRDs, system contract v1.12 (frozen), Aegean design guide, 2 infra specs. Summaries are in `orchestration/digests/` |
| Plan and backlog | `PLAN.md` (waves W1–W9); `backlog/backlog.json` + `BACKLOG.md` with 134 work packages that hold all 3,711 Must requirements; `SLICE-PLAN.md` for the slice |
| Foundations | Built, reviewed, merged (§5) |
| Slice modules | Party, Market, Product, Rating, Underwriting, Policy, Billing, Compliance (fiscal stub only), Finance, **Claims**. Each covers only what E2E-01/E2E-02a need (§6) |
| Staff UI | Greek-first React app in the **Aegean mockup style** (ambient canvas, landing/record pages, role-based home, avatar menu with theme/density/language). Screens: home; customers; 7-step quote wizard with referral explanations; policy view; underwriting referrals; billing account/invoice/payment; journals; claims home, FNOL, claim file (stage strip, money card, history, Financials tab with reserve/payment builder), approvals inbox |
| Not started | Reinsurance, Documents, Work management, Channels/portals, Data/reporting, Migration; renewals, changes, cancellations. Their projects exist with generated contract types only |
| Tests | 10 .NET test projects (about 1,900 tests, 441 Testcontainers integration tests); about 1,270 web tests (Vitest + axe); Playwright E2E-01 and E2E-02a (API + UI) |

### Modules and schemas

| Module | Schema | Slice scope |
|---|---|---|
| Platform | `plt` | Command pipeline, outbox, audit, idempotency, numbering, permissions, data keys, dev sign-in |
| Party | `pty` | Person create/get/search (accent-insensitive, ELOT 743 Latin forms, AFM), masked P2 data with an audited reveal, intermediaries, producer codes |
| Market | `mkt` | Legal entity GR-TEST, configuration resolver (configuration authority, D-SLC-15), rounding, GR pack values from the PRDs with legalStatus |
| Product | `pfc` | Motor Private Car (MOTOR-GR 1.0): MTPL + own damage + windscreen, MOTOR-RISK question set, charge types, write-once versions resolved by date |
| Rating | `rat` | Decision-table rating on the shared rule engine (illustrative tariff, D-SLC-04), IPT from MKT, content-addressed worksheets |
| Underwriting | `uw` | PRE_QUOTE / PRE_BIND rule sets: accept, refer or decline (illustrative thresholds); issue list and **decide** (approve/reject with reason), SoD over every job participant, fingerprinted approvals (D-UW-01) |
| Policy | `pol` | Submission → draft → quote → bind; policy/term/transaction/segment with bitemporal storage, exclusion constraints and append-only triggers; gapless policy numbers |
| Billing | `bil` | Account, charge intake, ANNUAL invoice (gapless number), payment, allocation, sealed sub-ledger, `BillingEntryPosted` |
| Compliance | `cmp` | Fiscal document request through the GR **stub** channel (never bound in Production) |
| Finance | `fin` | Posting rules as data (rule set v2), intake from `BillingEntryPosted` and CLM `ReserveChanged`/`PaymentIssued` (D-SLC-12, D-SL2-08), balanced append-only sealed journals, gapless journal numbers, journals by claim |
| Platform (slice 2) | `plt` | Approval requests (maker ≠ checker incl. the maker's principal, content hash, in-process request only, `verifyForExecution` binds type + subject), CLM authority types with illustrative limits (D-SL2-03/13) |
| Policy (slice 2) | `pol` | `pol.Snapshot.get` at an instant (immutable ref, byte-identical re-read), policy search by number/insured (POST) |
| Billing (slice 2) | `bil` | Payee accounts (IBAN encrypted + blind index, masked), claim-payment disbursements (duplicate key per claim, screening fail-closed, PLT approval verified), stub VoP/bank, sealed disbursement entries |
| Claims | `clm` | FNOL (staff), claim/exposure/claimant/incident, cover on the POL snapshot, gapless claim numbers, search, close guard; reserve lines, sealed financial transactions, transaction sets with authority on exposure totals and cumulative paid, one PLT approval per referred authority, derived balances, payments via BIL, `ReserveChanged`/`PaymentIssued` |

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
  | `uwsenior` | Staff.Underwriter + Staff.UnderwritingManager | Decides underwriting referrals (not on jobs they took part in) |
  | `billing` | Staff.Billing | Accounts, invoices, payments |
  | `finance` | Staff.Finance | Journals |
  | `claims` | Staff.ClaimsHandler | FNOL, exposures, reserves and payments within EUR 5,000 (illustrative), close |
  | `claimsmgr` | Staff.ClaimsManager | Approvals inbox, limits up to EUR 50,000 (illustrative) |
  | `admin` | Platform.Admin | Product import and the rest |
  | `superuser` | every role above | Everything for demos; still cannot approve its own requests |

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

### Automated E2E-02a (claims happy path, API and UI)
```bash
tests/e2e/run-e2e02.sh          # fresh "coreins-e2e02" stack (ports 26500+, image coreins-host:e2e02), then down -v
python infra/local/seed-demo.py http://127.0.0.1:5000 --claims   # demo data plus one CLOSED paid claim and one OPEN claim with a reserve
```
- **Path:** policy in force (term starts seconds ahead, loss dated inside it) → FNOL → payee account → reserves (1,200.00 approved at submit; +5,300.00 referred, self-approval refused, manager approves) → FINAL payment 6,200.00 (release of 300.00 proposed) → BIL CLEARED → FIN journals → close.
- **Asserts (integer cents):** open reserve 0, paid = incurred = 620000, every claim journal balanced, GL-5110 net debit 620000, GL-2210/2510/2530 net 0, GL-1110 −620000, disbursement `sourceId` = claim payment id, no IBAN in any response.

- **UI spec** (`e2e02-ui.spec.ts`): the same journey through the claims screens as `claims` and `claimsmgr` in two browser contexts, asserting the IBAN never appears in a request URL.

### Local merge gate (replaces CI while Actions is blocked, D-USR-12)
Before every push: `dotnet build CoreIns.sln -c Release`, **every** .NET test project, `ContractGen -- --check`, all web
gates (use `npx vitest run --maxWorkers=2` when the machine is busy), and for runtime-affecting merges
`tests/e2e/run-e2e01.sh` and `tests/e2e/run-e2e02.sh`. Not covered locally: CodeQL, Trivy, Bicep lint.

### CI (`.github/workflows/`) — currently not running (account billing)
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
6. **Referrals:** quote a 1991 car (or a driver aged 18–20, or a value over 100,000) as `underwriter` → bind shows why it is referred → as `uwsenior` (second browser profile) Policies → Underwriting referrals → approve with a reason → `underwriter` checks again and binds.
7. **Claims** (as `claims`; seed with `seed-demo.py --claims`): Claims → New claim → find the policy → loss inside the term → claim file (stage strip, exposures, money card, history) → Financials tab: capture payee (IBAN GR1601101250000000012300695), reserve 1,200 (approved at once), reserve +5,300 (needs approval) → as `claimsmgr` approve in the inbox → FINAL payment 6,200 (system proposes the −300 release) → manager approves → payment cleared → close. Finance shows the claim's journals.

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

### Slice 2 — claims (Phase 3, `SLICE-PLAN-2.md`)

| WP | Model | Review outcome | Notable |
|---|---|---|---|
| SL2-POL-SNAP | Sonnet | deep **FAIL → fixed**: a forged snapshot ref with a future knownAt bypassed the check | Deferrals D-SL2-09 (knownAt race, policy row not bitemporal) |
| SL2-PLT | strongest | deep **FAIL → fixed**: HTTP request let the maker set authority and supersede a module's request; an AI maker's principal could approve | Request in-process only; verify binds type + subject |
| SL2-BIL-DISB | strongest | deep **FAIL → fixed**: duplicate key never fired (double payment) | `CLM_CLAIM_PAYMENT`; first account without cooling-off (D-SL2-10) |
| SL2-CLM-CORE | strongest | deep **FAIL → fixed**: did not compile against the merged POL contract | P1 free text encrypted at rest (D-SL2-11) |
| SL2-FIN-CLM | strongest | deep **PASS first round** | Fail-closed follow-ups D-SL2-12 |
| SL2-CLM-MONEY | strongest | deep **FAIL → fixed**: split reserves escaped DENY; no cumulative payment check; mixed set checked one authority | Authority on exposure totals and cumulative paid; one approval per referred authority (D-SL2-13) |
| SL2-UI-CLM | Sonnet | light + live browser walk **PASS** (two rounds) | The walk found 5 real bugs (wrong cover code etc.) |
| SL2-E2E | Sonnet | integration | API + UI specs; found two test races, no product regression |
| Side: UW referral decisions | strongest | deep **FAIL → fixed**: job creator could approve; rejection laundering via REST evaluate | D-UW-01, user `uwsenior` |
| Side: SL-UX restyle | strongest | user visual acceptance (D-USR-11/13) | Whole UI to the Aegean mockup, focus ring, claim file |
| Side: superuser, 403 banner, wizard errors | Sonnet | light | Dev-only all-roles user; plain errors |

**Six of seven deep first-round reviews found a real defect again**, mostly in authority/SoD. Pattern for future WPs:
authority must be checked on **aggregates** (totals, cumulative), never per line, and every approval must be bound to
its exact subject, type and content.

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
- **Slice 2 and 2026-10-08:** D-USR-10 (claims slice), D-SL2-01..13 (scope, illustrative limits and categories, stubs, EUR only, posting sources, review rulings, authority dimensions), D-UW-01 (referral decisions), D-USR-11..14 (mockup is binding; CI skipped, local gate; restyle accepted; up to 6 agents), D-ARC-37 (PII-absence assertions).

## 8. Known gaps, deferrals and follow-ups

- **From slice 2:**
  - Out of scope (D-SL2-01): MTPL bodily injury and statutory offer clocks, fiscal settlement receipt, recoveries, RI, Friendly Settlement, letters, WRK activities, fraud, cat events, vendors, deductibles.
  - Payment holds, reissue and void (D-SL2-13); leftover PLT inbox items after a set is rejected need in-process withdrawal.
  - D-SL2-09 POL knownAt race and policy-row versioning must be solved before endorsements.
  - Claims UI gaps: no "transaction sets of a claim" read, dry run returns no authority checks, approval `diff` untyped, close-guard detail not in `errors[]`.
  - UW: per-rule sensitive inputs and tolerances (REQ-UW-091/037), UWApproval entity; the referral **workbench** (mockup «Ανάληψη κινδύνου») was started then paused by the user (branch `worktree-agent-ac9c66400c2402b05`, WIP).
  - Home dashboard is thin for some roles: no unfiltered lists for policies/parties/claims, no activity feed or KPI time series.

- **From slice acceptance (D-SLC-21):**
  - The dev sign-in Select is intermittently unreliable under remote-driven clicks.
  - "Open policy" after bind was not re-verified by hand.
  - Rows on policy and billing pages open only by double-click.
  - Tables at about 840 px wide scroll sideways and truncate status pills.
  - The policy journal view omits the cash-receipt journal; it needs a billing-account view.
  - A malformed bearer token returns 500 instead of 401.
  - Customers with policies still show PROSPECT.
  - The wizard should require vehicle value when own damage is selected (being fixed 2026-10-08 with plain rating-error messages).
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
  5. Merge `--no-ff` **in the orchestrator worktree** `.claude/worktrees/orchestrator` (branch main; never in the shared checkout, which another session uses), then run the **local gate** (§2), then push, then rebuild the user's `coreins` stack.
  6. On solution or appsettings conflicts, keep both sides.
  7. Tell running builders to merge main.
  8. UI work: side-by-side screenshots against the mockup are part of the review (D-USR-11); share them with the user as FYI, do not wait for approval (the user wants the work to keep moving).
- **Parallelism:** at most 6 agents (D-USR-14). Start the next batch early on top of an unreviewed reference branch when the review's likely findings won't change the pattern being copied (this saved hours in the slice). Code against generated fakes (interface-first); an integration WP wires the real modules at the end.
- **Lessons learned:**
  - Deep reviews pay off on security, money and temporal code. Every one found a real defect, and two modules made the same ledger mistake, which became a rule.
  - Unit tests with mocks miss real-data UI bugs. Test against the real stack in a real browser before calling UI done.
  - Run every test project before merging; a skipped suite turned CI red twice.
  - Keep wall-clock assertions loose; machine load caused flakes.
  - Briefs must not contradict the PRD. The FIN posting source was wrong in the brief, and the builder caught it.
  - (Slice 2) Visual fidelity must be reviewed explicitly; behaviour/a11y reviews let a plain UI through for a whole slice.
  - (Slice 2) Authority and SoD are the most common deep-review findings: check aggregates, bind approvals to subject + type + hash, collect every job participant.
  - (Slice 2) Builders must commit early: another session deleted worktrees and uncommitted work was lost twice.
  - (Slice 2) E2E waits must wait for the complete expected set of journals, not the first account seen.
  - (Slice 2) On this laptop (Core Ultra 7 165U, 14 threads) 4–6 agents saturate the CPU; run vitest with `--maxWorkers=2`, tear down isolated stacks promptly, watch disk space.
- **Cost and time:**
  - Foundations took about 16–20M agent tokens over about 1.5 days.
  - The slice took about 6–7M sub-agent tokens over about 10–11 wall-clock hours: builders 300k–720k each, deep reviews about 150k–215k, light reviews about 70k–90k.
  - The full MVP is still estimated at weeks of agent time and 100M+ tokens at the current rigour.

## 11. Dev-machine notes

- The user's stack runs as compose project `coreins` with host ports 25000+ (`infra/local/.env`, git-ignored). Rebuild it from the orchestrator worktree after every pushed merge (the user wants Docker current).
- The main checkout `core-insurance-app/` is shared with another session ("training portal", untracked `training-portal/` folder). Never switch branches there and never delete `.claude/worktrees`.
- Several unrelated containers and other sessions run on this machine: never stop them.
- Claude in Chrome: remote-driven clicks on React Aria Select popovers are unreliable. Pick from the hidden native select via form input, or test with Playwright.
- Chrome copies sessionStorage into tabs opened from an existing tab, so a new tab may carry a stale dev token.

## 12. What comes next (the user decides)

1. **Recommended: slice 3 — W6 servicing** (endorsement, cancellation with refund and fiscal credit note E2E-03, renewal E2E-04, non-payment lapse E2E-07, distance withdrawal E2E-08). Without these it is not a sellable motor product. It must first solve D-SL2-09 (POL knownAt race, policy-row versioning). A planning agent was started and stopped by the user; restart it when the user says so.
2. **Underwriting referral workbench** (mockup «Ανάληψη κινδύνου»), paused WIP branch exists.
3. **Widen wave by wave (W1 → W9)**; estimate on 2026-10-08: about 180–250 agent wall-clock hours (3–5 weeks on this laptop) and 120–180M tokens to a complete Motor MVP, plus the external items in §9.
4. **Harden what exists:** D-SLC-21 follow-ups, platform items (D-ARC-33/35), Azure environment and deploy; restore GitHub CI once billing is fixed (or the repo is made public — user's decision).

**To start any new slice:** write a `SLICE-PLAN`-style table (batches, models, review depth), brief builders from the
template, point them at `docs/module-pattern.md`, and finish with an integration/E2E WP that adds a browser-level E2E
test to CI.

