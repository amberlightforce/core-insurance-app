# SL4-PLT recovery: roles, development identity and owning-module withdrawal

Branch `codex/sl4-platform`; owned checkout `agent-ad6062491a131f6ec`.
Started from main `a9ec551` after SL3-PLT-SUPPORT merged. Preserved the prior released branch unchanged.
Initial dev-user checkpoint `30277559bd363e3bd9dd4b7ac6e9b4b0b768de60`; server actor-key checkpoint `443052e404370740bc6d908dd367d29f271823d3`.

## Scope and ownership

Development users append the canonical contract application roles: ROLE-20 `Staff.RecoverySpecialist`, ROLE-28
`Staff.ReinsuranceAccountant`, ROLE-27 `Staff.ReinsuranceManager`. There is no separate central application-role
enum in PLT: the existing BillingManager pattern is development users plus owning-module permission grants.
`AppRole` is the host process mode, so it is unchanged. Authority types/grants remain with owning modules.

Exact unique display names: `Dev Recovery Specialist (synthetic)`, `Dev Reinsurance Accountant (synthetic)`,
`Dev Reinsurance Manager (synthetic)`. Superuser gains all three roles; maker/checker principal restrictions remain.
Development list/sign-in returns `actorKey` derived by the server from the JWT identity (`USER:dev:<id>`), preserving
the picker `id`. This corrects a mismatch found by root's real RI browser journey; no authentication semantics change.

Root explicitly authorized shared-file exceptions for RequestContext/TransactionDecorator, SPI wiring and Host
development identity. The context records the registered command descriptor's module, restores it in finally across
nested calls and exceptions, and never accepts a module from a public DTO. Existing transaction behavior is preserved.
Withdrawal captures the outer identity before entering PLT and compares it with the stored subject module.
Calls outside an owning command or from another module fail `PLT-ERR-NOT-OWNER` (403). No REST route is mapped.

This boundary trusts registered in-process command producers and the module-owned subject identity they stored.
It is not an attestation against arbitrary process code, malicious command registration or the shared application
database credential. No credential/schema redesign or new migration is introduced. Existing approval constraints,
conditional updates, audit/outbox and idempotency pipeline are reused.

## Requirement traceability

| Requirement | Regression |
|---|---|
| D-SL4-16 roles/users/exact names, Development only | DevelopmentSignInTests: three actual new-user JWT/role/name cases; existing Production/Staging/Testing refusal |
| Server-returned actor identity agrees with JWT oid | SL4_users_return_the_server_actor_key_matching_the_JWT |
| REQ-PLT-004/D-SL4-14 owner-only withdrawal | Owner_guard_rejects_wrong_module_and_unstamped_direct_calls_but_accepts_BIL_owner |
| Repeat-call idempotency, mismatch, reason, event once, inbox removal | Owner_withdrawal_is_audited_published_once_idempotent_and_removed_from_inbox |
| Decided refusal and no REST | Decided_request_cannot_be_withdrawn_and_no_REST_route_exists |
| Decide/withdraw race, one winner/409/event once | Concurrent_decide_and_withdraw_have_exactly_one_winner_and_one_conflict (five iterations) |
| Concurrent repeated withdrawal | Concurrent_double_withdrawal_returns_the_same_result_and_emits_once |
| Nested module stamp and exception rollback | Owner wrappers assert CLM/BIL restoration; Nested_command_stamp_restores_after_failure_and_outer_exception_rolls_back |
| Superuser maker/checker SoD remains | Existing ApprovalTests superuser regression |

The brief's generic `PLT-ERR-STATE` name is implemented using the already typed withdrawal contract's
`PLT-ERR-APPROVAL-STALE` (409). Repeat withdrawal returns the original timestamp and emits no additional event.
The reason is persisted in decision_comment and successful audit, without changing supersession withdrawal.
No contracts/generated artifacts change.

Files changed: Host development-users JSON and DevelopmentAuthentication; Platform Approvals/WithdrawApproval,
ApprovalCommands SPI wiring, ApprovalStore withdrawal path, PlatformModule registration, Context/RequestContext,
Commands/Decorators; IntegrationTests DevelopmentSignInTests and Platform/WithdrawApprovalTests; this report.

## Validation

Initial checkpoint: native Release build zero warnings/errors; eight DevelopmentSignInTests passed.
Final native gates passed against the implementation checkpoint plus the distinct-name assertion:
Release zero warnings/errors; WithdrawApprovalTests 6; DevelopmentSignInTests 11; existing ApprovalTests 16
(including superuser SoD); IdempotencyTests 9; AuditTests 8; Platform unit tests 69; Host tests 35;
Architecture 198; Contracts 18. All passed with zero skipped/failed. ContractGen current (2,852 files);
API samples checked 2,423, all valid. Logs: `.logs/sl4-plt-final-{build,withdraw,signin,approval,idempotency,audit,platform,host,architecture,contractgen,samples,api-samples}.log`.

These are focused integration/pipeline/unit/contract checks, not a full solution test run or proof of the dependent
RI UI browser acceptance. No requested behavior remains stubbed. GitHub CI and independent security review are
reported on the PR; root owns integration and merging. The initial user/actor-key checkpoints were separately
provided to root for its real RI browser journey.

PITFALLS 3/5: owning module/principal SoD; 4/6: typed SPI and no REST; 15: conditional state transition;
30: unique display names; 38/39: Windows native, two MSBuild workers, server/node reuse disabled; 45: verify pushed HEAD.
Root owns final integration, independent review and merging. No user Docker stack was altered.
