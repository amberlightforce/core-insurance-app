# HANDOVER — Greek P&C Core Insurance: Phase 0 + Phase 1 (Foundations)

**Date:** 2026-10-07 · **Repo:** https://github.com/amberlightforce/core-insurance-app (private, branch `main`) ·
**Live tracker:** https://claude.ai/artifact/QwQVP63L5vGPhUskFrAzaD

**Status:** foundations are complete. All six foundation work packages (F-1a … F-1f) passed independent review and are merged; CI is green.
**No product feature has been built yet.** The programme is **paused** at the user's request (D-USR-07). The next step, the thin end-to-end slice, starts only on the user's go-ahead.

This document is for whoever picks the work up next, human or agent. Read it with `orchestration/PLAN.md`, `DECISIONS.md`
(about 140 rulings) and `STATUS.md`. Those three files are the programme's memory.

---

## 1. What exists and what does not

| Area | State |
|---|---|
| Specifications | 18 PRDs + system contract (v1.12, frozen) + Aegean design guide + 2 infra specs, outside the repo in `../core-insurance-prds` and `../core-insurance-infra`. Each is summarised in `orchestration/digests/` |
| Plan | `orchestration/PLAN.md`: inventory, dependency graph, waves W1–W9, shared contracts, conflicts |
| Backlog | `orchestration/backlog/backlog.json` + `BACKLOG.md`: **134 work packages** (6 foundation, 116 feature of which 3 deferred, 12 E2E). Together they cover the **3,711** Motor-MVP Must requirements, each placed in exactly one WP |
| Foundations | Built, reviewed, merged (§3) |
| Feature modules (PTY, PFC, RAT, UW, POL, BIL, CLM, RI, FIN, DOC, CMP, CHN, WRK, DAT, MIG, MKT, PLT) | **Not started.** Each module has empty `CoreIns.Modules.<X>` and `<X>.Contracts` projects with generated contract types, and a schema constant |
| Running system | The Host starts in api, worker and migrate roles. The web app shows the Aegean shell with placeholder module routes. No business screen or API works end to end yet |

## 2. How to run, build and test

**Toolchain:** .NET 10 SDK (10.0.401), Node 24, Docker, Python 3.12, Azure CLI with Bicep. Read `README.md` and `infra/README.md` in the repo.

- **Local stack:** `infra/local/compose.yaml` (PostgreSQL 17.11, Azurite, Gotenberg 8.37, Mailpit, WireMock, Aspire dashboard; every port bound to 127.0.0.1). Copy `.env.example` to `.env`. The `migrate` job runs bootstrap and migrations. **No migration runs at start-up.**
- **Backend:** `dotnet build CoreIns.sln -c Release` (warnings are errors). Tests use xunit.v3 on Microsoft.Testing.Platform: `dotnet test --project tests/<Project>`.
- **Integration tests:** they use Testcontainers PostgreSQL 17 by default. Without Docker, set `COREINS_TEST_POSTGRES` (or `COREINS_TEST_PG` for the Greek search tests) to a superuser connection string; each test class then creates and drops its own database.
- **Front end:** `cd web && npm ci`, then:
  - `npm run dev` (port 5173, proxies `/api` to 5000)
  - `lint`, `lint:css`, `lint:i18n`, `typecheck`, `test`, `build`, `build-storybook`
- **E2E:** `tests/e2e` (Playwright) holds the shell accessibility test today.
- **Contracts:**
  - `python contracts/events/validate.py --require-jsonschema`
  - `python contracts/openapi/validate.py --require-spec-validator`
  - `dotnet run --project tools/CoreIns.ContractGen -- --check` (fails if generated C# is stale)
- **CI:** `.github/workflows/ci.yml` runs the .NET build and tests including Testcontainers integration, the web gates, the Bicep build and Key Vault least-privilege guard, the container image build with a Trivy scan, SBOMs, and the contract checks. `codeql.yml` and Dependabot are also configured. `deploy.yml` is dispatch-only, using OIDC to Azure. It has never been run, and no Azure environment exists yet.
- **Benchmarks:** `tools/CoreIns.Rules.Benchmark` (NFR-UW-001) and `tools/CoreIns.OutboxBenchmark`.
- **npm lock files:** regenerate with the npm version CI uses (`npx npm@11.19.0 install --package-lock-only`). A Windows-generated lock broke `npm ci` in CI for several commits (D-ARC-29).

### Dev machine (Windows 11): native toolchain (D-ARC-30)
Everything runs natively on Windows: .NET SDK 10.0.401, Node 24, Docker Desktop (Testcontainers). The earlier problems (Docker would not start, App Control blocked new DLLs, no SDK) are resolved, and WSL is no longer used. If a native build is ever blocked again, stop and report it.

## 3. Foundations: what was built

| WP | What | Where | Review |
|---|---|---|---|
| F-1a Scaffold | Modular monolith per infra §7. Host with `APP_ROLE` api, worker or migrate and health endpoints. 16 module + Contracts projects. Analysers COREINS001 (no float/double) and COREINS002 (no system clock). Architecture tests (NetArchTest + IL). Secure Azure DB bootstrap: shared idempotent SQL, SCRAM verifiers, per-secret Key Vault RBAC, dedicated bootstrap identity, CI guard script. Bicep for every §6.2 resource. CI/CD | `src/CoreIns.Host`, `infra/`, `.github/`, `tests/CoreIns.ArchitectureTests` | PASS on attempt 3 |
| F-1b Shared kernel + platform | **SharedKernel:** Money (exact, precision-guarded), Currency, Rate, BusinessDate/Instant/DateRange/Bitemporal, 83 UUIDv7 typed ids, business number types, BusinessKeys, LocalizedText, Iban, StateMachine, RFC 8785 canonical JSON. **Platform:** IClock (shiftable test clock), DbSession unit of work shared across module DbContexts. Transactional **outbox**: one table, gap-free per-aggregate sequence, ordered starvation-safe dispatcher with leases, micro-batching (2,380 ev/s measured against a 2,000 target), dead letters, replay, archive. **Audit:** insert-only, hash-chained per day, chain head protected by a SECURITY DEFINER function. **Idempotency:** HTTP + command level. RFC 9457 Problem Details with `/problems/<CODE>` pages. Command pipeline (validation → transaction → idempotency → audit → authority → handler; no MediatR). Authority and configuration interfaces with simple default implementations. Contract state models | `src/CoreIns.SharedKernel`, `src/CoreIns.Platform`, `src/CoreIns.Modules.*.Contracts/StateModels.cs` | PASS on attempt 2 |
| F-1c Contracts | **Events:** envelope + **304** JSON Schemas + `catalog.json` (consumers trimmed to real handlers, lineage business keys). **APIs:** OpenAPI 3.1 for **1,110** operations across 17 modules; the quote, bind, money and claims chains are fully typed; `x-maturity: pre-release`. **SPI catalogue:** `contracts/openapi/spi.md`, 40 SPIs. **C# generated:** 304 event records, 203 in-process interfaces (425 operations), 1,787 DTOs, 400 error constants, plus fakes (sandbox doubles) and a `RecordingEventPublisher` | `contracts/`, `tools/CoreIns.ContractGen`, `src/*/Generated`, `src/CoreIns.Platform.Contracts`, `tests/CoreIns.Testing.Contracts` | Events PASS on attempt 2; APIs PASS on attempt 2; C# PASS (light review) |
| F-1d Design system | Aegean tokens unchanged plus extensions; self-hosted OFL fonts; light and dark themes. **35/39 components**, with 3 partial and 1 deferred (D-FE-24). App shell from the v3 mockup: rail, palette, help, notifications, el/en switch. Patterns: form layout, Wizard, DataTable, split workbench, maker-checker ApprovalPanel, states. Greek formatters: money «1.234,56 €», dates, amount in words, `toGreekUpper`, ELOT Greeklish search. react-i18next + ICU with a key lint. Stylelint gate allows token values only. 1,109 tests, each with axe | `web/src/design-system`, `web/src/app-shell`, `web/src/format`, `web/src/i18n` | PASS on attempt 2 |
| F-1e Greek cross-cutting | SPI interfaces (IdValidator, NameTransliterator, AddressFormatter, TaxCalculator, FiscalDocumentChannel, BureauAdapter, StatutoryClockSet, Geocoder, NumberingScheme). **GR pack:** AFM mod-11, VAT EL, plates, **ELOT 743** with per-name-part search variants, Greek upper-casing and sorting, addresses. **CY stub.** Greek search SQL (ICU `el_gr_ci_ai`, search key function, `greek_unaccent_v1`) with C#↔SQL parity. **DataProtection:** DataClass, AES-256-GCM envelope encryption, replica-safe key rotation (two-step retirement, fail-closed long-transaction guard), Key Vault provider, HMAC blind index | `src/CoreIns.Modules.Market.Contracts/Spi`, `src/CoreIns.CountryPacks.*`, `infra/database/greek-search.sql`, `src/CoreIns.Platform.DataProtection` | PASS on attempt 3 |
| F-1f Spikes and engines | **PDF/A:** Gotenberg Chromium route + in-house .NET finaliser gives PDF/A-3a, PDF/UA-1, byte-reproducible output and a PAdES seal (B-B); chunk documents over about 150 pages (`spikes/gotenberg-pdfa/FINDINGS.md`). **Rule engine:** CEL subset, decimal-only, decision tables (UNIQUE/FIRST/PRIORITY/COLLECT), cost/allocation/depth/time limits, hashes covering schema and limits, 883 tests. **Outbox throughput** proven (see F-1b) | `src/CoreIns.Rules`, `spikes/` | PASS on attempt 3 |

## 4. Architecture and rules every builder must follow

- The infra specs are binding: .NET 10, PostgreSQL 17, EF Core + Dapper, Hangfire, outbox, React 19, Entra ID, Gotenberg, Azure Container Apps, Bicep.
- **Translations of PRD wording (DECISIONS §B):**

  | PRD says | Build it as |
  |---|---|
  | message broker | outbox |
  | workflow engine | state machines + deadline rows + Hangfire recurring scanners |
  | own identity server | Entra ID / External ID |
  | lakehouse | PostgreSQL `rpt_*` schemas |
  | in-house PDF engine | Gotenberg + finaliser |
  | PostgreSQL 18 | PostgreSQL 17 exclusion constraints |
- **Module boundaries:** one schema per module. A module never reads another module's tables, references only other modules' `*.Contracts`, `SharedKernel`, `Platform.*` and `Rules`, and calls other modules in-process through their generated interfaces. Architecture tests enforce all of this.
- **Money:** `decimal`/`NUMERIC` only, rounding always explicit, precision loss raises an error. Calculations are pure functions. Ledgers are append-only. Every lifecycle is an explicit state machine. Every command carries an Idempotency-Key. Events are written in the same transaction as the change. Everything is audited.
- **Contracts are interface-first and pre-release** until their first consumer merges (D-API-06/06a). Owners type their own operations. Names come from the owner PRD (D-API-03). Valid time is `validAt`, transaction time is `knownAt` (D-API-08).
- **Regulatory values come only from the PRDs**, carry a `legalStatus`, and anything not Settled is refused in production (D-REG-01…07). Nothing has been invented. The open legal questions are in DECISIONS §G and must be closed by the commissioned legal opinion (D2) before go-live.
- **UI:** design-system components only, token-only styling enforced by Stylelint, Greek-first with English, every screen has empty, loading and error states.

## 5. Known gaps and deferrals (carried into feature work)

- **Design system (D-FE-16, 24):**
  - Relationship graph (visx).
  - AI agent-plan and streaming surfaces.
  - pdf.js rendering in the document viewer.
  - DataTable extras: saved views, inline edit, drag-reorder.
  - Some microinteractions.
- **Contracts:**
  - 220 minimal operations and 65 operation families need completing by their owning WPs.
  - Lifecycle-chain operations get typed by W2-CMP, W5-BIL and W6-POL (D-API-06a).
  - 15 events have minimal payloads.
  - D-CON-34 lists the C# generation follow-ups.
- **Platform:**
  - Authority grants and the configuration resolver are simple defaults. The full models come in W1-PLT and W1-MKT.
  - MSAL / OIDC sign-in is deferred to W1-PLT (D-FE-10).
  - Retention durations are unset (D-REG-07).
  - Identity is re-scoped to Entra ID (D-ARC-03).
- **Data protection:**
  - The Host still needs to register a durable data-key store, the Key Vault provider, the legal-entity catalogue and the retirement scans.
  - Operating constraints for key rotation are in D-ARC-23a.
- **Greek pack:** missing postcode list (D-ARC-22), the full ELOT 743 table (OI-MKT-18), and ID card, passport and plate formats. No tax, levy or clock values have been entered.
- **Documents:** the chunk merge for 500+ page documents, PAdES long-term validation levels (B-T, B-LT, B-LTA), and the real seal key.
- **Scope decisions:**
  - Mobile apps are deferred (D-USR-01).
  - OCR is deferred (D-USR-03).
  - Bancassurance is out of P1 (D6).
  - Motor RI is assumed to be XoL-only (A-01, to be confirmed).
  - Migration is planned as scenario B.

## 6. What comes next (when the user says go)

**Agreed approach (D-USR-04…06):**
1. **Thin end-to-end slice:** one motor product, quote → bind → invoice → payment → journal (the happy path of E2E-01). It cuts through MKT config/pack, PFC product, RAT rating, UW evaluate, PTY party/producer, POL quote/bind, BIL charges/invoice/payment, CMP fiscal stub, FIN posting and CHN or staff UI. Each module implements only the requirements the slice needs, as WP slices drawn from the backlog. Then widen wave by wave (W1 → W9).
2. **Lighter reviews:** one pass, blocking only on blockers and majors. Deep adversarial review only for money/ledger, temporal and security code.
3. **Model by risk:** Sonnet for routine WPs and reviews; the strongest model for POL bitemporal, BIL ledger, FIN posting, RAT, and security.
4. Use the builder brief template (in the original orchestration instructions and reflected in `orchestration/briefs/`) and the reviewer template `orchestration/briefs/REVIEWER-TEMPLATE.md`. **Builders must not spawn sub-agents** (D-PRG-15). Run at most 4 agents at a time.

**First things a W1/slice builder needs:** W1-PLT (identity on Entra, authority grants, numbering, calendars) and W1-MKT (six-layer configuration, GR pack values with legalStatus, SPI binding). Remember these are the platform that every module builds on.

## 7. How the orchestration works

- **Memory:** `orchestration/STATUS.md` (current state, blockers, pending user requests), `PLAN.md`, `DECISIONS.md` (append rulings; never rewrite history; supersede with a new ID), `digests/` (the summarised specs), `backlog/`.
- **Tracker:**
  - `backlog/backlog.json` is the source of truth.
  - `python orchestration/tracker/set_status.py <WP> <status> [owner] [blockers;…]` updates it.
  - Push the change to the live page with ArtifactData `update` on collection `wps`, document = WP id, passing the `if_version` of the document as last read.
  - `tracker/export_tracker.py` re-seeds everything.
- **Work loop:**
  1. A builder works in an isolated git worktree under `.claude/worktrees/`, which is git-ignored.
  2. A separate reviewer passes or fails it.
  3. On FAIL, the builder is resumed with the defect list. After 3 failures, escalate to the user.
  4. Merge into `main` with `--no-ff`, push, and watch CI.
  5. `CoreIns.sln` often conflicts on merges. Resolve by keeping both sides' project entries and checking project counts.
- **Cost reality:** Phase 0 + Phase 1 consumed roughly 16–20M agent tokens. Deep reviews and fix rounds made up about a third of the foundation cost. They found real defects (data-loss in key rotation, event reordering, a security hole in the database bootstrap), but the lighter policy above is now in force.

## 8. Open questions for the user or business
- **Legal opinion D2, the go-live gate:**
  - IPT liability point; levy split (PRD-17 4.2/1.8 vs REQ-FIN-190 4.5/1.5); stamp duty rate.
  - myDATA document types and codes; Information Centre channel and format.
  - 16 unverified clock values; Friendly Settlement limits; retention durations.
  - All of these are listed in DECISIONS §G.
- **Product choices still open:** motor RI XoL-only confirmation; migration scenario B board confirmation; card acquirer and banks; aggregators.
- **Glossary sign-off:** 118 Greek status labels marked `glossary: pending` (`web/src/design-system/README.md`).
- **Leap-day birthdays:** does `ageAt` count 29 February as 28 Feb or 1 Mar in common years? (OQ-ORC-01)
