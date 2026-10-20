# Regulatory go-live gates

Source: `src/CoreIns.CountryPacks.*` (rule and config values carry a `LegalStatus`), `orchestration/NEXT-STEPS.md` NX-EC-12,
`tests/CoreIns.CountryPacks.Tests`.

## The gate
A pack value is settled only when its `LegalStatus` is `Settled` (or `NotRegulatory`). Values still `Unverified`, `Verify`,
`PendingOpinion` or `Draft` are provisional: fine in dev and test, but they **block Production activation** of a stamp for
that country until legal sign-off changes their status.

Known open items (the pack sources hold the current values): claim clocks (OQ-008), the auxiliary-fund levy split basis and
ceiling (OQ-010, `tax.levy.auxfund.*`), the IPT liability point (OQ-011, `tax.ipt.liability_point`), stamp duty, and the myDATA
fiscal channel (OQ-012; `MyDataStubFiscalChannel` is a stub). Greek friendly-settlement eligibility decisions are always
`Unverified` and provisional. The synthetic CY pack (`src/CoreIns.CountryPacks.CY/CyTreatmentRules.cs`) must never be activated in Production.

## Checklist test (NX-EC-12, written in parallel)
`tests/CoreIns.CountryPacks.Tests` will hold a CI test that lists every value still Unverified or PendingOpinion as an
artefact, asserts that Production activation of the CY pack is refused, and adds structure-only golden tests for the GR levy
lines. Until it merges, review by hand: `grep -rnE "PendingOpinion|Unverified|Verify|Draft" src/CoreIns.CountryPacks.*`.

## Before activating Production for a stamp
1. Take the checklist artefact from the CI run of the release commit. Every line is either resolved or has a written legal opinion attached.
2. Change the pack value and its `LegalStatus` and source reference in one reviewed change; the checklist count drops accordingly.
3. Set the stamp variables `STAMP_LEGAL_ENTITY` and `IMMUTABILITY_RETENTION_DAYS` (from the PLT retention catalogue; no default by design).
4. Record release-manager approval (`Platform.ReleaseManager`), then follow [deploy-rollback.md](deploy-rollback.md).

An open item left in place is an explicit, signed-off exception, never a default.
