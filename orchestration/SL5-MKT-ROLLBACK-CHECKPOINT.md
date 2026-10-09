# SL5-MKT-ROLLBACK source checkpoint � 9 October 2026

Status: **partially validated WIP**. Release compilation and two pure proof tests pass; database gates remain blocked by unavailable Docker. Do not merge or count this package completed.
Branch: codex/sl5-market-rollback. Preserved prior bda60af proof commit and original MarketDbContext WIP in recovery/2026-10-09-market-resume.
Latest main dependencies merged locally: PLT50, RI48, PFC49.

## Implemented source

- Immediate request/preview/decide REST commands through native command pipeline, native PLT approvals, audited transaction and outbox.
- Frozen content covers pack/entity/from/to/kind/reason, parent state, source activation, target digest, maker and principal.
- Authenticated legal entity, active entity ownership, current manifest integrity/source consistency and one entity per country-pack fail closed.
- Native owner-decided approval authority MKT_PACK_ACTIVATION, dimensions pack/legalEntity; illustrative DesignAuthority grant only outside Production.
- Different human checker, principal SoD, native verifyForExecution subject/type/hash/authority, stale parent/source/target refusal.
- Market migration freezes requested facts and execution history, restricts transitions; deferred boolean proof uses PLT-owned database predicate, plus owner and native PLT audit in same transaction.
- Genesis migration leaves historical proof unknown rather than inventing it. Genesis initialization remains permitted against the actual GENESIS state.
- Applied reason/from history is truthful. Window/hash evidence is frozen in persistence and event payload; optional PackActivationView evidence additions deferred until ContractGen can run.

## Stable POL consumer contract

Existing generated PackRolledBackV1 remains unchanged. It always carries packId (registry UUID), pack (code, gr), legalEntity (GR-TEST), activationId, fromVersion/toVersion, reason, resultingHash. Window and AffectedWindow are identical [source activation instant, native checker decision instant); HashesIssued and HashesInWindow are identical ordered hashes for recorded states in that interval whose manifest contains the source pack/version. Event aggregate is Stamp, aggregate id is legal entity code; business keys stampId=entity code and packId=registry UUID. One approved activation yields one outbox event; no POL consumer added here.

REST: POST /api/mkt/v1/packs/rollback; POST /api/mkt/v1/packs/schedule-activation; POST /api/mkt/v1/pack-activations/decide; GET /api/mkt/v1/pack-activations; GET /api/mkt/v1/pack-activations/{id}. Existing typed DTOs unchanged. Request DryRun previews without PLT/activation writes. Activation occurs at native checker decision time only.

## Remaining before review/completion

1. Restore disk capacity; root grants serial native gate slot.
2. Compile Release with no node reuse, -m:2; repair compiler/analyzer errors.
3. Regenerate/compare EF migration snapshot through EF tooling; manual additive migration and snapshot remain unverified.
4. Run Market/Packs and State integration tests, PLT approval/audit/owner tests; native outbox/audit failure-atomicity and app-role guards require execution/probes.
5. Add/run stale parent, principal SoD, rejected approval, tampered proof, exactly-once outbox and frozen window/hash database assertions (API test source currently covers dry run/idempotency/self denial/apply/restore/history/entity/permission/target).
6. ContractGen --check and sample validation; no API contract delta currently introduced.
7. Adversarial security/temporal review and exact-head CI. Recheck main before final PR readiness.

Pitfalls considered: 3/4/5 approval binding and participants; 10 fail closed; 12 full consumer fields; 15 state lock; 17 history; 21 additive contracts; 34 preservation; 40 transaction lock; 41 genesis backcompat; 47 transition guard; 48 owner-decided type. None are claimed proven until gates run.

Source tests map: REQ-MKT-137 hash-dimension test and REQ-MKT-136 full-key diff test passed (2/2). The key assertion now checks all nine shipped treatment keys explicitly. REQ-MKT-137/138 native API rollback workflow, REQ-MKT-003 frozen history, and REQ-MKT-139 immediate execution window remain unvalidated: their Testcontainers fixtures could not connect to Docker.

## Bounded native validation update

- Release integration-project build: zero warnings and errors; .logs/resume-market-proof-build-final.log.
- PackActivationProofTests: 2/2 passed; .logs/resume-market-proof-tests-final.log.
- Earlier Market/Packs attempt could not initialize four database fixtures because Docker daemon pipes were unavailable. This provides no behavioral evidence for those API tests.
- EF regeneration, database security/temporal assertions, ContractGen checks, independent review and exact-head CI remain required.

