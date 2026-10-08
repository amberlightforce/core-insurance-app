Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-MKT-STATE — persisted configuration states, pack-version registry, resolve by any hash (wave 2)

Wave 2, **GATED on the merge of SL3-MKT-TREATMENT**, which owns `Modules.Market` services and
`CountryPacks.GR/Configuration` in slice 3. Do not start before the orchestrator confirms it. Review: **deep
(config/temporal)**. Estimate: 4.5 h. Meaningful PR.

- **WP / PRD:** SL5-MKT-STATE.
  - Requirements:
    - REQ-MKT-003 (registry subset), REQ-MKT-125 (manifest subset: pack id, semver, content digest), REQ-MKT-129
      (semver meaning);
    - REQ-MKT-046 and REQ-MKT-047 (hash over the canonical state manifest, stamped on facts);
    - REQ-MKT-048 (rebuild any past state; never purge a referenced one);
    - REQ-MKT-050 (atomic switch; nothing starting 1 s after an activation sees the old hash);
    - REQ-MKT-051 (unit-of-work pinning);
    - properties P-06 (the hash changes if and only if the manifest changes) and P-07 (`resolve(hash)` is unchanged
      by later activations).
  - Sources: `../core-insurance-prds/PRD-17-multi-market-country-packs.md` §7.1 (ConfigState, Pack/PackVersion,
    PackActivation), §9; digest `orchestration/digests/PRD-17.md` §3, §7.1.
  - Decisions: D-SL5-06, D-SL5-07, D-REG-01..07, D-SLC-09, D-SL3-05/06; PITFALLS 36–37 (from the SL3-MKT-TREATMENT
    review).
- **Today:** `ConfigurationCatalogue` is built once at startup from `IPackConfigurationSource`s, with one hash.
  `ConfigurationEngine` refuses any other hash ("history arrives with W1-MKT-01") and any `knownAt` before
  `ActivatedAt`. `GrPackConfiguration.Version = "0.1.0"`, but its values now include the treatment rows from
  SL3-MKT-TREATMENT.
- **Scope:**
  1. **Pack versions as shipped data (D-SL5-07).** Let a pack source expose **several versions**:
     - GR **0.1.0** = exactly the GR values as shipped before SL3-MKT-TREATMENT, i.e. without
       `GrTreatmentRules.Values`. Use `git log -p` on `GrPackConfiguration.cs` to confirm nothing else changed;
     - GR **0.2.0** = 0.1.0 + `GrTreatmentRules.Values`;
     - CY stub: 0.1.0 without and 0.2.0 with its treatment rows (synthetic, as today);
     - core defaults stay core (not a pack).
     - **No value is added, removed or changed.** Each version's content digest = SHA-256 over the RFC 8785 canonical
       JSON of its values.
  2. **Schema (MKT migration owner, wave 2):**
     - `mkt.pack_version` (pack_id, version, content_digest, values jsonb, status Published, registered_at; unique
       pack+version; append-only);
     - `mkt.config_state` (hash PK, parent_hash, manifest jsonb {packVersions, coreDigest, …}, activated_at, cause
       GENESIS / PACK_ACTIVATION / PACK_ROLLBACK, cause_ref; append-only);
     - `mkt.pack_activation` (id, legal_entity_id, pack_id, version, kind ACTIVATE/ROLLBACK, status, requested_by,
       decided_by, approval_request_id, activated_at, resulting_hash, supersedes_id). SL5-MKT-ROLLBACK writes it; you
       create the table and the genesis rows.
     - App role: SELECT/INSERT on the state and version tables, no UPDATE/DELETE. Prove it with a test **as the app
       role** (PITFALLS 8, 17).
  3. **Genesis.** At startup, under an advisory lock so api and worker do not race:
     - register the shipped versions, idempotent by digest. A digest mismatch for an existing pack+version is a
       startup failure: a version can never change;
     - if there is no state, create the genesis state from the **newest shipped version of each pack** (GR 0.2.0),
       with an ACTIVATE activation per entity.
     - Today's behaviour for every other slice is unchanged. The genesis hash may differ from today's hash; facts from
       before slice 5 keep their old hash, which stays `CFG-HASH-UNKNOWN` (D-SL5-06). Report the change.
  4. **State-aware reads.**
     - `ConfigurationEngine.Resolve`, rounding, `TaxCalculatorService.TreatmentAsync` and `calculate` take the state
       from the request's `configurationHash` (any recorded state) or else the current state.
     - The catalogue becomes a per-state, immutable, in-memory object built from the state manifest + pack versions,
       cached by hash.
     - `knownAt` resolves the state that was current at that instant (`activated_at ≤ knownAt`, latest).
     - `CFG-HASH-UNKNOWN` is kept only for hashes that were never recorded.
  5. **Current state.** `currentHash` reads the latest state from the database, cached for at most **1 s** in each
     process (api and worker), so an activation is seen everywhere within REQ-MKT-050's bound. Keep the existing
     unit-of-work pinning: a command that captured H1 completes under H1 (REQ-MKT-051). Add a test where the state
     changes in the middle of a command.
  6. `mkt.Pack.list` and `mkt.Pack.get` (typed by SL5-CONTRACTS-PACKS) from the registry and activations. Permissions:
     Platform.ReleaseManager, Platform.DesignAuthority and Platform.Admin, in `permissions/mkt.json`.
  7. **New business under 0.1.0 (open question Q2).** Write an integration test that switches the current state to
     one with GR 0.1.0, using a test-only helper (not an API), then quotes and binds a MOTOR-GR policy.
     - Expected: it works. New-business rating uses `TaxCalculator.calculate`, and treatment is used only on the
       servicing path.
     - If it fails because a `NEW_BUSINESS` treatment row is required, **stop and report**. Do not add rows.
- **Depends on / provides:**
  - **Depends on:** SL3-MKT-TREATMENT merged; SL5-CONTRACTS-PACKS (types).
  - **Provides:** the states that SL5-MKT-ROLLBACK writes and that POL/RAT/E2E read by hash.
- **Files you own:** `src/CoreIns.Modules.Market/**` (**MKT migration owner, wave 2**),
  `src/CoreIns.CountryPacks.GR/Configuration/**` and `src/CoreIns.CountryPacks.CY/**` (version split only; rows
  unchanged), `src/CoreIns.Host/Hosting/CountryPackBinding.cs` (registration only), `src/CoreIns.Host/permissions/mkt.json`,
  `tests/CoreIns.IntegrationTests/Market/State/**`, `tests/CoreIns.CountryPacks.Tests` (new classes).
- **Do not touch:** `CountryPacks.GR/Fiscal/**` (CMP), POL, RAT, PFC.
- **Tests:**
  - **P-06:** the same manifest in any insertion order gives the same hash, and any change gives a different one.
  - **P-07:** `resolve(H2)` is byte-identical before and after a new state is written.
  - Genesis is idempotent across two concurrent hosts (both start; one genesis). A shipped-version digest mismatch
    fails startup.
  - `treatment(CANCELLATION, Policyholder)`: under the GR 0.2.0 state → `GR-TRT-IPT-CANCEL-POLICYHOLDER`, under the
    0.1.0 state → `RULE_MISSING`.
  - The production gate still allows only `Settled` (PITFALLS 36).
  - The 1 s cache bound; a mid-command activation keeps H1.
  - The app role cannot UPDATE or DELETE states or versions.
  - The new-business-under-0.1.0 test from scope item 7.
- **Reviewer focus (deep, config/temporal):**
  - Can any path resolve a value from the wrong state: a stale cache past 1 s, a `knownAt` before genesis, a
    half-built catalogue?
  - Can a state or version be rewritten?
  - Does the genesis race?
  - Is the hash canonical (JCS, decimals as strings)?
  - Do the GR 0.1.0/0.2.0 contents equal the historical content exactly?
- **PITFALLS to self-check (likely):** 8, 10, 13, 15, 17, 21, 24, 36, 37.
- **Workflow:** `briefs/sl5/_COMMON.md`. PR title:
  `SL5-MKT-STATE: persisted configuration states, pack versions, resolve by hash`. Do not merge.
