Runs on Sonnet 5.5 (model: sonnet), D-USR-16.

# Brief SL5-RAT-FALLBACK — activate the source tariff for a fall-back product version (wave 2)

Wave 2, **GATED on the merge of SL3-RAT-PRORATE**, which owns RAT services in slice 3 and adds the 1.1 activation.
Review: light, with the config checklist. Estimate: 1.5 h. Small WP: push your branch and report; the orchestrator
batches it (D-USR-18) unless the change turns out larger than one consumer and one service method.

- **WP / PRD:** SL5-RAT-FALLBACK.
  - Requirements:
    - REQ-PFC-213 (RAT side): the fall-back version must be ratable;
    - REQ-RAT-063: in-term transactions keep the term's pinned artefact, so this WP never changes existing terms;
    - the PRD-02 / PRD-03 binding rule "floating at resolution, pinned for the term".
  - Sources: digest `orchestration/digests/PRD-03.md` (artefact binding, RateActivation); D-SL3-15 (how 1.1 was
    activated).
  - Decisions: D-SL5-09, D-SL3-15.
- **Today:** RAT activations are keyed by product code + product version (`rat` tables indexed on ProductCode,
  ProductVersion, EffectiveFrom). MOTOR-GR 1.0 and 1.1 are activated from seeds.
- **Scope:**
  1. Consume `ProductVersionPublished` (typed by SL5-CONTRACTS-PACKS, published by SL5-PFC-FALLBACK), idempotent on
     `event_id`. When `fallbackOf` is set:
     - find the artefact active for `fallbackOf`;
     - write an activation of **that same artefact hash** for the new version, effective from the new version's
       new-business window start;
     - nothing else: no new artefact, no tariff value.
  2. When the source has no active artefact, record a refusal (log + audit) and leave the version unratable, which
     fails closed at quote time. Never fall back to another artefact.
  3. When `fallbackOf` is null, do nothing (today's behaviour).
- **Depends on / provides:**
  - **Depends on:** SL3-RAT-PRORATE merged; SL5-CONTRACTS-PACKS (event type); SL5-PFC-FALLBACK for the real event.
    Until it merges, test with the generated fake publisher.
  - **Provides:** a ratable MOTOR-GR 1.2 for E2E-12 step 9.
- **Files you own:** `src/CoreIns.Modules.Rating/Events/ProductVersion*` (new), one activation method in the RAT
  activation service, `tests/CoreIns.IntegrationTests/Rating/Fallback/**`.
- **Shared:** `RatingModule.cs` (one DI/consumer registration line, append).
- **Do not touch:** proration and servicing tax (SL3-RAT-PRORATE), seeds of 1.0/1.1, PFC.
- **Tests:**
  - an event with `fallbackOf = 1.0` → 1.2 rates with exactly 1.0's artefact hash, and the worksheet shows it;
  - a replay of the event → one activation;
  - a source without an artefact → no activation, and quoting 1.2 fails closed;
  - existing 1.0/1.1 quotes and terms are unaffected.
- **PITFALLS to self-check (likely):** 10, 12, 21.
- **Workflow:** `briefs/sl5/_COMMON.md` (batched variant): merge `origin/main`, quick gate, push, report.
