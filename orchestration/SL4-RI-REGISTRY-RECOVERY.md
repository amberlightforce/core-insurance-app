# SL4-RI-REGISTRY recovery validation — 9 October 2026

Recovered branch: `worktree-agent-a1fd0c774fb4c3051`, based on main `c85a424`.
Preserved implementation checkpoints: `d3f7aba`, `da42709`, `5b9ad17`; list summaries `ac89a41`;
PLT approval evidence and authoritative editors `e4d222b`.

The registry implements programme-less EUR per-risk XoL contracts, append-only content revisions,
gapless entity-scoped numbering, typed create/update/submit/approve/get/list/applicable APIs,
PLT maker-checker approvals, and worker activation/expiry through the transactional pipeline.
List items include optional layers from the current content revision in one batched page read.

Independent backend/UI integration review found two additional blockers. Scope codes now use the
shared Code identifier shape, accepting the real PFC `MOTOR-GR` / `OWN-DAMAGE` catalogue vocabulary.
Layer validation requires each numbered layer's attachment to equal the previous attachment plus
limit exactly; gaps and overlaps are refused on create, edit and submission, because this slice
does not expose an explicit non-contiguous declaration (REQ-RI-037).
The previously unreleased registry hash format now serializes sorted scope-code arrays as JSON,
so configured codes containing commas cannot make different product/coverage scopes share a hash.
The other frozen treaty, layer, clause and participant facts remain in the same deterministic
hash calculation. No already released main module hash format is changed.

Root authorized the narrow ownership exception for `src/CoreIns.Host/permissions/pfc.json` and
`pty.json`: existing read-grant arrays add only RI accountant/manager roles and preserve all prior
roles. Host configuration requires one file per operation, so adding duplicate keys in `ri.json`
would fail startup. No party/product write permissions are added. Exact-role HTTP regressions
resolve/read the real catalogue, search/read a reinsurer, create a real-code treaty as accountant,
and prove product import and party creation remain forbidden.

## Approval producer trust boundary

RI creates requests in process and supplies the real contract participants as authoritative editor keys.
PLT freezes those keys with the subject, approval type, content hash and authority dimensions;
later requests inherit existing participants. RI and the generic PLT inbox refuse makers, editors,
submitters and a delegated checker's participant principal. RI verifies execution against the
stored subject/type/hash and recomputes the content hash before activation.

The deferred RI database guard calls a narrowly scoped PLT-owned `SECURITY DEFINER` predicate,
`plt.ri_contract_approval_verified`, to require a matching approved request and successful
PLT decision audit with the same checker and authority check, including entity, contract type,
hash and participant/principal restrictions. Public execution is revoked; the app receives only
the specific function grant. It checks consistency of persisted decision evidence and refuses
an RI-only writer that attempts to seal a version without that evidence.

This predicate trusts PLT's approval-request and audit producers. The existing shared app credential
can write pending approval decisions and insert audit records; matching rows alone cannot prove
that an independent trusted producer generated them if that credential is compromised. These
guards are not an attestation system or isolation from arbitrary writes across all PLT producer
tables. Credential separation and stronger producer authentication remain platform work outside
this recovery scope. No claim of protection against wholesale shared-app credential compromise
is made.

## Requirement traceability

Tests are in `tests/CoreIns.IntegrationTests/Reinsurance/Registry/` unless stated otherwise.

| Requirement / behavior | Evidence |
|---|---|
| REQ-RI-001 registry APIs, permissions and entity isolation | `ContractLifecycleTests.REQ_RI_001_Permissions_get_list_and_the_legal_entity_boundary`; temporal applicable test |
| REQ-RI-030/031/032 programme-less XoL; REQ-RI-231 identity/year | Lifecycle create/submit/approve test; `ContractContentTests.D_SL4_04_Only_XoL_per_risk_in_EUR_with_realised_only_inuring` |
| REQ-RI-037/038 validation; EUR only | Invalid-treaty and draft-edit lifecycle tests; content rules tests |
| REQ-RI-037 adjacent layers, gaps and overlaps | `REQ_RI_037_Adjacent_layers_must_meet_exactly_when_non_contiguous_is_not_declared`; HTTP create/update refusals |
| RI-only catalogue and organisation picker read permissions | `Exact_RI_roles_can_read_the_real_catalogue_and_reinsurer_picker_without_write_permissions` (accountant/manager); actual `MOTOR-GR` / `OWN-DAMAGE` creation |
| REQ-RI-046/047 signed share and exactly one lead | Invalid-treaty lifecycle test; `ContractContentTests.REQ_RI_046_047_Signed_lines_must_equal_the_placed_share_with_exactly_one_lead` |
| REQ-RI-056/057 lifecycle and maker/checker | Lifecycle tests for creator/editor/submitter, return, stale content, duplicate decisions, inbox approval/rejection and delegated participant principal |
| REQ-RI-057 immutability and decision-evidence consistency | `ContractDatabaseTests`: app-role sealed-version/content refusal, already-approved insert refusal, pending seal refusal and predicate dimension/participant checks |
| REQ-RI-058 activation/expiry | `ContractTemporalTests`: future activation/expiry, DST/start day, late registration and scanner idempotence; `LifecycleJobTests` worker registration |
| REQ-RI-001/116 applicable period/scope | `ContractTemporalTests.REQ_RI_001_116_Applicable_respects_the_half_open_Athens_period_and_the_scope`; expired treaty remains applicable to an in-period loss |
| REQ-RI-065 no deletion; history and exclusion | `ContractDatabaseTests`: app privileges, append-only child content, lifecycle and overlapping valid periods |
| Gapless/idempotent creation; no event personal data | Lifecycle numbering/idempotence and activation-event tests |
| Additive list layers, current revision and layer order | `Registry_list_layer_summary_uses_current_revision_and_preserves_money_and_layer_order` |
| PLT editor persistence, supersession, validation and delegated checker | `Platform.ApprovalTests.Owning_module_editors_are_deduplicated_inherited_and_refuse_delegated_decisions`; actor-key validation theory and existing approval regressions |

The recovery fixes the delegated-checker test's in-process request context to supply the pipeline's
required idempotency key and configuration hash. Its original failure happened before the SoD check.
The list summary regression compares decimal values rather than database formatting scale.

## Validation and limits

Native Windows Release build uses `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
and `-m:2`. Tests use `--no-build` and Microsoft Testing Platform namespace/class filters.
Logs are unique to this worktree under `.logs/ri-recovery-validation-*20261009.log`.

| Gate | Result |
|---|---|
| Full native solution Release build | Passed; zero warnings/errors |
| Registry namespace | 38 passed, zero failed/skipped |
| PLT `ApprovalTests` | 20 passed, zero failed/skipped |
| Architecture | 198 passed, zero failed/skipped |
| ContractGen `--check` | Current, 2,852 generated files |
| Generated API samples | 2,423 checked; OK |

These are focused registry/approval regressions plus architecture and contract gates, not a full
solution test run or real-browser acceptance. GitHub CI status is recorded on the PR separately.

Applicable pitfalls: 3–5 (in-process, bound approvals and participant SoD), 8/17/40/47 (app-role
immutability, append-only history, locking and state/evidence guards), 13/14 (time boundaries),
15 (409 races), 18 (event personal data), 21–23 (additive contracts and module boundary),
35/38/39 (isolated Testcontainers, bounded build workers and unique logs), 45 (exact remote head).
PLT inbox decisions remain supported; RI applies them only after full execution verification.

This is the limited SL4 registry foundation. It does not deliver cession/recovery calculations,
settlements, programmes, other treaty types, renewal/versioning beyond v1, or production commercial
treaty/authority decisions. Commercial values in tests are illustrative. The dependent RI UI is
published separately; backend integration tests are not proof of its real-browser acceptance.
