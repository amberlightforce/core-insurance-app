# PRD-18 digest C: Annex B.1 Motor MVP cut (P1 Musts) by module, part 1

Source: `core-insurance-prds/PRD-18-programme-requirements-baseline.md`, lines 4782–6700 only (read in full).

## Scope and context

- Lines 4782–4791 are the tail of the previous annex (XMR findings table rows XMR-F-420/421, counts, CR summary). Not part of B.1. Two items there are relevant to the MVP cut:
  - XMR-F-420 (Minor, open: Yes): golden scenario GS-02 (six-month term) runs against the Greece pack on every build, while three PRDs (POL, RAT, FIN) assume annual-only motor terms at MVP. Owner POL with MKT; refs XMR-D-286, XMR-CR-MKT-06. Touches REQ-POL-040 (term types), REQ-PFC-034 (allowed term lengths), REQ-MKT-238/239 (golden suite).
  - XMR-F-421 (Minor): local risk IDs `R-01…` collide in form with contract rulings `R-01…R-100`. Read the 'R-nn' references below as contract rulings.
  - Prior annex totals: 22 findings (0 Blocker, 10 Major, 12 Minor); decisions XMR-D-250…288; 78 CRs in §20.2.
- Line 4795 starts `# Annex B. Motor MVP cut at requirement level (detail for §16)`; line 4797 `## B.1 Motor MVP cut (phase P1 Musts) by module`. Sub-headings are numbered `A.1.x` in the source even though they sit under B.1.
- Every row in B.1 is a phase P1 Must. Table columns are kept as in the source: **ID | J | W | Sz | Tag | Summary | Must deps (other modules)**. The range defines no legend. Reading the values: J looks like the journey (J0 platform, J1 party/distribution, J2 product/rating/UW, J3 policy, J4 billing, J5 mid-term servicing, J6 renewal, J13 migration), W looks like the delivery wave (W1–W9), Sz is the size (S/M/L/XL), and Tag is B or E (legend not in range; probably Base vs Enhanced/Extended). Check the legend in §16 before relying on these readings.
- The summary column below is a few-word title made from the source summary, which is itself cut off in the source with '…'. The deps column is copied exactly.
- **Dependence on undecided questions:** no row in the range is marked as depending on an open question, an undecided decision or a TBD. Nothing is marked 'partial', 'deferred' or 'stub' at scope level. Two notes on the words used: 'stub' appears only as the **Cyprus stub pack** (a deliberate test variant: REQ-MKT-263/265, REQ-PFC-128/239, REQ-RAT-154/194), and 'partial' appears only in behaviour (REQ-PFC-225 partial drafts, REQ-POL-072 never partial, REQ-BIL-132 partial payments). The closest open items are: XMR-F-420 above (term length); REQ-MKT-266 (Cyprus motor insurers' fund levy '5%, assumed', synthetic); REQ-MKT-265 (CY-STAMP, 'not law', synthetic); REQ-RAT-122 ('subject to the renewal…', with the condition cut off in the source); REQ-MKT-327 and REQ-BIL-012, whose deps lists are cut off in the source ('…').
- The Notes column is derived from the summary text and flags: Cyprus stub / test fixture / synthetic or assumed values / Greece pack / contract-ruling or CCR basis / migration (J13 or W9) / wave W8 / truncated deps.

## Per-module counts

| Module | Source lines | Stated Musts | Stated points | Rows in this range | Complete? |
|---|---|---|---|---|---|
| PLT | 4799–5085 | 283 | 397 | 283 | Yes |
| MKT | 5087–5334 | 244 | 354 | 244 | Yes |
| PTY | 5336–5557 | 218 | 377 | 218 | Yes |
| PFC | 5559–5762 | 200 | 298 | 200 | Yes |
| RAT | 5764–5981 | 214 | 295 | 214 | Yes |
| UW | 5983–6169 | 183 | 269 | 183 | Yes |
| POL | 6171–6467 | 293 | 450 | 293 | Yes |
| BIL | 6469–6700 | 294 | 442 | 228 | **No**: continues past line 6700 (66 rows not in range) |
| **Total** | | 1929 | 2882 | 1863 | |

The row counts match the stated counts for PLT, MKT, PTY, PFC, RAT, UW and POL. **BIL is cut off: the last row read is REQ-BIL-279 (line 6700). The next digest continues from REQ-BIL-280 (line 6701).**

### Breakdown by J / W / Tag per module

| Module | J values | W values | Tag B | Tag E | Sz S/M/L/XL |
|---|---|---|---|---|---|
| PLT | J0:277, J13:6 | W1:241, W8:36, W9:6 | 207 | 76 | 219/44/5/15 |
| MKT | J0:243, J13:1 | W1:240, W8:3, W9:1 | 107 | 137 | 168/62/4/10 |
| PTY | J1:217, J13:1 | W2:161, W6:56, W9:1 | 209 | 9 | 102/99/4/13 |
| PFC | J2:199, J6:1 | W2:199, W6:1 | 128 | 72 | 136/52/1/11 |
| RAT | J13:6, J2:208 | W3:188, W8:20, W9:6 | 179 | 35 | 160/45/0/9 |
| UW | J2:173, J6:10 | W3:173, W6:10 | 164 | 19 | 126/46/2/9 |
| POL | J13:1, J3:206, J5:65, J6:21 | W4:206, W6:86, W9:1 | 276 | 17 | 184/89/6/14 |
| BIL | J1:31, J13:1, J4:167, J5:29 | W5:167, W6:60, W9:1 | 219 | 9 | 135/78/3/12 |

## A.1.1 PLT (283 Musts, 397 points): 283 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-PLT-001 | J0 | W1 | XL | B | custom-built identity service with a **staff realm** that authenticates… | REQ-MKT-001 |  |
| REQ-PLT-002 | J0 | W1 | XL | B | business audit trail API that appends AuditEvent records (actor… | — |  |
| REQ-PLT-003 | J0 | W1 | XL | B | shared authority framework: owning modules register authority types with… | REQ-UW-010, REQ-CLM-003, REQ-BIL-007, REQ-RAT-005 |  |
| REQ-PLT-004 | J0 | W1 | XL | B | maker-checker (four-eyes) service in which owning modules register approval… | REQ-WRK-001 |  |
| REQ-PLT-005 | J0 | W1 | XL | B | event infrastructure: a transactional outbox library per module schema,… | — |  |
| REQ-PLT-006 | J0 | W1 | XL | B | integration hub (adapter host) where every external call runs… | REQ-CMP-001, REQ-CMP-002 |  |
| REQ-PLT-007 | J0 | W1 | XL | B | durable workflow engine (namespaces per stamp, versioned workflow definitions,… | REQ-CMP-003, REQ-UW-001 |  |
| REQ-PLT-008 | J0 | W1 | XL | B | host the configuration-service runtime that resolves configuration under the… | REQ-MKT-001, REQ-MKT-004 |  |
| REQ-PLT-009 | J0 | W1 | XL | B | own and serve reference data: calendars and holidays per… | REQ-MKT-006, REQ-FIN-006 |  |
| REQ-PLT-010 | J0 | W8 | XL | B | AI control plane: an EU model gateway as the… | REQ-DAT-005, REQ-CMP-007 | W8 |
| REQ-PLT-011 | J0 | W1 | XL | B | data-protection platform services: the retention catalogue (RC codes) and… | REQ-CMP-005, REQ-DOC-004 |  |
| REQ-PLT-012 | J0 | W8 | XL | B | DORA operations: an incident log with timestamped detection, awareness,… | REQ-CMP-008 | W8 |
| REQ-PLT-013 | J0 | W1 | XL | B | observability and service-level management: OpenTelemetry traces, metrics and logs… | — |  |
| REQ-PLT-014 | J0 | W1 | XL | B | numbering service that issues business identifiers per legal entity… | REQ-MKT-002 |  |
| REQ-PLT-015 | J0 | W1 | XL | B | **external realm** of the identity service for customers, agents… | REQ-CHN-008, REQ-PTY-008 |  |
| REQ-PLT-030 | J0 | W1 | M | B | hold a staff directory record per user with: user… | REQ-MKT-005 |  |
| REQ-PLT-031 | J0 | W1 | S | B | manage the staff user lifecycle Invited → Active →… | — |  |
| REQ-PLT-032 | J0 | W1 | S | E | act as an OpenID Connect provider for staff applications… | — |  |
| REQ-PLT-033 | J0 | W1 | M | E | require phishing-resistant multi-factor authentication for all staff (WebAuthn passkeys,… | — |  |
| REQ-PLT-035 | J0 | W1 | S | B | where a password is permitted (recovery factor or exception), | — |  |
| REQ-PLT-036 | J0 | W1 | S | B | throttle failed sign-ins progressively and lock the account after… | — |  |
| REQ-PLT-037 | J0 | W1 | S | E | single sign-on across every staff application in a stamp… | — |  |
| REQ-PLT-038 | J0 | W1 | S | E | support step-up authentication: an API or screen may demand… | — |  |
| REQ-PLT-039 | J0 | W1 | S | E | let users and administrators list and revoke sessions, and… | — |  |
| REQ-PLT-040 | J0 | W1 | M | E | issue short-lived access tokens (default 10 min) as JWTs… | — |  |
| REQ-PLT-041 | J0 | W1 | S | B | offer token introspection and revocation, and ensure that disabling… | — |  |
| REQ-PLT-043 | J0 | W1 | S | E | keep token-signing keys in the key-management service, rotate them… | — |  |
| REQ-PLT-044 | J0 | W1 | S | E | allow an administrator to reset a user's authenticators only… | — |  |
| REQ-PLT-046 | J0 | W1 | S | E | emit identity security events (sign-in success and failure, MFA… | — |  |
| REQ-PLT-048 | J0 | W1 | S | B | present the hosted sign-in page in Greek and English,… | REQ-MKT-005 |  |
| REQ-PLT-049 | J0 | W1 | S | B | use the same staff single sign-on for every staff… | — |  |
| REQ-PLT-050 | J0 | W1 | M | B | keep the external realm separate from the staff realm… | — |  |
| REQ-PLT-051 | J0 | W1 | M | B | let customers register with a verified email or mobile… | REQ-PTY-001, REQ-PTY-005 |  |
| REQ-PLT-052 | J0 | W1 | S | E | offer passkeys as the primary customer sign-in method, with… | — |  |
| REQ-PLT-053 | J0 | W1 | S | E | require step-up authentication for customer actions marked sensitive by… | — |  |
| REQ-PLT-054 | J0 | W1 | S | B | require MFA at every sign-in for intermediary and bank-staff… | — |  |
| REQ-PLT-056 | J0 | W1 | S | B | protect sign-in, registration and recovery endpoints against automated attacks… | — |  |
| REQ-PLT-057 | J0 | W1 | M | B | federate bank-staff users from each bancassurance partner's identity provider… | — |  |
| REQ-PLT-058 | J0 | W1 | S | B | register partner API clients with client credentials authenticated by… | REQ-CHN-001 |  |
| REQ-PLT-059 | J0 | W1 | S | E | issue workload identities to modules, batch jobs and adapters… | — |  |
| REQ-PLT-060 | J0 | W1 | S | E | mint delegated tokens for AI agents by token exchange,… | REQ-CHN-003 |  |
| REQ-PLT-063 | J0 | W1 | M | E | customer account recovery: recovery with a second registered passkey,… | REQ-PTY-005 |  |
| REQ-PLT-064 | J0 | W1 | S | E | intermediary account recovery through the agency administrator or, for… | — |  |
| REQ-PLT-067 | J0 | W1 | S | B | re-point external identity links when PTY publishes PartiesMerged or… | REQ-PTY-007 |  |
| REQ-PLT-069 | J0 | W1 | S | E | offer self-service APIs for external users to manage authenticators,… | REQ-CHN-008 |  |
| REQ-PLT-070 | J0 | W1 | S | B | route a customer's request to delete their online account… | REQ-CMP-005 |  |
| REQ-PLT-071 | J0 | W1 | S | E | suspend all users of an intermediary within 5 minutes… | REQ-PTY-008 |  |
| REQ-PLT-072 | J0 | W1 | M | B | derive intermediary users' ABAC scope (producer codes, branch, hierarchy)… | REQ-PTY-008, REQ-PTY-009 |  |
| REQ-PLT-073 | J0 | W1 | M | E | delegated administration: an organisation administrator (agency administrator, bank channel… | REQ-CHN-005, REQ-PTY-008 |  |
| REQ-PLT-075 | J0 | W1 | S | B | maintain a permission catalogue to which each module contributes… | — |  |
| REQ-PLT-076 | J0 | W1 | S | B | let administrators define roles as sets of permissions with… | — |  |
| REQ-PLT-078 | J0 | W1 | S | E | make role assignments effective-dated (start, optional end) and scoped… | — |  |
| REQ-PLT-079 | J0 | W1 | S | B | one authorisation policy library used by every module, evaluating… | — |  |
| REQ-PLT-080 | J0 | W1 | S | B | mask P2 and P3 attributes by default in every… | REQ-PTY-001 |  |
| REQ-PLT-081 | J0 | W1 | S | B | set the caller's legal-entity and jurisdiction context on every… | — |  |
| REQ-PLT-082 | J0 | W1 | S | B | hold segregation-of-duties rules as data (conflicting permissions, roles or… | REQ-RAT-007 |  |
| REQ-PLT-084 | J0 | W1 | S | E | accept joiner, mover and leaver records from the HR… | — |  |
| REQ-PLT-085 | J0 | W1 | S | E | on a mover event, compute the access delta, add | — |  |
| REQ-PLT-086 | J0 | W1 | S | B | on a leaver event, disable the user within 1 | REQ-WRK-001 |  |
| REQ-PLT-087 | J0 | W1 | M | E | run access-review campaigns with scope (all users, privileged roles,… | REQ-WRK-001 |  |
| REQ-PLT-088 | J0 | W1 | S | E | schedule access reviews at least quarterly for privileged roles… | — |  |
| REQ-PLT-089 | J0 | W1 | M | E | grant privileged access just in time: the user requests… | — |  |
| REQ-PLT-090 | J0 | W1 | M | E | at least two emergency-access accounts per stamp, with credentials… | — |  |
| REQ-PLT-091 | J0 | W1 | S | E | prohibit standing privileged access to production infrastructure and databases;… | — |  |
| REQ-PLT-092 | J0 | W1 | S | E | record for every service identity an owner, purpose, scopes,… | REQ-WRK-001 |  |
| REQ-PLT-094 | J0 | W1 | S | E | generate and run an access-control test matrix (every role… | — |  |
| REQ-PLT-095 | J0 | W1 | S | B | offer user search by username, first name, last name,… | REQ-WRK-003 |  |
| REQ-PLT-096 | J0 | W1 | S | E | scope administrators to legal entities, so that an administrator… | — |  |
| REQ-PLT-098 | J0 | W1 | S | B | export users, roles, role assignments, authority grants and SoD… | — |  |
| REQ-PLT-100 | J0 | W1 | L | B | let owning modules register authority types with code, name… | REQ-UW-010, REQ-CLM-003, REQ-BIL-007, REQ-RAT-005 |  |
| REQ-PLT-101 | J0 | W1 | S | B | let administrators create, clone, edit, retire and version authority… | — |  |
| REQ-PLT-102 | J0 | W1 | S | B | assign authority profiles to users or roles, and allow… | — |  |
| REQ-PLT-103 | J0 | W1 | M | B | plt.Authority.check(actor, authorityType, dimensions, objectRef, asAt) returning decision (allow, refer,… | REQ-UW-010 |  |
| REQ-PLT-104 | J0 | W1 | S | E | let a holder delegate some or all of an… | REQ-WRK-003 |  |
| REQ-PLT-105 | J0 | W1 | S | E | forbid any user from creating or editing an authority… | — |  |
| REQ-PLT-106 | J0 | W1 | S | E | evaluate monetary limits in the limit's currency, converting the… | — |  |
| REQ-PLT-108 | J0 | W1 | S | E | route authority-profile and grant changes above configured thresholds through… | — |  |
| REQ-PLT-109 | J0 | W1 | S | B | show each profile's grants with type, comparison, value and… | — |  |
| REQ-PLT-110 | J0 | W1 | S | E | store every check result (check id, inputs, decision, limit,… | — |  |
| REQ-PLT-111 | J0 | W1 | S | E | never grant authority to an AI agent; for AI-initiated… | — |  |
| REQ-PLT-113 | J0 | W1 | S | B | let modules register approval types with: checker permission or… | — |  |
| REQ-PLT-114 | J0 | W1 | M | B | plt.Approval.request, decide (approve, reject, return for change, with mandatory… | REQ-WRK-007 |  |
| REQ-PLT-115 | J0 | W1 | S | B | refuse a decision by the maker, by any user… | — |  |
| REQ-PLT-116 | J0 | W1 | M | E | publish ApprovalRequested and ApprovalDecided (accepted, contract R-02) so WRK… | REQ-WRK-001, REQ-WRK-003 | per contract ruling |
| REQ-PLT-117 | J0 | W1 | S | E | bind each approval to the SHA-256 content hash of… | — |  |
| REQ-PLT-120 | J0 | W1 | S | B | approval inbox and detail screen for platform-owned approval types… | — |  |
| REQ-PLT-121 | J0 | W1 | S | E | apply the same maker-checker rules to changes proposed by… | — |  |
| REQ-PLT-123 | J0 | W1 | M | B | store AuditEvent with the attributes in section 7.1, including… | — |  |
| REQ-PLT-124 | J0 | W1 | S | B | write audit records through an in-process library in the… | — |  |
| REQ-PLT-125 | J0 | W1 | S | E | chain audit records per stamp and partition with SHA-256… | — |  |
| REQ-PLT-126 | J0 | W1 | S | B | keep audit records in write-once storage with retention lock… | — |  |
| REQ-PLT-127 | J0 | W1 | S | E | store P2/P3 before/after values encrypted under per-subject keys and… | — |  |
| REQ-PLT-128 | J0 | W1 | S | B | offer plt.Audit.query filtered by object, actor, time range, operation,… | — |  |
| REQ-PLT-129 | J0 | W1 | S | B | audit log viewer (SCR-PLT-09) with object timeline, actor activity,… | — |  |
| REQ-PLT-130 | J0 | W1 | S | E | export audit search results as CSV, JSON and PDF… | — |  |
| REQ-PLT-131 | J0 | W1 | S | E | verify chain integrity daily and on demand and raise… | — |  |
| REQ-PLT-132 | J0 | W1 | S | B | link audit records, events and traces through the correlation… | — |  |
| REQ-PLT-133 | J0 | W1 | S | E | audit access to the audit trail itself (searches, views… | — |  |
| REQ-PLT-134 | J0 | W1 | S | B | retain audit records under retention class RC-AUD-BUS and honour… | — |  |
| REQ-PLT-135 | J0 | W1 | S | B | send identity and infrastructure security logs to the SIEM… | — |  |
| REQ-PLT-136 | J0 | W1 | M | B | outbox table in each module schema with: outbox_id, event_id… | — |  |
| REQ-PLT-137 | J0 | W1 | S | B | offer a publish API that only writes to the… | — |  |
| REQ-PLT-138 | J0 | W1 | S | B | relay outbox rows with workers that claim partitions by… | — |  |
| REQ-PLT-139 | J0 | W1 | S | B | assign a gap-free sequence per aggregate at write time… | — |  |
| REQ-PLT-140 | J0 | W1 | S | B | publish to topics <mod>.events.v<major> partitioned by aggregate id, retain… | REQ-DAT-001 |  |
| REQ-PLT-141 | J0 | W1 | S | E | reject events larger than 256 KB and any P2/P3… | — |  |
| REQ-PLT-142 | J0 | W1 | S | B | keep a schema registry with a schema per event… | — |  |
| REQ-PLT-143 | J0 | W1 | M | B | consumer library that de-duplicates on event_id per consumer group… | — |  |
| REQ-PLT-144 | J0 | W1 | S | B | park dead letters with error class, stack reference, attempts,… | — |  |
| REQ-PLT-145 | J0 | W1 | S | B | let operators replay events for a consumer group from… | — |  |
| REQ-PLT-146 | J0 | W1 | S | B | measure outbox backlog, relay lag, consumer lag per group,… | — |  |
| REQ-PLT-147 | J0 | W1 | S | B | fill the envelope automatically (correlation id, causation id, actor,… | — |  |
| REQ-PLT-148 | J0 | W1 | S | B | pass through origin=MIGRATION for events from import APIs unchanged. | REQ-MIG-001 |  |
| REQ-PLT-152 | J0 | W1 | S | B | isolate event streams per stamp; the only cross-stamp flow… | — |  |
| REQ-PLT-153 | J0 | W1 | S | B | run all calls to external systems in a separate… | — |  |
| REQ-PLT-154 | J0 | W1 | S | B | apply per adapter: connect and response timeouts, retries with… | — |  |
| REQ-PLT-155 | J0 | W1 | S | B | send an idempotency key with every outbound command, de-duplicate… | REQ-BIL-009 |  |
| REQ-PLT-156 | J0 | W1 | S | B | archive each request and response (encrypted, hashed, with correlation… | REQ-CMP-005 |  |
| REQ-PLT-157 | J0 | W1 | S | B | sandbox double for every adapter, used in CI and… | REQ-CMP-001 |  |
| REQ-PLT-159 | J0 | W1 | S | B | keep an adapter registry: external system, owner, protocol, endpoints… | — |  |
| REQ-PLT-160 | J0 | W1 | S | B | store adapter credentials and certificates only in the secrets… | — |  |
| REQ-PLT-161 | J0 | W1 | L | B | platform malware-scanning and quarantine service: every inbound file or… | REQ-BIL-004, REQ-WRK-005, REQ-WRK-263, REQ-DOC-004 |  |
| REQ-PLT-162 | J0 | W1 | S | B | integration and queue monitor (SCR-PLT-11) with adapter health, breaker… | — |  |
| REQ-PLT-164 | J0 | W1 | S | B | apply each adapter's declared degraded mode (queue and continue,… | REQ-CMP-001 |  |
| REQ-PLT-165 | J0 | W1 | S | B | durable workflow engine that records workflow history as events,… | — |  |
| REQ-PLT-166 | J0 | W1 | S | B | run one workflow namespace per stamp with task queues… | — |  |
| REQ-PLT-167 | J0 | W1 | S | B | version workflow definitions so that running instances finish on… | — |  |
| REQ-PLT-168 | J0 | W1 | S | B | durable timers in calendar time and in business days… | REQ-CMP-003 |  |
| REQ-PLT-169 | J0 | W1 | S | B | prevent workflows from holding financial state: workflow state may… | — |  |
| REQ-PLT-170 | J0 | W1 | S | B | index workflows by type, status, business key, legal entity,… | — |  |
| REQ-PLT-171 | J0 | W1 | S | B | let authorised operators retry a failed activity, send a… | — |  |
| REQ-PLT-172 | J0 | W1 | S | B | run scheduled jobs as workflows with cron schedules per… | — |  |
| REQ-PLT-173 | J0 | W1 | S | E | split batch jobs into partitions with checkpoints so that… | — |  |
| REQ-PLT-174 | J0 | W1 | S | B | evaluate decision tables with typed inputs and outputs and… | REQ-UW-001 |  |
| REQ-PLT-175 | J0 | W1 | S | B | store decision-table versions immutably with a content hash and… | REQ-UW-001 |  |
| REQ-PLT-176 | J0 | W1 | S | B | require test cases with each table version and block… | — |  |
| REQ-PLT-177 | J0 | W1 | S | E | return an evaluation trace (table, version hash, inputs, matched… | — |  |
| REQ-PLT-179 | J0 | W1 | M | B | host the configuration resolution runtime implementing the MKT model… | REQ-MKT-001, REQ-MKT-008 |  |
| REQ-PLT-180 | J0 | W1 | S | B | resolve configuration from an in-process cache within 5 ms… | REQ-MKT-003 |  |
| REQ-PLT-181 | J0 | W1 | S | B | compute a configuration hash per resolution context (legal entity,… | REQ-POL-001 |  |
| REQ-PLT-182 | J0 | W1 | S | B | publish ConfigChanged with key, layer and new hash when… | REQ-MKT-001 |  |
| REQ-PLT-183 | J0 | W1 | S | B | roll effective dates at midnight in the jurisdiction's time… | — |  |
| REQ-PLT-184 | J0 | W1 | S | B | keep runtime properties (technical environment settings: endpoints, timeouts, batch… | — |  |
| REQ-PLT-185 | J0 | W1 | S | E | allow runtime-property changes directly in non-production; in production only… | — |  |
| REQ-PLT-186 | J0 | W1 | M | B | manage feature flags with key, description, type (release, operational… | — |  |
| REQ-PLT-187 | J0 | W1 | S | B | evaluate flags in process within 1 ms p95, with… | — |  |
| REQ-PLT-188 | J0 | W1 | S | B | audit every flag change, publish FeatureFlagChanged, and require maker-checker… | — |  |
| REQ-PLT-189 | J0 | W1 | S | B | resolution-trace API returning, for a key and context, the… | REQ-MKT-001 |  |
| REQ-PLT-191 | J0 | W1 | S | E | refuse any flag or property that disables audit, authority… | — |  |
| REQ-PLT-192 | J0 | W1 | S | E | apply operational kill-switch flags across the stamp within 60… | — |  |
| REQ-PLT-193 | J0 | W1 | S | B | feature-flag console (SCR-PLT-12). | — |  |
| REQ-PLT-195 | J0 | W1 | S | B | hold calendars per country, region (zone), settlement system and… | — |  |
| REQ-PLT-196 | J0 | W1 | M | E | generate holidays from pack rules supplied through HolidayCalendarProvider (fixed… | REQ-MKT-002 |  |
| REQ-PLT-197 | J0 | W1 | M | B | business-day arithmetic: isBusinessDay, addBusinessDays (positive and negative), nextBusinessDay, previousBusinessDay,… | REQ-CMP-003, REQ-WRK-009 |  |
| REQ-PLT-198 | J0 | W1 | S | B | let authorised users list holidays, add exception holidays (name… | — |  |
| REQ-PLT-199 | J0 | W1 | S | E | version calendars; a change after publication creates a new… | — |  |
| REQ-PLT-200 | J0 | W1 | M | B | store FX rates with currency pair, rate, rate type… | REQ-FIN-006 |  |
| REQ-PLT-201 | J0 | W1 | S | B | import the default rate source daily through the adapter… | — |  |
| REQ-PLT-202 | J0 | W1 | S | E | allow manual FX entry or correction only with maker-checker… | — |  |
| REQ-PLT-203 | J0 | W1 | S | B | hold the ISO 4217 currency catalogue (code, numeric code,… | REQ-MKT-006 |  |
| REQ-PLT-205 | J0 | W1 | S | E | publish ReferenceDataPublished (accepted, contract R-02) when a calendar version,… | — | per contract ruling |
| REQ-PLT-206 | J0 | W1 | S | B | map each jurisdiction to an IANA time zone and… | — |  |
| REQ-PLT-207 | J0 | W1 | S | B | audit every reference-data change and show change history per… | — |  |
| REQ-PLT-208 | J0 | W1 | S | B | hold group USD rates of the rate type defined… | REQ-FIN-006 |  |
| REQ-PLT-209 | J0 | W1 | S | B | plt.Number.next(scheme, context) returning a business identifier unique within the… | REQ-MKT-002 |  |
| REQ-PLT-210 | J0 | W1 | S | B | apply series definitions from packs: prefix, pattern, check-digit algorithm,… | — |  |
| REQ-PLT-211 | J0 | W1 | S | B | allocate gapless numbers inside the caller's transaction and record… | REQ-CMP-001 |  |
| REQ-PLT-212 | J0 | W1 | S | E | issue at least 500 numbers per second per gap-allowed… | — |  |
| REQ-PLT-213 | J0 | W1 | S | B | forbid number formats that encode personal data and include… | REQ-MKT-003 |  |
| REQ-PLT-215 | J0 | W1 | S | B | reserve ranges and register legacy numbers as used for… | REQ-MIG-002 |  |
| REQ-PLT-217 | J0 | W8 | S | B | route every model call from any module through the… | — | W8 |
| REQ-PLT-218 | J0 | W8 | S | B | keep a model-endpoint registry (provider, EU region, model id… | REQ-DAT-005 | W8 |
| REQ-PLT-219 | J0 | W8 | S | B | resolve AI toggles across tenant → legal entity →… | — | W8 |
| REQ-PLT-220 | J0 | W8 | S | B | global and per-feature kill switch reaching full effect across… | — | W8 |
| REQ-PLT-221 | J0 | W8 | S | B | allow only EU-hosted endpoints with contractual zero data retention… | — | W8 |
| REQ-PLT-222 | J0 | W8 | M | B | enforce per-feature data minimisation at the gateway: an allow-list… | REQ-CMP-007 | W8 |
| REQ-PLT-223 | J0 | W8 | M | B | write an AiInteractionRecord for every model call (feature id,… | — | W8 |
| REQ-PLT-224 | J0 | W8 | S | B | manage the AI recommendation lifecycle Proposed → Accepted /… | — | W8 |
| REQ-PLT-225 | J0 | W8 | S | B | version prompt templates and tool definitions per feature, with… | — | W8 |
| REQ-PLT-226 | J0 | W8 | S | B | apply a per-feature timeout (default 5 s to first… | — | W8 |
| REQ-PLT-227 | J0 | W8 | S | B | disable or flag a feature automatically on ModelDriftDetected or… | REQ-DAT-005 | W8 |
| REQ-PLT-228 | J0 | W8 | S | B | require maker-checker for toggle changes at tenant and legal-entity… | — | W8 |
| REQ-PLT-230 | J0 | W8 | S | E | apply input and output safety controls: prompt-injection screening for… | — | W8 |
| REQ-PLT-231 | J0 | W8 | S | B | make AI-agent tool calls use the delegated token of… | REQ-CHN-003 | W8 |
| REQ-PLT-232 | J0 | W8 | S | B | AI control centre (SCR-PLT-17). | — | W8 |
| REQ-PLT-233 | J0 | W8 | S | B | refuse to enable any AI feature that lacks an… | REQ-CMP-007 | W8 |
| REQ-PLT-234 | J0 | W8 | S | B | send per-feature usage, latency, acceptance, edit and override rates… | REQ-DAT-005 | W8 |
| REQ-PLT-235 | J0 | W1 | S | B | maintain the retention catalogue of RC codes in section… | — |  |
| REQ-PLT-236 | J0 | W1 | S | B | hold retention schedules per RC code and jurisdiction (duration… | REQ-MKT-001 |  |
| REQ-PLT-237 | J0 | W1 | S | B | run the retention engine daily: compute records due per… | — |  |
| REQ-PLT-238 | J0 | W1 | S | B | apply legal holds to objects, parties or scopes (for… | — |  |
| REQ-PLT-239 | J0 | W1 | S | B | answer plt.LegalHold.check(objectRefs) within 20 ms p95. | REQ-DOC-004 |  |
| REQ-PLT-240 | J0 | W1 | S | B | execute erasure tasks received from CMP for PLT-owned data… | REQ-CMP-005 |  |
| REQ-PLT-241 | J0 | W1 | S | E | encrypt personal values in audit, exchange archives and event… | — |  |
| REQ-PLT-242 | J0 | W1 | S | B | produce pseudonymised copies for non-production with consistent, format-preserving tokens… | REQ-DAT-001 |  |
| REQ-PLT-243 | J0 | W1 | S | B | generate synthetic sample datasets (tiny, small, large, every product… | — |  |
| REQ-PLT-244 | J0 | W1 | S | E | aggregate each module's declaration of personal-data attributes (P0–P3), purposes… | REQ-CMP-006 |  |
| REQ-PLT-245 | J0 | W1 | S | B | scrub logs and traces of personal data other than… | — |  |
| REQ-PLT-246 | J0 | W1 | S | E | keep backups no longer than their retention (default 35… | — |  |
| REQ-PLT-247 | J0 | W1 | S | B | retention and legal-hold console (SCR-PLT-18). | — |  |
| REQ-PLT-248 | J0 | W1 | S | B | export PLT-held data about a subject (identity data, sign-in… | REQ-CMP-005 |  |
| REQ-PLT-249 | J0 | W1 | S | B | instrument every module through a platform library emitting OpenTelemetry… | — |  |
| REQ-PLT-250 | J0 | W1 | S | B | propagate W3C Trace Context across HTTP calls, events, workflows… | — |  |
| REQ-PLT-251 | J0 | W1 | S | B | emit structured logs with levels and allow per-module log-level… | — |  |
| REQ-PLT-252 | J0 | W1 | S | B | run an EU-hosted monitoring stack per stamp with default… | — |  |
| REQ-PLT-253 | J0 | W1 | S | B | dashboards per module (latency, traffic, errors, saturation) and per… | — |  |
| REQ-PLT-254 | J0 | W1 | S | E | define SLOs as code (SLI, target, window), compute error… | — |  |
| REQ-PLT-255 | J0 | W1 | S | B | compute business SLIs supplied by modules (for example bind… | REQ-CMP-001 |  |
| REQ-PLT-256 | J0 | W1 | S | B | route alerts by service and severity to on-call rotations,… | — |  |
| REQ-PLT-257 | J0 | W1 | S | B | manage on-call schedules, escalation policies and acknowledgement timers. | — |  |
| REQ-PLT-261 | J0 | W1 | S | B | log explorer (SCR-PLT-24) to search logs by module, level,… | — |  |
| REQ-PLT-262 | J0 | W1 | M | B | environment ladder: ephemeral environment per pull request, integration, user… | — |  |
| REQ-PLT-263 | J0 | W1 | S | B | prevent production data from leaving production: non-production environments receive… | — |  |
| REQ-PLT-264 | J0 | W1 | S | B | build one signed artefact per commit on trunk, with… | — |  |
| REQ-PLT-265 | J0 | W1 | M | B | gate the pipeline on unit, contract, architecture-fitness, access-control-matrix, golden-suite… | REQ-MKT-008 |  |
| REQ-PLT-266 | J0 | W1 | S | E | create and update a change record automatically for every… | — |  |
| REQ-PLT-267 | J0 | W1 | S | B | enforce that the change approver is neither the author… | — |  |
| REQ-PLT-268 | J0 | W1 | S | E | deploy progressively (canary or two-slot switch), watch SLOs during… | — |  |
| REQ-PLT-269 | J0 | W1 | S | B | support emergency changes with an expedited path, mandatory retrospective… | — |  |
| REQ-PLT-270 | J0 | W1 | S | B | test the fall-back (rollback) procedure for each release in… | — |  |
| REQ-PLT-271 | J0 | W1 | M | B | define a complete stamp (network, container orchestration cluster with… | — |  |
| REQ-PLT-273 | J0 | W1 | S | B | deploy one stamp per regulated legal entity; only stateless… | — |  |
| REQ-PLT-274 | J0 | W1 | S | E | keep a thin global layer for request routing and… | — |  |
| REQ-PLT-275 | J0 | W1 | S | B | back up databases with continuous point-in-time recovery meeting the… | — |  |
| REQ-PLT-276 | J0 | W1 | S | E | run automated restore drills monthly per stamp (sample databases)… | — |  |
| REQ-PLT-277 | J0 | W1 | S | B | replicate each stamp to a DR site in another… | — |  |
| REQ-PLT-278 | J0 | W1 | S | B | perform at least one full failover test per stamp… | — |  |
| REQ-PLT-281 | J0 | W1 | S | B | hold business continuity plans per critical or important function… | — |  |
| REQ-PLT-283 | J0 | W1 | S | B | keep capacity headroom per NFR-PLT-005 for renewal batches and… | — |  |
| REQ-PLT-284 | J0 | W1 | S | B | keep tested exit plans for the hosting provider and… | — |  |
| REQ-PLT-285 | J0 | W1 | L | B | host all stamps, backups, monitoring and AI model endpoints… | — |  |
| REQ-PLT-286 | J0 | W1 | S | B | encrypt data at rest (databases, backups, object storage, event… | — |  |
| REQ-PLT-287 | J0 | W1 | S | B | field-level encryption for P2/P3 identifiers and IBANs through a… | REQ-PTY-001 |  |
| REQ-PLT-288 | J0 | W1 | S | B | manage keys in a hardware-backed key-management service with rotation,… | — |  |
| REQ-PLT-289 | J0 | W1 | S | B | keep secrets only in the secrets vault, issue short-lived… | — |  |
| REQ-PLT-290 | J0 | W1 | S | B | run static analysis, dynamic testing, dependency and container scanning… | — |  |
| REQ-PLT-291 | J0 | W1 | S | E | track vulnerabilities to closure within remediation targets: critical 7… | — |  |
| REQ-PLT-292 | J0 | W1 | S | B | be penetration-tested by independent testers before go-live, annually and… | — |  |
| REQ-PLT-293 | J0 | W1 | S | B | protect internet-facing endpoints with a web application and API… | REQ-CHN-001 |  |
| REQ-PLT-294 | J0 | W1 | S | B | feed the SIEM with security events from identity, edge… | — |  |
| REQ-PLT-295 | J0 | W1 | S | B | harden the container platform: default-deny network policies, restricted pod… | — |  |
| REQ-PLT-296 | J0 | W1 | S | B | meet OWASP ASVS Level 2 for external applications and… | — |  |
| REQ-PLT-297 | J0 | W1 | S | B | set security headers, a strict content security policy and… | — |  |
| REQ-PLT-299 | J0 | W8 | M | B | record incidents with number, title, source (alert, user report,… | — | W8 |
| REQ-PLT-300 | J0 | W8 | S | B | move incidents through Detected → Triaged → Classified →… | — | W8 |
| REQ-PLT-301 | J0 | W8 | M | E | pre-built, versioned impact queries per critical service that count… | REQ-DAT-001 | W8 |
| REQ-PLT-302 | J0 | W8 | M | B | evaluate the classification criteria and thresholds of BR-PLT-030 …… | — | W8 |
| REQ-PLT-303 | J0 | W8 | M | B | run reporting timers per BR-PLT-034: initial notification due at… | — | W8 |
| REQ-PLT-304 | J0 | W8 | S | E | pre-fill initial, intermediate and final report drafts in the… | — | W8 |
| REQ-PLT-305 | J0 | W8 | S | B | submit reports through the IncidentReportingChannel SPI (accepted, contract R-05),… | REQ-CMP-008 | per contract ruling; W8 |
| REQ-PLT-306 | J0 | W8 | S | B | link incidents to a personal-data breach assessment with a… | — | W8 |
| REQ-PLT-308 | J0 | W8 | S | B | require root-cause analysis, lessons learned and tracked actions (as… | REQ-WRK-001 | W8 |
| REQ-PLT-311 | J0 | W8 | S | E | support exercise mode for incidents: identical flow, flagged as… | — | W8 |
| REQ-PLT-312 | J0 | W8 | M | B | publish IncidentDeclared, IncidentClassified and IncidentReported. | REQ-CMP-008, REQ-WRK-001 | W8 |
| REQ-PLT-313 | J0 | W8 | S | B | maintain an ICT asset inventory (services, components, data stores,… | — | W8 |
| REQ-PLT-314 | J0 | W8 | M | B | maintain the ICT third-party register: providers (LEI or other… | REQ-PTY-001 | W8 |
| REQ-PLT-315 | J0 | W8 | S | B | export the register of information in the templates of… | REQ-CMP-008 | W8 |
| REQ-PLT-316 | J0 | W8 | M | B | record third-party risk assessments (pre-contract due diligence, annual review,… | — | W8 |
| REQ-PLT-317 | J0 | W8 | S | E | check each critical contract against a clause checklist (service… | — | W8 |
| REQ-PLT-318 | J0 | W8 | S | B | hold the resilience-testing programme (vulnerability assessments, network security, gap… | — | W8 |
| REQ-PLT-320 | J0 | W1 | S | B | test in CI that modules use only other modules'… | REQ-MKT-008 |  |
| REQ-PLT-321 | J0 | W1 | S | B | implement architecture tests with an open-source architecture-test library for… | — |  |
| REQ-PLT-322 | J0 | W1 | S | B | give each module its own database role with grants… | — |  |
| REQ-PLT-323 | J0 | W1 | S | B | fail the build if core code imports any country-pack… | REQ-MKT-008 |  |
| REQ-PLT-324 | J0 | W1 | S | B | compute the fork-ratio metric per build: country-pack code relative… | REQ-MKT-008 |  |
| REQ-PLT-327 | J0 | W1 | M | B | staff application shell: global navigation (IB-24 Quiet chrome), entry… | REQ-WRK-006 |  |
| REQ-PLT-328 | J0 | W1 | S | B | let users set preferences: language, region format, legal jurisdiction,… | REQ-MKT-005 |  |
| REQ-PLT-329 | J0 | W1 | M | B | show errors to users as an error page or… | — |  |
| REQ-PLT-330 | J0 | W1 | M | B | batch and workflow monitor (SCR-PLT-10) listing jobs with name,… | — |  |
| REQ-PLT-331 | J0 | W1 | M | B | controlled data change (SCR-PLT-23): requests with reference, description, linked… | — |  |
| REQ-PLT-332 | J0 | W1 | M | B | time service that every module uses to obtain the… | — |  |
| REQ-PLT-333 | J0 | W1 | S | B | load named synthetic sample datasets into non-production environments only,… | — |  |
| REQ-PLT-337 | J0 | W1 | S | B | let each module register its administration screens in the… | REQ-WRK-002 |  |
| REQ-PLT-341 | J0 | W1 | S | B | change log and release dashboard (SCR-PLT-22). | — |  |
| REQ-PLT-344 | J0 | W1 | S | E | service health and SLO dashboard (SCR-PLT-27) showing binary health… | — |  |
| REQ-PLT-345 | J13 | W9 | L | E | import operations conforming to the import-API contract REQ-MIG-001 for… | REQ-MIG-001, REQ-MIG-072 | migration (J13/W9) |
| REQ-PLT-346 | J13 | W9 | M | E | import users (staff exceptions not provisioned from HR, intermediary… | REQ-MIG-002, REQ-MIG-089 | migration (J13/W9) |
| REQ-PLT-347 | J13 | W9 | S | E | apply SoD checks to imported role assignments exactly as… | — | migration (J13/W9) |
| REQ-PLT-348 | J13 | W9 | M | E | import authority profiles, limits and grants only against authority… | — | migration (J13/W9) |
| REQ-PLT-349 | J13 | W9 | M | E | import external-identity invitations that pre-link a future external user… | REQ-PTY-001, REQ-PTY-243, REQ-PTY-013 | migration (J13/W9) |
| REQ-PLT-350 | J13 | W9 | S | E | treat identity-token validation, the authorisation policy library and configuration… | REQ-MIG-004 | migration (J13/W9) |
| REQ-PLT-351 | J0 | W1 | M | B | show inbound and outbound file transfers on the integration… | — |  |
| REQ-PLT-353 | J0 | W1 | L | E | route every model call of AI agents operated by… | REQ-CHN-003 |  |

## A.1.2 MKT (244 Musts, 354 points): 244 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-MKT-001 | J0 | W1 | XL | E | configuration model with six ordered layers (core, group, region:EU,… | REQ-PLT-008, REQ-PLT-004 |  |
| REQ-MKT-002 | J0 | W1 | XL | E | maintain the SPI catalogue of contract §3.5.8 (and SPIs… | REQ-RAT-009, REQ-CMP-001, REQ-PTY-003 |  |
| REQ-MKT-003 | J0 | W1 | XL | E | pack registry and lifecycle: pack manifest, semantic versioning, declared… | REQ-PLT-004, REQ-PLT-005 |  |
| REQ-MKT-004 | J0 | W1 | XL | E | business capability switches per market, legal entity, product line… | REQ-PLT-008, REQ-PLT-004 |  |
| REQ-MKT-005 | J0 | W1 | XL | E | support four independent locale settings — language, region format,… | REQ-PTY-005, REQ-DOC-002 |  |
| REQ-MKT-006 | J0 | W1 | XL | E | currency rules: transaction, functional and group currency roles; rounding… | REQ-PLT-009, REQ-FIN-006 |  |
| REQ-MKT-007 | J0 | W1 | XL | B | hold regime code lists (Solvency II lines of business,… | REQ-PFC-005, REQ-DAT-003 |  |
| REQ-MKT-008 | J0 | W1 | XL | E | run, on every build, a golden suite of about… | REQ-PLT-265 |  |
| REQ-MKT-009 | J0 | W1 | XL | B | supply statutory clock values (duration, unit, calendar, start, stop,… | REQ-CMP-003, REQ-PLT-009 |  |
| REQ-MKT-010 | J13 | W9 | XL | E | migration-baseline configuration state: a configuration snapshot and the pack… | REQ-MIG-001, REQ-MIG-061, REQ-PLT-004 | migration (J13/W9) |
| REQ-MKT-030 | J0 | W1 | S | E | define exactly six configuration layers in fixed precedence order… | — |  |
| REQ-MKT-031 | J0 | W1 | S | E | represent each layer as a set of layer nodes… | — |  |
| REQ-MKT-032 | J0 | W1 | S | E | resolve layer L5 in the order product+channel, then product,… | — |  |
| REQ-MKT-033 | J0 | W1 | M | E | maintain a key registry in which every configuration key… | — |  |
| REQ-MKT-034 | J0 | W1 | S | E | register keys from code-declared descriptors shipped by the owning… | REQ-PLT-008 |  |
| REQ-MKT-035 | J0 | W1 | S | B | support key value types boolean, integer, decimal, percentage, money,… | — |  |
| REQ-MKT-036 | J0 | W1 | S | B | apply merge type REPLACE so that the most specific… | — |  |
| REQ-MKT-037 | J0 | W1 | S | E | apply merge type ADDITIVE_LIST so that the effective list… | — |  |
| REQ-MKT-040 | J0 | W1 | S | E | apply merge type CONSTRAINED_RANGE so that a lower layer… | — |  |
| REQ-MKT-041 | J0 | W1 | S | B | support final values: a value marked final at a… | — |  |
| REQ-MKT-042 | J0 | W1 | S | B | store each value with a business validity period [valid_from,… | — |  |
| REQ-MKT-043 | J0 | W1 | S | E | declare per key a time basis — EFFECTIVE_DATE, TERM_START_DATE,… | REQ-RAT-009 |  |
| REQ-MKT-044 | J0 | W1 | M | E | mkt.Configuration.resolve with inputs legal entity, jurisdiction and subdivision, product… | REQ-PLT-008 |  |
| REQ-MKT-045 | J0 | W1 | S | E | make resolution deterministic: the same hash, context and dates… | — |  |
| REQ-MKT-046 | J0 | W1 | M | E | compute the configuration hash as SHA-256 over the canonical… | — |  |
| REQ-MKT-047 | J0 | W1 | M | B | expose mkt.Configuration.currentHash and require every module to stamp the… | REQ-PLT-005, REQ-POL-001 |  |
| REQ-MKT-048 | J0 | W1 | S | B | rebuild and display the full configuration for any past… | REQ-PLT-011 |  |
| REQ-MKT-050 | J0 | W1 | S | B | cache resolved values in-process per stamp node and invalidate… | REQ-PLT-008 |  |
| REQ-MKT-051 | J0 | W1 | S | E | pin the configuration hash for the duration of a… | REQ-POL-001 |  |
| REQ-MKT-052 | J0 | W1 | S | B | let a configuration administrator create a change request containing… | — |  |
| REQ-MKT-053 | J0 | W1 | M | B | validate each change at draft time for type, range,… | — |  |
| REQ-MKT-054 | J0 | W1 | M | E | produce an impact preview for a change request comprising:… | REQ-RAT-007 |  |
| REQ-MKT-055 | J0 | W1 | S | E | block submission of a change request whose preview changes… | — |  |
| REQ-MKT-056 | J0 | W1 | S | B | require maker-checker approval via REQ-PLT-004 for every change at… | REQ-PLT-004 |  |
| REQ-MKT-057 | J0 | W1 | S | B | register authority type MKT_CONFIG_APPROVAL with dimensions layer, key namespace… | REQ-PLT-003 |  |
| REQ-MKT-058 | J0 | W1 | M | B | schedule activation at an instant in UTC derived from… | — |  |
| REQ-MKT-059 | J0 | W1 | M | E | allow retroactive changes only for keys flagged retro_allowed, require… | — |  |
| REQ-MKT-060 | J0 | W1 | S | B | allow a scheduled activation to be withdrawn before its… | REQ-PLT-002 |  |
| REQ-MKT-061 | J0 | W1 | S | E | detect concurrent change requests touching the same key and… | — |  |
| REQ-MKT-064 | J0 | W1 | S | E | compare any two hashes and list added, removed and… | — |  |
| REQ-MKT-065 | J0 | W1 | S | E | mkt.Configuration.explain returning, for a key and context, the full… | — |  |
| REQ-MKT-066 | J0 | W1 | S | B | write an AuditEvent via REQ-PLT-002 for every create, update,… | REQ-PLT-002 |  |
| REQ-MKT-067 | J0 | W1 | S | B | reject secrets (passwords, keys, tokens) as configuration values and… | — |  |
| REQ-MKT-068 | J0 | W1 | M | B | restrict who may draft changes by layer and namespace:… | REQ-PLT-001 |  |
| REQ-MKT-069 | J0 | W1 | S | E | require a core-layer default for every key, except keys… | — |  |
| REQ-MKT-070 | J0 | W1 | M | E | maintain a capability-switch catalogue in which each switch has… | — |  |
| REQ-MKT-071 | J0 | W1 | S | E | store capability switches as configuration keys of type capability… | — |  |
| REQ-MKT-072 | J0 | W1 | M | E | classify as IRREVERSIBLE at least: ledger posting model, functional… | REQ-PLT-004 |  |
| REQ-MKT-073 | J0 | W1 | S | E | prevent an IRREVERSIBLE switch, once active for a scope,… | — |  |
| REQ-MKT-075 | J0 | W1 | S | E | validate switch dependencies (requires, excludes) at draft and at… | — |  |
| REQ-MKT-076 | J0 | W1 | S | E | forbid business behaviour that differs per market from being… | REQ-PLT-008 |  |
| REQ-MKT-078 | J0 | W1 | S | B | publish ConfigurationActivated with kind = capability when a switch… | REQ-PLT-005 |  |
| REQ-MKT-079 | J0 | W1 | S | E | ship the initial switch catalogue listed in section 10.2.3. | — |  |
| REQ-MKT-080 | J0 | W1 | M | E | define each SPI as a versioned typed interface in… | — |  |
| REQ-MKT-081 | J0 | W1 | S | E | expose mkt.Spi.catalogue returning every SPI with interface versions, callers,… | — |  |
| REQ-MKT-082 | J0 | W1 | S | E | maintain an SPI binding table with rows (legal entity,… | — |  |
| REQ-MKT-083 | J0 | W1 | S | E | resolve a binding by legal entity, SPI, axis value,… | — |  |
| REQ-MKT-084 | J0 | W1 | M | E | enforce at most one binding per (legal entity, SPI,… | — |  |
| REQ-MKT-085 | J0 | W1 | S | E | support binding axes RISK_LOCATION, LEGAL_ENTITY_HOME, BRANCH_LOCATION, POLICYHOLDER_RESIDENCE, CONTRACT_LAW_JURISDICTION, DOCUMENT_LANGUAGE,… | — |  |
| REQ-MKT-086 | J0 | W1 | S | E | invoke SPI implementations only through an SPI gateway that… | REQ-PLT-013 |  |
| REQ-MKT-087 | J0 | W1 | M | B | TaxCalculator SPI as specified in §9.4.4: insurance premium tax,… | REQ-RAT-009, REQ-BIL-002, REQ-FIN-005 |  |
| REQ-MKT-088 | J0 | W1 | M | B | FiscalDocumentChannel SPI as specified in §9.4.5: build, transmit and… | REQ-CMP-001 |  |
| REQ-MKT-090 | J0 | W1 | M | B | IdValidator SPI as specified in §9.4.1, validating and normalising… | REQ-PTY-003 |  |
| REQ-MKT-091 | J0 | W1 | S | B | NameTransliterator SPI as specified in §9.4.2 converting native-script names… | REQ-PTY-012 |  |
| REQ-MKT-092 | J0 | W1 | M | B | AddressFormatter SPI as specified in §9.4.3: parse, validate postcode,… | REQ-PTY-012 |  |
| REQ-MKT-093 | J0 | W1 | S | B | BureauAdapter SPI as specified in §9.4.7 for motor bureau… | REQ-CMP-002 |  |
| REQ-MKT-094 | J0 | W1 | S | B | StatutoryClockSet SPI as specified in §9.4.8 returning clock values… | REQ-CMP-003 |  |
| REQ-MKT-095 | J0 | W1 | S | B | IntermediaryRegister SPI as specified in §9.4.18 to check intermediary… | REQ-PTY-008 |  |
| REQ-MKT-096 | J0 | W1 | S | B | NumberingScheme SPI as specified in §9.4.9 supplying business identifier… | REQ-PLT-014 |  |
| REQ-MKT-097 | J0 | W1 | M | B | HolidayCalendarProvider SPI as specified in §9.4.10 supplying public and… | REQ-PLT-009 |  |
| REQ-MKT-098 | J0 | W1 | S | B | PaymentReferenceGenerator SPI as specified in §9.4.11 creating and validating… | REQ-BIL-004 |  |
| REQ-MKT-099 | J0 | W1 | S | B | ComplaintRules SPI as specified in §9.4.19 supplying acknowledgement and… | REQ-CMP-004 |  |
| REQ-MKT-100 | J0 | W1 | M | B | BankFileFormat SPI as specified in §9.4.12 for payment and… | REQ-BIL-004, REQ-BIL-009 |  |
| REQ-MKT-101 | J0 | W1 | M | B | DocumentLanguageRule SPI as specified in §9.4.15 stating, per document… | REQ-DOC-002 |  |
| REQ-MKT-102 | J0 | W1 | M | B | PayeeVerification SPI as specified in §9.4.13 for verification of… | REQ-BIL-007, REQ-BIL-009 |  |
| REQ-MKT-103 | J0 | W1 | S | B | RefusalDocumentRule SPI as specified in §9.4.20 deciding when a… | REQ-UW-005 |  |
| REQ-MKT-104 | J0 | W1 | S | B | RegimeCodeList SPI as specified in §9.4.14 returning code lists… | REQ-PFC-005 |  |
| REQ-MKT-105 | J0 | W1 | S | B | MotorDataProvider SPI as specified in §9.4.16 for vehicle registry… | REQ-POL-010 |  |
| REQ-MKT-107 | J0 | W1 | S | B | SanctionsListSource SPI as specified in §9.4.21 supplying the lists… | REQ-PTY-006 |  |
| REQ-MKT-108 | J0 | W1 | S | B | ClaimsHistoryFormat SPI as specified in §9.4.22 supplying the content… | REQ-CLM-008 |  |
| REQ-MKT-109 | J0 | W1 | S | B | FriendlySettlementClearing SPI as specified in §9.4.23 for inter-insurer clearing… | REQ-CLM-003 |  |
| REQ-MKT-110 | J0 | W1 | M | E | PricingConstraint SPI (proposed as CCR-MKT-01, accepted by R-05) as… | REQ-RAT-001, REQ-PFC-002 | per contract ruling; CCR (accepted) |
| REQ-MKT-111 | J0 | W1 | M | E | ConsentRules SPI (proposed as CCR-MKT-02, accepted by R-05) as… | REQ-PTY-005 | per contract ruling; CCR (accepted) |
| REQ-MKT-112 | J0 | W1 | S | E | ESignatureProvider SPI (proposed as CCR-MKT-03, accepted by R-05) as… | REQ-DOC-006 | per contract ruling; CCR (accepted) |
| REQ-MKT-114 | J0 | W1 | S | E | TaxReturnFormat SPI (proposed as CCR-MKT-04, accepted by R-05) as… | REQ-FIN-005 | per contract ruling; CCR (accepted) |
| REQ-MKT-115 | J0 | W1 | M | E | MandatoryWordingSet SPI (proposed as CCR-MKT-05, accepted by R-05) as… | REQ-DOC-002, REQ-DOC-003 | per contract ruling; CCR (accepted) |
| REQ-MKT-116 | J0 | W1 | S | E | return SPI failures in a typed error model —… | REQ-PLT-013 |  |
| REQ-MKT-117 | J0 | W1 | M | E | require calculation SPIs (TaxCalculator, RegimeCodeList, StatutoryClockSet, NameTransliterator, AddressFormatter formatting,… | — |  |
| REQ-MKT-118 | J0 | W1 | M | B | run external-call SPIs (FiscalDocumentChannel, EInvoiceProvider, BureauAdapter, RegistryLookup, IntermediaryRegister, MotorDataProvider,… | REQ-PLT-006 |  |
| REQ-MKT-119 | J0 | W1 | S | E | record each SPI call in an SPI call log… | REQ-PLT-002 |  |
| REQ-MKT-120 | J0 | W1 | S | E | version SPI interfaces semantically and support the current and… | — |  |
| REQ-MKT-121 | J0 | W1 | M | E | accept a new SPI only with: evidence that the… | — |  |
| REQ-MKT-122 | J0 | W1 | S | E | forbid generic escape-hatch interfaces (untyped maps, script execution, reflective… | — |  |
| REQ-MKT-123 | J0 | W1 | S | E | provide, per SPI, a conformance test kit (contract tests,… | — |  |
| REQ-MKT-125 | J0 | W1 | M | E | describe every pack by a manifest containing pack id,… | — |  |
| REQ-MKT-126 | J0 | W1 | M | E | support these pack content types: SPI implementations, configuration layer… | — |  |
| REQ-MKT-127 | J0 | W1 | S | E | deliver region:EU layer values as an eu pack (risk-location… | — |  |
| REQ-MKT-128 | J0 | W1 | S | E | deliver group layer values as a group pack (group… | REQ-FIN-006 |  |
| REQ-MKT-129 | J0 | W1 | M | B | apply semantic versioning to packs: MAJOR for removed keys,… | — |  |
| REQ-MKT-130 | J0 | W1 | M | E | build each pack in a pipeline that runs pack… | — |  |
| REQ-MKT-131 | J0 | W1 | M | E | hot-fix path for short-notice changes to pack data (for… | REQ-PLT-004 |  |
| REQ-MKT-132 | J0 | W1 | S | B | verify pack signatures with keys held in the platform… | REQ-PLT-012 |  |
| REQ-MKT-133 | J0 | W1 | S | B | check, at load and when scheduling activation, that the… | — |  |
| REQ-MKT-134 | J0 | W1 | S | E | certify a pack version only when conformance kits, golden… | REQ-CMP-003 |  |
| REQ-MKT-135 | J0 | W1 | S | E | activate a certified pack version per legal entity at… | REQ-PLT-005 |  |
| REQ-MKT-136 | J0 | W1 | S | E | show an activation preview listing binding changes, value changes… | — |  |
| REQ-MKT-137 | J0 | W1 | S | B | require maker-checker approval for every production pack activation and… | REQ-PLT-004 |  |
| REQ-MKT-138 | J0 | W1 | M | B | roll back by activating the previous (or any earlier… | REQ-PLT-005 |  |
| REQ-MKT-139 | J0 | W1 | S | E | refuse a rollback instant earlier than the activation instant… | — |  |
| REQ-MKT-141 | J0 | W1 | S | B | remove a pack version from the runtime load set… | REQ-DOC-004 |  |
| REQ-MKT-143 | J0 | W1 | S | B | resolve pack dependencies at certification and activation and refuse… | — |  |
| REQ-MKT-144 | J0 | W1 | S | E | expose a pack registry API (mkt.Pack.list, get, certify, scheduleActivation,… | — |  |
| REQ-MKT-145 | J0 | W1 | S | E | allow data-only pack versions (PATCH or MINOR without code… | — |  |
| REQ-MKT-146 | J0 | W1 | S | E | prevent a pack flagged test_only from being certified for… | — | test-only fixture |
| REQ-MKT-147 | J0 | W1 | S | B | require review by the pack's named country owners for… | — |  |
| REQ-MKT-148 | J0 | W1 | S | E | maintain in each pack a rule index linking every… | REQ-CMP-006 |  |
| REQ-MKT-149 | J0 | W1 | S | B | publish PackActivated, PackRolledBack and the PackDeprecated events (R-02). | REQ-PLT-005 | per contract ruling |
| REQ-MKT-150 | J0 | W1 | M | E | implement tenancy as: one deployment stamp per regulated legal… | REQ-PLT-001 |  |
| REQ-MKT-151 | J0 | W1 | M | E | maintain a legal entity registry (entity LegalEntity, CCR-MKT-11 accepted… | — | per contract ruling; CCR (accepted) |
| REQ-MKT-152 | J0 | W1 | S | E | maintain a stamp registry with stamp id, hosting region… | REQ-PLT-013 |  |
| REQ-MKT-153 | J0 | W1 | S | E | define partitions (brand, channel, bancassurance partner, branch) within a… | REQ-PLT-001 |  |
| REQ-MKT-160 | J0 | W1 | S | B | isolate stamps: separate encryption keys, secrets, event stream namespaces,… | REQ-PLT-005 |  |
| REQ-MKT-162 | J0 | W1 | S | E | manage legal entity status Planned → Onboarding → Active… | — |  |
| REQ-MKT-165 | J0 | W1 | S | E | hold four independent locale settings — language (BCP 47… | REQ-PLT-001 |  |
| REQ-MKT-166 | J0 | W1 | M | E | define the same four settings for customers (from PTY… | REQ-PTY-005, REQ-DOC-001 |  |
| REQ-MKT-167 | J0 | W1 | S | E | never derive language, region format, jurisdiction or currency from… | — |  |
| REQ-MKT-168 | J0 | W1 | S | B | configure supported languages per legal entity (key l10n.languages), with… | — |  |
| REQ-MKT-169 | J0 | W1 | S | B | format dates, times, numbers, percentages and currency amounts by… | — |  |
| REQ-MKT-170 | J0 | W1 | S | E | pin the CLDR data version used for formatting and… | — |  |
| REQ-MKT-171 | J0 | W1 | S | B | store UI strings and system messages as translation entries… | — |  |
| REQ-MKT-172 | J0 | W1 | S | E | publish translations as versioned translation bundles whose digest is… | — |  |
| REQ-MKT-173 | J0 | W1 | S | B | run a translation workflow Missing → Draft → InReview… | — |  |
| REQ-MKT-174 | J0 | W1 | S | E | fail a release build when any shipped message key… | — |  |
| REQ-MKT-175 | J0 | W1 | M | E | fall back at runtime for staff UI strings from… | REQ-DOC-001 |  |
| REQ-MKT-176 | J0 | W1 | S | B | accept and store text in Unicode UTF-8, normalise input… | — |  |
| REQ-MKT-177 | J0 | W1 | M | B | transliterate native-script names and address lines through NameTransliterator at… | REQ-PTY-012 |  |
| REQ-MKT-178 | J0 | W1 | M | E | search-key library that produces accent-insensitive, case-insensitive (including Greek final… | REQ-PTY-001, REQ-WRK-006 |  |
| REQ-MKT-179 | J0 | W1 | S | B | sort lists by the Unicode Collation Algorithm with the… | — |  |
| REQ-MKT-180 | J0 | W1 | S | B | validate script per field where a field requires a… | REQ-BIL-009 |  |
| REQ-MKT-181 | J0 | W1 | S | E | support the Bulgarian Cyrillic script end to end in… | — | test-only fixture |
| REQ-MKT-183 | J0 | W1 | S | B | tag the language of every text run in UI… | REQ-DOC-001 |  |
| REQ-MKT-187 | J0 | W1 | S | B | store timestamps in UTC and compute business dates in… | — |  |
| REQ-MKT-188 | J0 | W1 | S | B | format personal and organisation names, addresses and phone numbers… | REQ-PTY-012 |  |
| REQ-MKT-189 | J0 | W1 | S | E | mkt.L10n.bundle and mkt.L10n.format APIs so that front ends obtain… | REQ-CHN-004 |  |
| REQ-MKT-190 | J0 | W1 | M | B | define currency roles: transaction currency (per policy, invoice, payment,… | REQ-FIN-006 |  |
| REQ-MKT-191 | J0 | W1 | S | B | take minor units of each currency from ISO 4217… | REQ-PLT-009 |  |
| REQ-MKT-192 | J0 | W1 | S | B | hold rounding rules as keys cur.rounding.<purpose> with mode (HALF_UP,… | — |  |
| REQ-MKT-193 | J0 | W1 | S | B | allow each pack to define rounding rules per tax… | REQ-RAT-009 |  |
| REQ-MKT-194 | J0 | W1 | M | E | configure the order of operations for money calculations via… | REQ-RAT-009, REQ-BIL-002 |  |
| REQ-MKT-195 | J0 | W1 | M | B | mkt.Rounding.apply(amount, currency, purpose, context) returning the amount after rounding,… | REQ-RAT-001, REQ-BIL-002 |  |
| REQ-MKT-196 | J0 | W1 | M | E | map each money purpose (transaction booking, revaluation, group reporting,… | REQ-FIN-006, REQ-PLT-009 |  |
| REQ-MKT-197 | J0 | W1 | S | E | define the missing-rate policy per rate type (use latest… | REQ-PLT-009 |  |
| REQ-MKT-198 | J0 | W1 | M | E | hold the MKT-governed parts of a currency changeover —… | REQ-MIG-001, REQ-PLT-004 |  |
| REQ-MKT-199 | J0 | W1 | S | E | mkt.Currency.convertFixed converting at the plan's rate without inverse rates,… | — |  |
| REQ-MKT-204 | J0 | W1 | S | B | FIN with the group reporting rate-type rules so that… | REQ-FIN-006 |  |
| REQ-MKT-205 | J0 | W1 | S | B | store each regime code list with regime id (for… | — |  |
| REQ-MKT-206 | J0 | W1 | S | B | publish a code list version through draft, review and… | REQ-PLT-004 |  |
| REQ-MKT-207 | J0 | W1 | S | E | hold crosswalks between taxonomy versions (one-to-one, one-to-many with split… | REQ-DAT-003 |  |
| REQ-MKT-208 | J0 | W1 | S | B | expose mkt.RegimeCode.list, get and validate(regime, code, date) for PFC,… | REQ-PFC-005 |  |
| REQ-MKT-209 | J0 | W1 | S | B | prevent retired codes from validating for dates after their… | — |  |
| REQ-MKT-210 | J0 | W1 | S | E | declare, per jurisdiction, the regimes mandatory for every coverage… | REQ-PFC-005 |  |
| REQ-MKT-212 | J0 | W1 | S | E | publish the event RegimeCodeListPublished (R-02) on publication or retirement. | REQ-PLT-005 | per contract ruling |
| REQ-MKT-213 | J0 | W1 | S | E | keep the SII_LOB list in the eu pack and… | — |  |
| REQ-MKT-216 | J0 | W8 | M | B | mkt.RiskLocation.resolve(riskType, attributes, policyholder) applying Solvency II Art. 13(13): buildings… | REQ-POL-010 | W8 |
| REQ-MKT-221 | J0 | W8 | S | E | compute the business basis (DOMESTIC, FOS, FOE) for a… | REQ-POL-001 | W8 |
| REQ-MKT-226 | J0 | W8 | S | B | supply DAT with cross-border attributes (business basis, host State,… | REQ-DAT-003 | W8 |
| REQ-MKT-230 | J0 | W1 | S | E | run architecture tests ARCH-01…ARCH-10 (catalogue in §14.x.3) on every… | — |  |
| REQ-MKT-231 | J0 | W1 | S | E | fail ARCH-01 when core code compares a jurisdiction, country… | — |  |
| REQ-MKT-232 | J0 | W1 | S | E | fail ARCH-02 when a core module imports or reflects… | — |  |
| REQ-MKT-233 | J0 | W1 | S | E | fail ARCH-03 when core source (outside resource bundles, tests… | — |  |
| REQ-MKT-234 | J0 | W1 | S | E | fail ARCH-04 when core business code contains currency-code literals… | — |  |
| REQ-MKT-235 | J0 | W1 | S | E | maintain an exceptions register for architecture rules with justification,… | — |  |
| REQ-MKT-236 | J0 | W1 | M | E | compute the fork-ratio metric on every build: pack-specific lines… | — |  |
| REQ-MKT-237 | J0 | W1 | S | E | warn when a pack's fork ratio exceeds gov.fork_ratio.warn (default… | — |  |
| REQ-MKT-238 | J0 | W1 | S | E | keep the golden suite (§14.x.1) as versioned scenario definitions… | — | see XMR-F-420 (6-month GS-02 vs annual-only MVP) |
| REQ-MKT-239 | J0 | W1 | S | E | run the full golden suite against the Greece pack… | — | see XMR-F-420 (6-month GS-02 vs annual-only MVP) |
| REQ-MKT-240 | J0 | W1 | S | E | assert core invariants for every scenario regardless of pack… | — |  |
| REQ-MKT-241 | J0 | W1 | S | E | show a Greece-versus-Cyprus divergence report per scenario listing expected… | — |  |
| REQ-MKT-242 | J0 | W1 | S | E | run every golden scenario twice per build and fail… | — |  |
| REQ-MKT-243 | J0 | W1 | S | E | run property-based tests of merge semantics with the invariants… | — |  |
| REQ-MKT-244 | J0 | W1 | S | E | run localisation tests for scripts, transliteration, collation, rounding and… | — |  |
| REQ-MKT-245 | J0 | W1 | S | E | run the changeover test with the Bulgaria fixture on… | — | test-only fixture |
| REQ-MKT-246 | J0 | W1 | M | E | maintain a market-readiness scorecard per market with dimensions: legal… | — |  |
| REQ-MKT-249 | J0 | W1 | S | E | reuse the golden-suite runner for impact previews of configuration… | — |  |
| REQ-MKT-250 | J0 | W1 | S | B | use only synthetic data in golden scenarios and fixtures,… | — | synthetic/not law |
| REQ-MKT-254 | J0 | W1 | S | E | verify statutory clock values, holiday content and regime code… | — |  |
| REQ-MKT-255 | J0 | W1 | S | B | Greece pack (gr) as the first full production implementation,… | — |  |
| REQ-MKT-256 | J0 | W1 | S | E | maintain the Greece pack index (§10.4.2) as pack metadata… | REQ-CMP-006 |  |
| REQ-MKT-257 | J0 | W1 | M | B | implement TaxCalculator for Greek premium tax by tax class… | REQ-RAT-009, REQ-FIN-005 |  |
| REQ-MKT-258 | J0 | W1 | S | B | implement FiscalDocumentChannel for AADE myDATA (document types, series, registration… | REQ-CMP-001 |  |
| REQ-MKT-259 | J0 | W1 | M | B | implement BureauAdapter (Information Centre reporting of insured vehicles), MotorDataProvider… | REQ-CMP-002, REQ-CLM-003 |  |
| REQ-MKT-260 | J0 | W1 | M | B | implement IdValidator (AFM with mod-11 check digit, GEMI, VAT… | REQ-PTY-003, REQ-PTY-008 |  |
| REQ-MKT-261 | J0 | W1 | M | B | supply HolidayCalendarProvider content (national holidays including Orthodox Easter-based movable… | REQ-PLT-009, REQ-CMP-003 |  |
| REQ-MKT-262 | J0 | W1 | M | B | supply DocumentLanguageRule (Greek binding, English informative), RefusalDocumentRule (natural-catastrophe refusals),… | REQ-DOC-002, REQ-UW-005 |  |
| REQ-MKT-263 | J0 | W1 | M | E | Cyprus stub pack (cy-stub) that is deliberately different from… | — | Cyprus stub pack |
| REQ-MKT-264 | J0 | W1 | S | E | implement IdValidator scheme TIC as 8 digits followed by… | — | Cyprus stub pack content |
| REQ-MKT-265 | J0 | W1 | L | E | implement a synthetic test fixture CY-STAMP (not law: the… | REQ-RAT-009 | Cyprus stub pack content; synthetic/not law |
| REQ-MKT-266 | J0 | W1 | S | E | implement a motor insurers' fund levy as a percentage… | REQ-RAT-009 | Cyprus stub pack content; assumed value |
| REQ-MKT-267 | J0 | W1 | S | E | implement FiscalDocumentChannel as a no-op returning NotRequired and BureauAdapter… | REQ-CMP-001 | Cyprus stub pack content |
| REQ-MKT-268 | J0 | W1 | S | E | bind English as the binding document language with Greek… | REQ-DOC-002 | Cyprus stub pack content |
| REQ-MKT-269 | J0 | W1 | S | E | supply its own holiday calendar content, statutory clock values… | REQ-PLT-014 | Cyprus stub pack content |
| REQ-MKT-270 | J0 | W1 | S | E | Bulgaria fixture (bg-fixture, test_only) used only in tests. | — | test-only fixture |
| REQ-MKT-271 | J0 | W1 | S | E | implement Cyrillic names and addresses with a transliteration rule… | — | Bulgaria test-only fixture |
| REQ-MKT-272 | J0 | W1 | S | E | implement a 2% premium tax and a fixed per-vehicle… | REQ-RAT-009 | Bulgaria test-only fixture; synthetic/not law |
| REQ-MKT-273 | J0 | W1 | S | E | implement a BGN→EUR changeover at 1.95583 on 2026-01-01 with… | — | Bulgaria test-only fixture |
| REQ-MKT-285 | J0 | W1 | S | B | hold, per clock code and jurisdiction, effective-dated values: duration,… | REQ-CMP-003 |  |
| REQ-MKT-286 | J0 | W1 | S | B | return clock values for the clock's start date (time… | REQ-CMP-003 |  |
| REQ-MKT-287 | J0 | W1 | S | B | supply EU-layer defaults in the eu pack: withdrawal period… | REQ-CHN-007 |  |
| REQ-MKT-288 | J0 | W1 | S | E | treat EU minimum protections as CONSTRAINED values that country… | — |  |
| REQ-MKT-289 | J0 | W1 | S | B | require every calendar id referenced by a clock value… | REQ-PLT-009 |  |
| REQ-MKT-290 | J0 | W1 | S | E | fail pack certification when any CMP clock definition applicable… | REQ-CMP-003 |  |
| REQ-MKT-291 | J0 | W1 | S | B | expose mkt.StatutoryClockSet.get(clockCode, jurisdiction, date) and list(jurisdiction, date). | REQ-CMP-003 |  |
| REQ-MKT-293 | J0 | W1 | S | E | configuration explorer (SCR-MKT-01) showing effective values for an entity,… | — |  |
| REQ-MKT-294 | J0 | W1 | S | E | configuration change request screen (SCR-MKT-02) with draft editing, validation,… | REQ-PLT-004 |  |
| REQ-MKT-295 | J0 | W1 | S | E | pack registry screen (SCR-MKT-03) with versions, compatibility, certification, activation… | — |  |
| REQ-MKT-296 | J0 | W1 | S | E | SPI catalogue browser (SCR-MKT-04) with implementation status per country… | — |  |
| REQ-MKT-297 | J0 | W1 | S | E | translation management and the missing-translation report (SCR-MKT-05). | — |  |
| REQ-MKT-298 | J0 | W1 | S | E | market-readiness scorecard and fork-ratio dashboard (SCR-MKT-06). | — |  |
| REQ-MKT-299 | J0 | W1 | S | E | golden-suite results screen comparing Greece and Cyprus outputs (SCR-MKT-07). | — |  |
| REQ-MKT-300 | J0 | W1 | S | E | capability switch board (SCR-MKT-08). | — |  |
| REQ-MKT-301 | J0 | W1 | S | E | legal entity and stamp registry (SCR-MKT-09). | — |  |
| REQ-MKT-302 | J0 | W1 | S | E | regime code list manager (SCR-MKT-10). | — |  |
| REQ-MKT-305 | J0 | W1 | M | E | Geocoder SPI as specified in §9.4.31: address geocoding with… | REQ-PTY-012, REQ-RAT-001 |  |
| REQ-MKT-306 | J0 | W1 | M | E | extend PricingConstraint (§9.4.24) to return, per jurisdiction and line,… | REQ-RAT-001, REQ-PFC-002 |  |
| REQ-MKT-307 | J0 | W1 | S | B | extend RefusalDocumentRule (§9.4.20) with responseDeadline(requestType, jurisdiction, date) and registerExport(afm,… | REQ-UW-005 |  |
| REQ-MKT-308 | J0 | W1 | M | B | PolicyLifecycleRules SPI as specified in §9.4.32 (void and withdrawal… | REQ-POL-004, REQ-BIL-007 |  |
| REQ-MKT-309 | J0 | W1 | M | B | implement PolicyLifecycleRules so that withdrawal from an insurance contract… | REQ-POL-001, REQ-CHN-007 |  |
| REQ-MKT-310 | J0 | W1 | S | B | MotorCompensationBodyAdapter SPI as specified in §9.4.33 for exchanges with… | REQ-CLM-003 |  |
| REQ-MKT-311 | J0 | W1 | M | B | StatutoryDeliveryRule SPI as specified in §9.4.34 returning, per document… | REQ-DOC-005, REQ-BIL-006 |  |
| REQ-MKT-312 | J0 | W1 | M | B | PaymentChannelProvider SPI as specified in §9.4.35 (createCollection, parseNotification, capabilities)… | REQ-BIL-004, REQ-CHN-004 |  |
| REQ-MKT-313 | J0 | W1 | S | B | extend FriendlySettlementClearing (§9.4.23) with evaluateEligibility, submitDispute, recordReply and receiveNotification… | REQ-CLM-003 |  |
| REQ-MKT-314 | J0 | W1 | S | B | extend TaxReturnFormat (§9.4.28) to cover detail lines, adjustments, validation… | REQ-FIN-005 |  |
| REQ-MKT-317 | J0 | W1 | S | B | InboundDocumentProfile SPI as specified in §9.4.36 (document classes, extraction… | REQ-WRK-005 |  |
| REQ-MKT-319 | J0 | W1 | S | B | IncidentReportingChannel SPI as specified in §9.4.38 for submitting DORA… | REQ-PLT-012 |  |
| REQ-MKT-320 | J0 | W1 | S | B | FxRateSource SPI as specified in §9.4.39 naming the official… | REQ-PLT-009 |  |
| REQ-MKT-321 | J0 | W1 | S | B | supply StatutoryClockSet values for every clock code added by… | REQ-CMP-003 |  |
| REQ-MKT-322 | J0 | W1 | L | B | model the Auxiliary Fund contribution under Law 5113/2024 Art.… | REQ-RAT-009, REQ-BIL-002, REQ-FIN-005 |  |
| REQ-MKT-323 | J0 | W1 | S | B | include the CHN transaction-permission matrix version in the configuration… | REQ-CHN-002 |  |
| REQ-MKT-324 | J0 | W1 | S | B | obtain the current time for PROCESSING_DATE resolution, activation scheduling… | REQ-PLT-332 |  |
| REQ-MKT-325 | J0 | W1 | M | B | LegacyDataProfile SPI as specified in §9.4.41 for country-specific legacy… | REQ-MIG-046 |  |
| REQ-MKT-326 | J0 | W1 | L | B | StatutoryDataReturnFormat SPI as specified in §9.4.42 (layouts, build, validation… | REQ-DAT-128, REQ-DAT-003, REQ-CMP-181, REQ-CMP-008 |  |
| REQ-MKT-327 | J0 | W1 | L | B | hold the shared code lists of contract R-84 as… | REQ-CHN-317, REQ-PFC-011, REQ-PTY-010, REQ-BIL-003, REQ-POL-012 … | per contract ruling; deps list truncated in source |
| REQ-MKT-328 | J0 | W1 | M | B | hold the IPT liability point as pack key tax.ipt.liability_point… | REQ-BIL-002, REQ-FIN-005 |  |
| REQ-MKT-329 | J0 | W1 | M | B | register every MKT AI feature in the CMP AI… | REQ-PLT-010, REQ-CMP-007 |  |

## A.1.3 PTY (218 Musts, 377 points): 218 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-PTY-001 | J1 | W2 | XL | B | maintain the party master record (Person or Organisation, independent… | REQ-PLT-001, REQ-MKT-005 |  |
| REQ-PTY-002 | J1 | W2 | XL | B | assign roles to parties from the role catalogue (policyholder,… | — |  |
| REQ-PTY-003 | J1 | W2 | XL | B | validate and normalise every party identifier through the IdValidator… | REQ-MKT-002 |  |
| REQ-PTY-004 | J1 | W2 | XL | B | own the Account entity (commercial container of policies with… | REQ-PLT-014 |  |
| REQ-PTY-005 | J1 | W2 | XL | B | expose pty.Consent.query and pty.CommunicationPreference.resolve returning, for a party and… | REQ-DOC-005 |  |
| REQ-PTY-006 | J1 | W2 | XL | B | synchronous sanctions screening operation pty.Screening.screen for a party or… | REQ-POL-003, REQ-BIL-007, REQ-BIL-009, REQ-CLM-004 |  |
| REQ-PTY-007 | J1 | W6 | XL | B | merge duplicate parties into a survivor (survivor keeps its… | REQ-PLT-004 |  |
| REQ-PTY-008 | J1 | W2 | XL | B | maintain the intermediary and producer-code registry and expose pty.ProducerCode.validate(producerCode,… | REQ-POL-003 |  |
| REQ-PTY-009 | J1 | W2 | XL | B | hold the producer of record per policy term, effective-dated,… | REQ-POL-001, REQ-BIL-008 |  |
| REQ-PTY-010 | J1 | W6 | XL | B | resolve commission agreements deterministically through pty.CommissionAgreement.resolve(producerCode, product, channel, transactionType,… | REQ-BIL-008 |  |
| REQ-PTY-011 | J1 | W2 | XL | B | expose pty.Vulnerability.query(partyId, date) returning whether the party is flagged… | — |  |
| REQ-PTY-012 | J1 | W2 | XL | B | store addresses and contact points as typed, effective-dated rows;… | REQ-MKT-002 |  |
| REQ-PTY-013 | J13 | W9 | XL | B | pty.Import.parties (and equivalents for accounts, intermediaries, producer codes, producer-of-record… | REQ-MIG-001, REQ-MIG-002 | migration (J13/W9) |
| REQ-PTY-030 | J1 | W2 | S | B | model every party as exactly one of Person or… | — |  |
| REQ-PTY-031 | J1 | W2 | M | B | hold for a Person: given name(s), family name, father's… | — |  |
| REQ-PTY-032 | J1 | W2 | M | B | hold for an Organisation: registered legal name, trade name… | — |  |
| REQ-PTY-033 | J1 | W2 | S | B | manage a party status lifecycle with states Prospect, Active,… | — |  |
| REQ-PTY-034 | J1 | W2 | S | B | issue a party number from the PLT numbering service… | REQ-PLT-014 |  |
| REQ-PTY-035 | J1 | W2 | M | B | stamp every party-domain row with legal_entity_id, jurisdiction, created_at, created_by,… | REQ-PLT-001 |  |
| REQ-PTY-036 | J1 | W2 | S | E | keep party, name, identifier, address, contact point, role and… | REQ-POL-002 |  |
| REQ-PTY-038 | J1 | W2 | S | B | require a change reason from a configurable reason list… | REQ-PLT-002 |  |
| REQ-PTY-039 | J1 | W2 | M | B | store multiple nationalities and citizenships per person, each with… | REQ-PLT-001 |  |
| REQ-PTY-040 | J1 | W2 | S | B | record a date of death with source (family notification,… | — |  |
| REQ-PTY-041 | J1 | W2 | S | B | hold a preferred correspondence language per party (default from… | REQ-MKT-005 |  |
| REQ-PTY-042 | J1 | W2 | M | B | group party attributes into named attribute groups (IDENTITY, NAME,… | REQ-PLT-005 |  |
| REQ-PTY-043 | J1 | W2 | S | B | apply optimistic concurrency on record_version; a stale update shall… | — |  |
| REQ-PTY-044 | J1 | W2 | M | B | mask P2 attributes (identifier values, birth date for roles… | REQ-PLT-001, REQ-PLT-002 |  |
| REQ-PTY-046 | J1 | W2 | M | B | store each identifier as a typed row with scheme,… | REQ-MKT-002 |  |
| REQ-PTY-047 | J1 | W2 | M | B | maintain an identifier scheme catalogue as configuration per country… | REQ-MKT-001 |  |
| REQ-PTY-048 | J1 | W2 | S | B | call IdValidator synchronously on entry and on import, returning… | REQ-MKT-002 |  |
| REQ-PTY-049 | J1 | W2 | S | B | the AFM validator shall accept exactly nine digits, reject… | REQ-MKT-002 | Greece pack |
| REQ-PTY-051 | J1 | W2 | S | B | validate LEI values as 20 alphanumeric characters with ISO… | — |  |
| REQ-PTY-052 | J1 | W2 | M | B | enforce that an identifier (scheme, normalised value, issuing country)… | — |  |
| REQ-PTY-053 | J1 | W2 | M | B | apply verification status transitions SelfDeclared → DocumentVerified or RegistryVerified;… | — |  |
| REQ-PTY-054 | J1 | W2 | M | B | look up organisations and sole traders through the RegistryLookup… | REQ-PLT-006 |  |
| REQ-PTY-055 | J1 | W2 | M | E | present registry data that differs from the stored party… | — |  |
| REQ-PTY-056 | J1 | W2 | M | B | support document verification: a user links an inbound document… | REQ-WRK-005, REQ-DOC-004 |  |
| REQ-PTY-059 | J1 | W2 | S | B | store the DOY (tax office) as a coded attribute… | REQ-CMP-001 |  |
| REQ-PTY-060 | J1 | W2 | S | B | encrypt identifier values at field level with keys held… | REQ-PLT-001 |  |
| REQ-PTY-061 | J1 | W2 | M | B | hold name components in the party's native script (persons:… | REQ-MKT-005 |  |
| REQ-PTY-062 | J1 | W2 | M | B | generate a Latin form of every non-Latin name and… | REQ-MKT-002 |  |
| REQ-PTY-063 | J1 | W2 | M | B | hold a separate, user-editable "as on ID document" Latin… | — |  |
| REQ-PTY-065 | J1 | W2 | M | B | maintain a normalised search key for every name and… | REQ-MKT-005 |  |
| REQ-PTY-066 | J1 | W2 | S | B | index normalised and cross-script keys with trigram indexes and… | — |  |
| REQ-PTY-067 | J1 | W2 | S | E | transliterate a Greek query to Latin and a Latin… | — |  |
| REQ-PTY-068 | J1 | W2 | M | B | pty.Party.search and pty.Account.search with criteria: name (person or organisation,… | REQ-POL-014 |  |
| REQ-PTY-069 | J1 | W2 | M | B | return search results with party or account number, display… | — |  |
| REQ-PTY-070 | J1 | W2 | S | B | restrict search results by ABAC: intermediary users see only… | REQ-PLT-001 |  |
| REQ-PTY-071 | J1 | W2 | S | B | complete party and account searches within 1 second at… | — |  |
| REQ-PTY-073 | J1 | W2 | S | B | publish party and account search documents (display names, numbers,… | REQ-WRK-006 |  |
| REQ-PTY-074 | J1 | W2 | S | B | hold addresses typed as Legal (registered seat or residence),… | — |  |
| REQ-PTY-075 | J1 | W2 | M | B | hold structured address fields (street name, street number, building… | REQ-MKT-002 |  |
| REQ-PTY-076 | J1 | W2 | M | B | the AddressFormatter shall validate Greek postcodes as five digits,… | REQ-MKT-002 | Greece pack |
| REQ-PTY-077 | J1 | W2 | M | E | request geocoding of Garaging addresses (motor, P1) and Risk… | REQ-PLT-006 |  |
| REQ-PTY-078 | J1 | W2 | M | B | propagate a party's Mailing or Legal address change to… | REQ-WRK-001, REQ-POL-001 |  |
| REQ-PTY-081 | J1 | W2 | S | B | hold contact points typed as Mobile phone, Landline (home),… | — |  |
| REQ-PTY-082 | J1 | W2 | S | B | normalise phone numbers to E.164 with the country pack's… | REQ-MKT-005 |  |
| REQ-PTY-083 | J1 | W2 | S | E | validate email syntax on entry and verify email ownership… | REQ-CHN-009 |  |
| REQ-PTY-084 | J1 | W2 | S | E | downgrade an email contact point to status Bouncing after… | REQ-DOC-005 |  |
| REQ-PTY-085 | J1 | W2 | S | B | hold account locations (location number, location code, location name,… | — |  |
| REQ-PTY-086 | J1 | W2 | S | B | refuse to deactivate or remove an account location referenced… | REQ-POL-002 |  |
| REQ-PTY-088 | J1 | W2 | S | B | format addresses of countries without an active pack using… | REQ-MKT-002 |  |
| REQ-PTY-090 | J1 | W2 | S | B | allow a user to add and remove party addresses… | — |  |
| REQ-PTY-091 | J1 | W2 | M | B | hold the role-type catalogue as configuration (code, Greek and… | REQ-MKT-001 |  |
| REQ-PTY-092 | J1 | W2 | M | B | record each role assignment with role type, valid period,… | REQ-POL-010 |  |
| REQ-PTY-093 | J1 | W2 | M | B | enforce a required-data profile per role at assignment (for… | REQ-POL-010 |  |
| REQ-PTY-094 | J1 | W2 | M | B | maintain role assignments automatically from consumed events: policyholder, insured,… | REQ-POL-001, REQ-CLM-001 |  |
| REQ-PTY-095 | J1 | W2 | S | B | show a party's roles with additional information per role… | — |  |
| REQ-PTY-097 | J1 | W2 | L | B | hold reinsurer and reinsurance broker master data (legal name,… | REQ-RI-001, REQ-RI-046, REQ-RI-047, REQ-RI-048, REQ-RI-051 |  |
| REQ-PTY-098 | J1 | W2 | M | B | create an account in one step from a new… | — |  |
| REQ-PTY-099 | J1 | W2 | S | B | issue an account number from the PLT numbering service… | REQ-PLT-014 |  |
| REQ-PTY-100 | J1 | W2 | S | B | manage account status Pending, Active, Withdrawn, Merged and Closed… | REQ-POL-002 |  |
| REQ-PTY-101 | J1 | W2 | M | B | hold account members (parties with an account-level role: Account… | — |  |
| REQ-PTY-102 | J1 | W2 | M | B | model a household as a Personal account whose members… | REQ-RAT-001 |  |
| REQ-PTY-104 | J1 | W2 | M | B | hold relationship types as configuration with reciprocal labels in… | REQ-MKT-001 |  |
| REQ-PTY-106 | J1 | W2 | S | B | hold producer-of-record defaults on the account (default producer code… | REQ-POL-001 |  |
| REQ-PTY-108 | J1 | W2 | M | B | maintain account summary measures as read models refreshed from… | REQ-POL-002, REQ-CLM-009, REQ-BIL-001 |  |
| REQ-PTY-110 | J1 | W2 | S | B | refuse to remove an account member who holds the… | REQ-POL-002 |  |
| REQ-PTY-111 | J1 | W2 | S | B | allow an account member to be created from a… | — |  |
| REQ-PTY-113 | J1 | W6 | M | B | life-event wizard with a configurable catalogue: death, marriage or… | REQ-PLT-007 |  |
| REQ-PTY-114 | J1 | W6 | M | B | run the death-of-policyholder workflow: record date of death with… | REQ-WRK-001, REQ-BIL-001, REQ-POL-001 |  |
| REQ-PTY-115 | J1 | W6 | M | B | run the divorce or separation workflow: end the spouse… | — |  |
| REQ-PTY-118 | J1 | W6 | M | B | define per life-event type a notification matrix (customer, trusted… | REQ-WRK-007, REQ-DOC-001 |  |
| REQ-PTY-119 | J1 | W6 | S | B | apply life-event effects to consents and preferences: suspend marketing… | — |  |
| REQ-PTY-120 | J1 | W6 | M | E | record each life event as a LifeEvent record (type,… | REQ-PLT-002 |  |
| REQ-PTY-123 | J1 | W6 | M | B | merge a source account into a target account in… | REQ-POL-002, REQ-BIL-001 |  |
| REQ-PTY-124 | J1 | W6 | M | B | create a policy move request from an account ("Move… | REQ-POL-001 |  |
| REQ-PTY-125 | J1 | W6 | S | B | launch POL's rewrite-to-new-account job from the account ("Rewrite policies… | REQ-POL-001 |  |
| REQ-PTY-126 | J1 | W6 | S | B | split a household or spin off a member: create… | — |  |
| REQ-PTY-127 | J1 | W6 | M | E | show a merge, move or split preview before submission… | REQ-BIL-001, REQ-CLM-009 |  |
| REQ-PTY-129 | J1 | W6 | S | B | refuse account merge, policy move and split across legal… | REQ-POL-002 |  |
| REQ-PTY-130 | J1 | W6 | S | B | require maker-checker approval for account merges, moves and splits… | REQ-PLT-004 |  |
| REQ-PTY-131 | J1 | W2 | M | B | check for duplicate parties and accounts in real time… | — |  |
| REQ-PTY-132 | J1 | W2 | S | B | build candidate sets with blocking keys: normalised identifier values;… | — |  |
| REQ-PTY-133 | J1 | W2 | M | B | score candidates with a deterministic, configurable weighted model (attribute… | REQ-MKT-001 |  |
| REQ-PTY-134 | J1 | W2 | S | B | never merge automatically a party that holds a role… | — |  |
| REQ-PTY-135 | J1 | W2 | M | B | run batch duplicate detection nightly on parties created or… | REQ-WRK-003 |  |
| REQ-PTY-136 | J1 | W2 | M | B | merge parties with survivorship rules per attribute group: identifiers… | — |  |
| REQ-PTY-137 | J1 | W2 | M | B | execute a merge atomically: write a MergeRecord (survivor, merged… | REQ-CLM-001, REQ-POL-002 |  |
| REQ-PTY-138 | J1 | W2 | M | B | unmerge within a window (configurable; default 90 days) by… | REQ-PLT-004 |  |
| REQ-PTY-139 | J1 | W2 | M | B | guarantee merge and unmerge invariants: no reference to a… | — |  |
| REQ-PTY-140 | J1 | W2 | S | B | remember "not a duplicate" decisions per party pair with… | — |  |
| REQ-PTY-141 | J1 | W2 | S | B | hold a data-quality rule catalogue (completeness, validity, consistency, uniqueness,… | REQ-DAT-007 |  |
| REQ-PTY-144 | J1 | W2 | S | B | require maker-checker for merges and unmerges of parties holding… | REQ-PLT-004 |  |
| REQ-PTY-145 | J1 | W2 | M | B | not merge intermediary parties through the customer duplicate process;… | — |  |
| REQ-PTY-146 | J1 | W2 | M | B | hold communication preferences per party and per purpose (policy… | — |  |
| REQ-PTY-147 | J1 | W2 | M | B | allow delivery medium "durable medium other than paper" for… | — |  |
| REQ-PTY-148 | J1 | W2 | M | B | allow delivery medium "website" only with the customer's recorded… | REQ-DOC-005 |  |
| REQ-PTY-149 | J1 | W2 | S | B | record a customer's request for paper copies at any… | — |  |
| REQ-PTY-150 | J1 | W2 | M | B | record marketing consent per channel and purpose (insurer marketing,… | REQ-DOC-004 |  |
| REQ-PTY-151 | J1 | W2 | M | B | maintain a processing-purpose register as configuration (purpose code, description… | REQ-CMP-006 |  |
| REQ-PTY-152 | J1 | W2 | S | B | apply a consent withdrawal with effect from the moment… | REQ-PLT-005 |  |
| REQ-PTY-154 | J1 | W2 | M | B | record trusted third-party contacts per party: the contact (an… | REQ-DOC-005 |  |
| REQ-PTY-155 | J1 | W2 | M | B | record a vulnerable-customer indicator with category (for example bereavement,… | REQ-PLT-001 |  |
| REQ-PTY-156 | J1 | W2 | M | B | apply handling rules from the vulnerable-customer indicator through configuration… | REQ-BIL-006 |  |
| REQ-PTY-158 | J1 | W2 | S | B | show and edit consents and preferences in one consent… | REQ-CHN-004 |  |
| REQ-PTY-159 | J1 | W2 | S | B | record language and script preference for documents (Greek, English;… | REQ-DOC-001 |  |
| REQ-PTY-160 | J1 | W2 | S | B | export a consent and preference evidence pack per party… | REQ-CMP-004 |  |
| REQ-PTY-161 | J1 | W6 | M | B | DSAR export operation returning all PTY-held personal data of… | REQ-CMP-005 |  |
| REQ-PTY-162 | J1 | W6 | S | B | exclude from DSAR exports screening-case details whose disclosure is… | REQ-CMP-005 |  |
| REQ-PTY-163 | J1 | W6 | S | B | execute rectification requests through the normal edit flows with… | REQ-CMP-005 |  |
| REQ-PTY-164 | J1 | W6 | M | B | evaluate erasure requests against retention obligations: erase or anonymise… | REQ-PLT-011 |  |
| REQ-PTY-165 | J1 | W6 | S | B | enforce Restricted status: the party is excluded from search… | REQ-PLT-001 |  |
| REQ-PTY-166 | J1 | W6 | S | B | record objection to processing for marketing and profiling as… | — |  |
| REQ-PTY-167 | J1 | W6 | M | B | assign a retention class from the PLT retention catalogue… | REQ-PLT-011 |  |
| REQ-PTY-168 | J1 | W6 | S | B | publish a personal-data classification catalogue for every PTY attribute… | REQ-DAT-001 |  |
| REQ-PTY-169 | J1 | W2 | M | B | run a sanctions screening engine inside PTY that ingests… | REQ-MKT-002 |  |
| REQ-PTY-170 | J1 | W2 | M | B | resolve which lists apply per legal entity and jurisdiction… | REQ-MKT-001 |  |
| REQ-PTY-171 | J1 | W2 | M | B | ingest list updates at least daily and within 2… | REQ-PLT-013 |  |
| REQ-PTY-172 | J1 | W2 | M | B | screen parties at onboarding (party creation with a customer,… | — |  |
| REQ-PTY-173 | J1 | W2 | M | B | match on every name form (native, generated Latin, "as… | — |  |
| REQ-PTY-174 | J1 | W2 | M | B | classify screening results with configurable thresholds per list and… | REQ-MKT-001 |  |
| REQ-PTY-175 | J1 | W2 | M | B | set the payment-block flag for every party with a… | REQ-CLM-004, REQ-BIL-009 |  |
| REQ-PTY-176 | J1 | W2 | M | B | manage screening cases through the states of section 7.3… | REQ-WRK-001, REQ-WRK-009 |  |
| REQ-PTY-177 | J1 | W2 | S | B | require a false-positive decision to record reason code, free-text… | REQ-PLT-004 |  |
| REQ-PTY-178 | J1 | W2 | M | B | keep an approved false-positive as a suppression rule (party… | — |  |
| REQ-PTY-179 | J1 | W2 | M | B | handle a TrueMatch decision by keeping the payment block,… | REQ-WRK-001 |  |
| REQ-PTY-181 | J1 | W2 | S | B | keep, per screening, the input snapshot hash, list versions,… | REQ-CMP-006 |  |
| REQ-PTY-182 | J1 | W2 | M | B | apply a degraded mode when the active list version… | REQ-PLT-013 |  |
| REQ-PTY-183 | J1 | W2 | S | B | never reveal screening status, list names or case reasons… | REQ-CHN-005 |  |
| REQ-PTY-184 | J1 | W2 | S | B | show the party's screening status (Clear with date, PotentialHit,… | REQ-UW-001 |  |
| REQ-PTY-185 | J1 | W2 | S | B | bulk screening operation for portfolios and migration batches with… | — |  |
| REQ-PTY-187 | J1 | W2 | M | B | model an intermediary as a party (person or organisation)… | REQ-MKT-002 |  |
| REQ-PTY-188 | J1 | W2 | M | B | record register data per intermediary: register (Greece pack: special… | — |  |
| REQ-PTY-189 | J1 | W2 | M | B | check register status through the IntermediaryRegister SPI at onboarding,… | — |  |
| REQ-PTY-190 | J1 | W2 | M | B | track intermediary authorisation status (Active, Suspended, Deleted) and expiry… | REQ-POL-003 |  |
| REQ-PTY-191 | J1 | W2 | M | B | record CPD (continuing professional development) per natural person subject… | REQ-MKT-001 |  |
| REQ-PTY-192 | J1 | W2 | M | B | compute CPD compliance status (Compliant, AtRisk, NonCompliant) daily, warn… | REQ-WRK-007 |  |
| REQ-PTY-194 | J1 | W2 | S | B | link each agency user acting in distribution to a… | — |  |
| REQ-PTY-195 | J1 | W2 | M | B | record conflict-of-interest data per intermediary: holdings above 10 %… | REQ-DOC-007 |  |
| REQ-PTY-196 | J1 | W2 | M | B | supply, through REQ-PTY-008, the intermediary information that must be… | REQ-DOC-007 |  |
| REQ-PTY-197 | J1 | W2 | M | B | supply, for applications and policies, the name, tax identifier… | REQ-DOC-001 |  |
| REQ-PTY-198 | J1 | W2 | S | B | manage the intermediary lifecycle Onboarding → Active → Suspended… | — |  |
| REQ-PTY-199 | J1 | W2 | S | B | publish IntermediaryLicenceChanged on every change of register status, authorisation… | REQ-PLT-005 |  |
| REQ-PTY-202 | J1 | W2 | S | B | on termination of an intermediary or producer code, require | — |  |
| REQ-PTY-203 | J1 | W2 | S | B | hold the distribution hierarchy legal entity → intermediary firm… | — |  |
| REQ-PTY-204 | J1 | W2 | S | B | issue producer codes for producers and branches through the… | REQ-PLT-014 |  |
| REQ-PTY-205 | J1 | W2 | M | B | hold authorities per producer code: may collect premium, may… | REQ-PLT-004 |  |
| REQ-PTY-206 | J1 | W2 | M | B | hold appointments per intermediary: products and lines, territories, channels,… | — |  |
| REQ-PTY-207 | J1 | W2 | M | B | search intermediaries and producer codes by organisation name, organisation… | — |  |
| REQ-PTY-208 | J1 | W2 | M | B | implement pty.ProducerCode.validate as a pure read at a date… | REQ-POL-003 |  |
| REQ-PTY-209 | J1 | W2 | S | B | hold branch codes for intermediary branches and bank branches… | — |  |
| REQ-PTY-211 | J1 | W2 | S | B | model coordinator relationships (agents attached to a coordinator of… | — |  |
| REQ-PTY-212 | J1 | W2 | S | B | expose producer attributes (type, tier, channel, branch, appointments) to… | REQ-PFC-011 |  |
| REQ-PTY-215 | J1 | W6 | S | B | create the producer-of-record record for a policy term from… | REQ-POL-001 |  |
| REQ-PTY-216 | J1 | W6 | S | B | validate the producer code proposed for a submission or… | REQ-POL-001 |  |
| REQ-PTY-217 | J1 | W6 | M | B | change the producer of record mid-term with a reason… | REQ-BIL-008 |  |
| REQ-PTY-218 | J1 | W6 | S | B | not create a policy transaction for a producer-of-record change;… | REQ-POL-002 |  |
| REQ-PTY-219 | J1 | W6 | S | B | require for a broker-initiated change (broker of record) the… | REQ-WRK-005 |  |
| REQ-PTY-220 | J1 | W6 | M | B | execute bulk book transfers from one or more source… | REQ-PLT-004, REQ-PLT-007 |  |
| REQ-PTY-221 | J1 | W6 | S | B | support in-agency producer code changes (a producer moves to… | — |  |
| REQ-PTY-222 | J1 | W6 | S | B | apply "at renewal" transfers by setting the producer on… | REQ-POL-009 |  |
| REQ-PTY-224 | J1 | W6 | S | B | return producer-of-record history for a term or policy with… | — |  |
| REQ-PTY-225 | J1 | W6 | S | B | refuse a producer-of-record change to a code that is… | — |  |
| REQ-PTY-227 | J1 | W6 | S | B | hold commission agreements (agreement number, parties, intermediary or intermediary… | — |  |
| REQ-PTY-228 | J1 | W6 | L | B | define rate lines by product (or line of business),… | REQ-CHN-317 |  |
| REQ-PTY-229 | J1 | W6 | S | B | define the commissionable base per agreement version as a… | REQ-PFC-004 |  |
| REQ-PTY-230 | J1 | W6 | S | B | define overrides (an additional rate to a parent node… | REQ-BIL-008 |  |
| REQ-PTY-232 | J1 | W6 | S | B | define chargeback rules for cancellations and return premiums: pro… | REQ-BIL-008 |  |
| REQ-PTY-234 | J1 | W6 | M | B | manage agreement version states Draft → PendingApproval → Approved… | REQ-PLT-004 |  |
| REQ-PTY-235 | J1 | W6 | M | B | prevent backdating a new version before a date for… | REQ-BIL-008 |  |
| REQ-PTY-236 | J1 | W6 | M | B | resolve agreements deterministically: select the agreement assigned to the… | REQ-BIL-008 |  |
| REQ-PTY-240 | J1 | W6 | M | B | hold per agreement the settlement attributes BIL and FIN… | REQ-BIL-010, REQ-FIN-005, REQ-CMP-001 |  |
| REQ-PTY-241 | J1 | W6 | S | B | nature-of-remuneration statement for IDD disclosure from the resolved agreement… | REQ-DOC-007 |  |
| REQ-PTY-242 | J1 | W6 | S | B | assign agreements to intermediaries and producer codes with effective… | — |  |
| REQ-PTY-243 | J1 | W6 | S | B | hold the association between external-realm users (identity owned by… | REQ-PLT-015 |  |
| REQ-PTY-244 | J1 | W6 | S | B | agency roles (agency administrator / office manager, producer, agency… | REQ-PLT-001 |  |
| REQ-PTY-245 | J1 | W6 | S | B | hold producer access grants: for each agency user, the… | REQ-PLT-001 |  |
| REQ-PTY-246 | J1 | W6 | S | B | let an agency administrator create, suspend and reactivate users,… | REQ-PLT-001 |  |
| REQ-PTY-247 | J1 | W6 | S | B | when a producer is removed from an agency, require | — |  |
| REQ-PTY-250 | J1 | W6 | S | B | support bank-staff users with grants by bank branch and… | — |  |
| REQ-PTY-251 | J1 | W6 | S | B | audit every delegated-administration action with actor, agency, target user… | REQ-PLT-002 |  |
| REQ-PTY-252 | J1 | W6 | S | B | push grant and role changes to PLT so that… | REQ-PLT-001 |  |
| REQ-PTY-253 | J1 | W6 | M | B | configure each bancassurance partnership as data: partner bank party,… | — |  |
| REQ-PTY-254 | J1 | W6 | S | B | support partnership termination or switch with a choice for… | — |  |
| REQ-PTY-255 | J1 | W6 | S | B | import and update the bank's branch structure and staff… | REQ-PLT-006 |  |
| REQ-PTY-257 | J1 | W6 | S | B | require bank staff selling insurance to be linked to… | — |  |
| REQ-PTY-259 | J1 | W2 | L | B | party 360 view with details, accounts, policies, open policy… | REQ-POL-002, REQ-CLM-009, REQ-BIL-001, REQ-WRK-004 |  |
| REQ-PTY-260 | J1 | W2 | S | B | account 360 view with details, overview measures, current activities,… | — |  |
| REQ-PTY-261 | J1 | W2 | L | B | offer from account and party 360 the actions New… | REQ-POL-001, REQ-WRK-001, REQ-WRK-004, REQ-DOC-001, REQ-CMP-005 |  |
| REQ-PTY-262 | J1 | W2 | M | B | show policy terms for the account or party from… | REQ-POL-002, REQ-POL-004 |  |
| REQ-PTY-263 | J1 | W2 | S | B | show open policy transactions (jobs) from POL with job… | REQ-POL-001 |  |
| REQ-PTY-264 | J1 | W2 | S | B | show claims of the account or party from CLM's… | REQ-CLM-009 |  |
| REQ-PTY-265 | J1 | W2 | S | B | show a billing summary from BIL as a statement… | REQ-BIL-001 |  |
| REQ-PTY-266 | J1 | W2 | M | B | show current activities and notes for the account from… | REQ-WRK-001, REQ-WRK-004 |  |
| REQ-PTY-268 | J1 | W2 | M | B | navigate from account 360 to Participants (WRK), Policy Transactions,… | REQ-DOC-008, REQ-WRK-010 |  |
| REQ-PTY-269 | J1 | W2 | S | B | run imported parties through duplicate detection against existing parties… | REQ-MIG-001 |  |
| REQ-PTY-270 | J1 | W2 | S | B | import intermediaries, producer codes, hierarchy, appointments, agreements with versions… | REQ-MIG-001 |  |
| REQ-PTY-271 | J1 | W2 | S | B | import consents and preferences with their original source, timestamp… | REQ-MIG-001 |  |
| REQ-PTY-272 | J1 | W2 | S | B | screen every imported party against sanctions lists before its… | — |  |
| REQ-PTY-273 | J1 | W2 | M | B | implement every PTY command with an Idempotency-Key, typed preconditions… | — |  |
| REQ-PTY-274 | J1 | W2 | M | B | hold every PTY code list (role types, relationship types,… | REQ-MKT-001 |  |
| REQ-PTY-275 | J1 | W2 | S | B | label every PTY screen, message and error in Greek… | REQ-MKT-005 |  |
| REQ-PTY-276 | J1 | W2 | S | B | expose PTY operations to CHN's partner API and MCP… | REQ-CHN-003 |  |
| REQ-PTY-278 | J1 | W2 | M | B | not store payee bank account data (IBAN, holder name,… | REQ-BIL-343, REQ-BIL-344, REQ-BIL-345 |  |
| REQ-PTY-279 | J1 | W2 | M | B | enforce AI governance for every AI-PTY feature: call models… | REQ-PLT-010 |  |
| REQ-PTY-280 | J1 | W2 | M | B | write an AiInteractionRecord (feature id, model and prompt versions,… | REQ-PLT-010, REQ-PLT-002, REQ-CMP-007 |  |
| REQ-PTY-281 | J1 | W2 | M | E | register every ICT third-party service PTY depends on (sanctions-list… | REQ-PLT-012, REQ-PLT-013, REQ-MKT-002 |  |

## A.1.4 PFC (200 Musts, 298 points): 200 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-PFC-001 | J2 | W2 | XL | B | pfc.ProductVersion.resolve that, given jurisdiction, legal entity, product code, channel,… | — |  |
| REQ-PFC-002 | J2 | W2 | XL | B | compile every product version into an immutable artefact identified… | — |  |
| REQ-PFC-003 | J2 | W2 | XL | B | catalogue queries over a version's coverages, coverage terms, options,… | REQ-MKT-001 |  |
| REQ-PFC-004 | J2 | W2 | XL | B | charge-type catalogue of a version with, per charge type,… | — |  |
| REQ-PFC-005 | J2 | W2 | XL | B | hold and serve, per coverage of a version, the… | REQ-MKT-007 |  |
| REQ-PFC-006 | J2 | W2 | XL | B | define question sets on product versions and evaluate, for… | — |  |
| REQ-PFC-007 | J2 | W2 | XL | B | pfc.Pog.get returning the POG record of a product version:… | — |  |
| REQ-PFC-008 | J6 | W6 | XL | B | hold renewal conversion rules for every ordered pair of… | REQ-POL-009 |  |
| REQ-PFC-009 | J2 | W2 | XL | B | publish the lifecycle events ProductVersionSubmitted, ProductVersionApproved, ProductVersionScheduled, ProductVersionPublished and… | REQ-PLT-005 |  |
| REQ-PFC-010 | J2 | W2 | XL | B | hold, per product version, references to rating artefacts, UW… | — |  |
| REQ-PFC-011 | J2 | W2 | XL | B | hold offerings (packages) per product version and availability rules… | — |  |
| REQ-PFC-030 | J2 | W2 | S | E | maintain product lines with code, Greek and English name,… | — |  |
| REQ-PFC-031 | J2 | W2 | S | B | maintain products with code, Greek and English name and… | — |  |
| REQ-PFC-032 | J2 | W2 | M | B | maintain product versions identified by product code plus major.minor,… | REQ-MKT-001 |  |
| REQ-PFC-033 | J2 | W2 | S | E | prevent two Locked versions of the same product, jurisdiction,… | — |  |
| REQ-PFC-034 | J2 | W2 | S | B | hold the allowed policy term lengths of a product… | — | see XMR-F-420 (6-month GS-02 vs annual-only MVP) |
| REQ-PFC-035 | J2 | W2 | S | B | hold an "offering required" flag per product version requiring… | — |  |
| REQ-PFC-036 | J2 | W2 | S | B | allow a product version to contain one or more… | — |  |
| REQ-PFC-037 | J2 | W2 | S | B | maintain policy-line definitions with code, Greek and English name… | REQ-MKT-006 |  |
| REQ-PFC-038 | J2 | W2 | S | B | store an explicit display order for product lines, products,… | — |  |
| REQ-PFC-039 | J2 | W2 | L | E | hold the channel set of a product version using… | — |  |
| REQ-PFC-040 | J2 | W2 | S | B | hold one contract currency per product version and restrict… | REQ-MKT-006 |  |
| REQ-PFC-041 | J2 | W2 | S | B | hold the language set of a product version; Greek… | REQ-MKT-005 |  |
| REQ-PFC-042 | J2 | W2 | S | E | allow a product version to extend one abstract base… | — |  |
| REQ-PFC-043 | J2 | W2 | S | E | support country overlays and channel overlays that add, modify… | — |  |
| REQ-PFC-044 | J2 | W2 | S | E | flatten base, country and channel layers into one artefact… | — |  |
| REQ-PFC-046 | J2 | W2 | S | E | allow a channel variant to declare simplified quote rules:… | — |  |
| REQ-PFC-047 | J2 | W2 | M | B | hold external codes per product, coverage and charge type:… | REQ-MIG-002, REQ-CHN-001 |  |
| REQ-PFC-048 | J2 | W2 | M | B | maintain element types (policy line, vehicle, driver, building, contents,… | REQ-POL-010 |  |
| REQ-PFC-050 | J2 | W2 | M | B | maintain field definitions per element type with code, Greek… | — |  |
| REQ-PFC-051 | J2 | W2 | M | B | support field data types text, integer, decimal, money, percentage,… | REQ-PTY-002, REQ-PTY-012 |  |
| REQ-PFC-052 | J2 | W2 | S | E | hold for numeric and money fields a unit from… | REQ-MKT-006 |  |
| REQ-PFC-053 | J2 | W2 | M | B | evaluate field-level validation rules: required, minimum, maximum, length, pattern,… | REQ-PTY-003, REQ-MKT-002 |  |
| REQ-PFC-054 | J2 | W2 | S | B | evaluate element-level validation rules across fields of one element… | — |  |
| REQ-PFC-055 | J2 | W2 | S | B | evaluate policy-level validation rules across elements, coverages and policy… | — |  |
| REQ-PFC-056 | J2 | W2 | S | E | assign each validation rule a severity (error, warning, information)… | REQ-POL-003 |  |
| REQ-PFC-057 | J2 | W2 | S | B | support default values that are static, expression-based, reference-table based… | REQ-PTY-012 |  |
| REQ-PFC-058 | J2 | W2 | M | E | treat the Greek label and help text as the… | — |  |
| REQ-PFC-059 | J2 | W2 | S | E | mark each label, help text and question as customer-visible… | — |  |
| REQ-PFC-060 | J2 | W2 | S | E | support derived fields computed by deterministic expressions over other… | — |  |
| REQ-PFC-061 | J2 | W2 | S | B | support conditional visibility and conditional requiredness of fields, elements,… | — |  |
| REQ-PFC-062 | J2 | W2 | S | E | define, in the Greece pack overlay of the motor… | REQ-POL-003 |  |
| REQ-PFC-063 | J2 | W2 | M | B | maintain product reference tables (for example seismic zone by… | REQ-RAT-001 |  |
| REQ-PFC-065 | J2 | W2 | M | E | express every rule (existence, default, derivation, visibility, validation, availability… | REQ-PLT-007 |  |
| REQ-PFC-066 | J2 | W2 | S | E | hold, per field, editability by transaction type (new business,… | REQ-POL-001 |  |
| REQ-PFC-067 | J2 | W2 | M | B | maintain coverage definitions with code, Greek and English name… | — |  |
| REQ-PFC-068 | J2 | W2 | M | B | maintain categories per policy line with code, Greek and… | — |  |
| REQ-PFC-069 | J2 | W2 | S | E | support a declarative existence rule per coverage ("required when",… | — |  |
| REQ-PFC-070 | J2 | W2 | S | B | support initial term values applied when a coverage is… | — |  |
| REQ-PFC-071 | J2 | W2 | S | B | support a removal rule per coverage that blocks removal… | — |  |
| REQ-PFC-072 | J2 | W2 | S | E | support coverage dependencies: requires, mutually excludes, and implies (auto-attach),… | — |  |
| REQ-PFC-073 | J2 | W2 | S | B | support coverage-term kinds: option list, numeric range with step,… | — |  |
| REQ-PFC-075 | J2 | W2 | S | B | hold a value type per term: money, percentage, count,… | — |  |
| REQ-PFC-076 | J2 | W2 | M | B | hold term semantics: model type (limit, sub-limit, deductible, sum… | REQ-CLM-002 |  |
| REQ-PFC-077 | J2 | W2 | S | B | hold for each term a required flag and default… | — |  |
| REQ-PFC-078 | J2 | W2 | S | B | maintain term options with code, value, currency, Greek and… | — |  |
| REQ-PFC-079 | J2 | W2 | S | B | support numeric-range terms with minimum, maximum, step and default,… | — |  |
| REQ-PFC-080 | J2 | W2 | S | B | reject an option-list term that has no options. | — |  |
| REQ-PFC-082 | J2 | W2 | S | E | support constraints between terms of the same or different… | — |  |
| REQ-PFC-083 | J2 | W2 | S | B | maintain exclusion definitions per policy line with code, Greek… | REQ-DOC-002 |  |
| REQ-PFC-084 | J2 | W2 | S | B | maintain condition definitions per policy line with the same… | — |  |
| REQ-PFC-085 | J2 | W2 | M | B | allow a term's minimum (or an option floor) to… | REQ-MKT-001 |  |
| REQ-PFC-086 | J2 | W2 | S | B | reject any product or channel layer that overrides, hides… | — |  |
| REQ-PFC-087 | J2 | W2 | M | E | on receipt of ConfigurationActivated or PackActivated, re-lint all Draft | REQ-WRK-001, REQ-MKT-001 |  |
| REQ-PFC-088 | J2 | W2 | S | B | the MTPL coverage shall be required and non-removable on… | — | Greece pack |
| REQ-PFC-093 | J2 | W2 | S | B | hold per coverage reinsurance attributes: RI-cedable flag, RI risk… | — |  |
| REQ-PFC-095 | J2 | W2 | S | B | maintain offerings per product version with code, Greek and… | — |  |
| REQ-PFC-096 | J2 | W2 | S | B | hold per offering the enabled or disabled state of… | — |  |
| REQ-PFC-097 | J2 | W2 | S | E | hold per offering default term values and restricted option… | — |  |
| REQ-PFC-099 | J2 | W2 | S | B | maintain, per coverage, the set of offerings that include… | — |  |
| REQ-PFC-100 | J2 | W2 | S | E | allow concurrent edits to offerings and selections in different… | — |  |
| REQ-PFC-101 | J2 | W2 | M | B | define availability rules attachable to products, offerings, coverages, terms,… | — |  |
| REQ-PFC-102 | J2 | W2 | S | E | resolve multiple matching availability rules by specificity (more scope… | — |  |
| REQ-PFC-103 | J2 | W2 | S | E | make child items inherit their parent's availability unless they… | — |  |
| REQ-PFC-104 | J2 | W2 | S | B | support grandfather rules per item with jurisdiction, legal entity… | — |  |
| REQ-PFC-105 | J2 | W2 | S | B | apply availability by transaction type (new business, renewal, policy… | — |  |
| REQ-PFC-108 | J2 | W2 | S | B | jurisdiction and region picker drawing codes from MKT (country,… | REQ-MKT-005 |  |
| REQ-PFC-109 | J2 | W2 | M | B | maintain question sets with code, type (pre-qualification, underwriting, demands… | REQ-UW-001 |  |
| REQ-PFC-110 | J2 | W2 | S | B | assign question sets to product versions and offerings with… | — |  |
| REQ-PFC-113 | J2 | W2 | M | B | maintain charge types per product version with code, Greek… | REQ-RAT-001 |  |
| REQ-PFC-114 | J2 | W2 | S | B | hold the handling of each charge type: pro rata… | REQ-RAT-004 |  |
| REQ-PFC-115 | J2 | W2 | S | B | hold an earning pattern per charge type: daily pro… | REQ-FIN-003 |  |
| REQ-PFC-116 | J2 | W2 | S | E | hold cancellation treatment per charge type: follow product refund… | REQ-POL-001 |  |
| REQ-PFC-117 | J2 | W2 | S | B | hold, per charge type, a tax class used by… | REQ-RAT-009 |  |
| REQ-PFC-118 | J2 | W2 | S | E | hold, per charge type, whether it is re-emitted when… | REQ-POL-006 |  |
| REQ-PFC-119 | J2 | W2 | M | E | hold the billing treatment per charge type: billed to… | REQ-BIL-002, REQ-FIN-005 |  |
| REQ-PFC-120 | J2 | W2 | S | B | hold, per charge type, a GL key consumed by… | REQ-FIN-004 |  |
| REQ-PFC-121 | J2 | W2 | S | E | hold, per charge type, a fiscal-category key resolved by… | REQ-CMP-001 |  |
| REQ-PFC-122 | J2 | W2 | M | B | hold RI-cedable and commissionable flags per charge type. | REQ-BIL-008 |  |
| REQ-PFC-123 | J2 | W2 | S | B | define tax and levy charge types as computed by… | REQ-RAT-009 |  |
| REQ-PFC-124 | J2 | W2 | M | B | the motor product shall carry charge type GR-AUXF-PH (Auxiliary… | REQ-RAT-009 | Greece pack |
| REQ-PFC-125 | J2 | W2 | M | B | the motor product shall carry charge type GR-AUXF-INS (Auxiliary… | REQ-MKT-001, REQ-FIN-005 | Greece pack |
| REQ-PFC-127 | J2 | W2 | S | E | support discount, surcharge and credit charge types linked to… | REQ-RAT-008 |  |
| REQ-PFC-128 | J2 | W2 | M | E | support the stub's synthetic flat per-policy | REQ-MKT-008, REQ-MKT-265 | Cyprus stub pack; synthetic/not law |
| REQ-PFC-129 | J2 | W2 | S | E | support two binding modes for each reference: pinned (exact… | — |  |
| REQ-PFC-130 | J2 | W2 | M | B | reference the rating artefact through a rating-slot declaration (rating… | REQ-RAT-001, REQ-RAT-061, REQ-RAT-067 |  |
| REQ-PFC-131 | J2 | W2 | S | B | reference UW rule sets per checkpoint (pre-quote, pre-bind, pre-issue,… | REQ-UW-001 |  |
| REQ-PFC-132 | J2 | W2 | M | B | reference form patterns for coverages, exclusions, conditions, offerings and… | REQ-DOC-003 |  |
| REQ-PFC-133 | J2 | W2 | S | B | list the payment plans offered per product version and… | REQ-BIL-003 |  |
| REQ-PFC-134 | J2 | W2 | M | B | hold the refund method per cancellation source, keyed by… | REQ-POL-001 |  |
| REQ-PFC-135 | J2 | W2 | S | B | hold the day-count convention of a product version (for… | REQ-RAT-004 |  |
| REQ-PFC-136 | J2 | W2 | S | B | hold the out-of-sequence conflict rule of a product version:… | REQ-POL-006 |  |
| REQ-PFC-137 | J2 | W2 | S | B | verify that every pinned reference exists, is approved and… | — |  |
| REQ-PFC-138 | J2 | W2 | S | E | maintain read models of referenced definitions (RatingArtifactView, UwRuleSetView, FormPatternView,… | REQ-PLT-005 |  |
| REQ-PFC-140 | J2 | W2 | S | B | assign each coverage a Solvency II line of business… | REQ-MKT-007 |  |
| REQ-PFC-141 | J2 | W2 | S | B | validate every code in a mapping against the regime… | REQ-MKT-007 |  |
| REQ-PFC-142 | J2 | W2 | S | B | assign each coverage an IPT class from the country… | REQ-RAT-009 |  |
| REQ-PFC-143 | J2 | W2 | S | B | assign each coverage an IFRS 17 portfolio code from… | REQ-FIN-003 |  |
| REQ-PFC-144 | J2 | W2 | S | B | assign each coverage a national statistical class (Bank of… | REQ-DAT-003 |  |
| REQ-PFC-146 | J2 | W2 | S | E | block compilation when a coverage maps to an authorised… | REQ-MKT-001 |  |
| REQ-PFC-147 | J2 | W2 | S | B | regulatory mapping table view and export (CSV and JSON)… | — |  |
| REQ-PFC-148 | J2 | W2 | M | B | maintain a POG record per product (applying to all… | — |  |
| REQ-PFC-149 | J2 | W2 | M | B | capture the target market as structured criteria (customer type,… | — |  |
| REQ-PFC-150 | J2 | W2 | S | B | capture the negative target market (groups for whom the… | REQ-CHN-002 |  |
| REQ-PFC-151 | J2 | W2 | S | B | capture the distribution strategy per channel (permitted channels, advice… | — |  |
| REQ-PFC-152 | J2 | W2 | S | B | record product testing evidence: scenario definitions, method (qualitative, quantitative),… | — |  |
| REQ-PFC-153 | J2 | W2 | M | E | classify each version change as significant adaptation or not,… | — |  |
| REQ-PFC-154 | J2 | W2 | M | B | schedule periodic POG reviews per product with a configurable… | REQ-WRK-001, REQ-WRK-007 |  |
| REQ-PFC-155 | J2 | W2 | S | B | record review outcomes: no action, remediation change set opened,… | — |  |
| REQ-PFC-158 | J2 | W2 | S | B | retain POG records, testing evidence and review outcomes immutably… | REQ-PLT-011 |  |
| REQ-PFC-159 | J2 | W2 | M | E | hold structured IPID data per product version and offering:… | REQ-DOC-007 |  |
| REQ-PFC-160 | J2 | W2 | S | E | Lint shall fail submission when any IPID section is… | — |  |
| REQ-PFC-161 | J2 | W2 | S | E | flag DOC to regenerate IPIDs when a Locked version… | REQ-DOC-007 |  |
| REQ-PFC-162 | J2 | W2 | S | B | manage product versions through the states Draft, Submitted, Approved,… | — |  |
| REQ-PFC-163 | J2 | W2 | M | B | restrict each transition to permitted roles: submit (author roles),… | REQ-PLT-001 |  |
| REQ-PFC-164 | J2 | W2 | S | B | make a version and its artefact immutable once Locked;… | — |  |
| REQ-PFC-165 | J2 | W2 | M | E | number versions major.minor, proposing a major increment when the… | — |  |
| REQ-PFC-166 | J2 | W2 | S | E | hold for each Locked version a new-business window and… | — |  |
| REQ-PFC-167 | J2 | W2 | S | B | select, for new business and renewal, the Locked version… | — |  |
| REQ-PFC-168 | J2 | W2 | S | B | not resolve a version for policy changes, cancellations and… | REQ-POL-001 |  |
| REQ-PFC-169 | J2 | W2 | S | B | guarantee that publishing a later version never changes the… | — |  |
| REQ-PFC-171 | J2 | W2 | M | B | require a renewal conversion rule set for every Locked… | — |  |
| REQ-PFC-172 | J2 | W2 | S | B | keep grandfathered options and coverages on renewed terms until… | — |  |
| REQ-PFC-173 | J2 | W2 | S | E | raise a referral, not a silent change, whenever no… | REQ-UW-004 |  |
| REQ-PFC-174 | J2 | W2 | S | E | run a dry-run conversion over the in-force and pending-renewal… | REQ-DAT-002 |  |
| REQ-PFC-175 | J2 | W2 | S | E | Lint shall fail when a conversion rule targets an… | — |  |
| REQ-PFC-178 | J2 | W2 | S | B | close a version to new business at a set… | — |  |
| REQ-PFC-179 | J2 | W2 | M | E | require, when a product is withdrawn, a run-off decision:… | REQ-POL-009, REQ-CMP-003 |  |
| REQ-PFC-180 | J2 | W2 | S | B | move a version to Retired automatically when both windows… | REQ-POL-014 |  |
| REQ-PFC-181 | J2 | W2 | S | B | keep artefacts of Retired versions retrievable by hash for… | — |  |
| REQ-PFC-182 | J2 | W2 | S | E | create change sets as branches in the product Git… | REQ-PLT-006 |  |
| REQ-PFC-183 | J2 | W2 | S | E | record every authoring edit as a commit attributed to… | REQ-PLT-002 |  |
| REQ-PFC-184 | J2 | W2 | S | B | show a changes panel listing pending edits in the… | — |  |
| REQ-PFC-185 | J2 | W2 | S | B | change review page listing location, context, change, when and… | — |  |
| REQ-PFC-186 | J2 | W2 | S | E | replace "commit" with "submit for approval", which validates, lints,… | — |  |
| REQ-PFC-187 | J2 | W2 | S | B | undo for the last edits in a session and… | — |  |
| REQ-PFC-188 | J2 | W2 | S | E | merge concurrent change sets on the same version with… | — |  |
| REQ-PFC-190 | J2 | W2 | S | B | validate product source against a versioned JSON Schema (draft… | — |  |
| REQ-PFC-191 | J2 | W2 | S | B | run the lint catalogue of section 14.x on every… | — |  |
| REQ-PFC-192 | J2 | W2 | S | B | show validation results live in authoring screens next to… | — |  |
| REQ-PFC-193 | J2 | W2 | S | B | be deterministic: the same source commit, base artefacts, pinned… | — |  |
| REQ-PFC-194 | J2 | W2 | S | E | serialise artefacts as canonical JSON (RFC 8785 JSON Canonicalization… | — |  |
| REQ-PFC-195 | J2 | W2 | M | E | record schema version, compiler version, source commit, base artefact… | REQ-MKT-001 |  |
| REQ-PFC-196 | J2 | W2 | S | E | rebuild every Locked artefact nightly from its manifest and… | REQ-PLT-012 |  |
| REQ-PFC-197 | J2 | W2 | S | B | store artefacts in a content-addressed, write-once runtime store replicated… | — |  |
| REQ-PFC-198 | J2 | W2 | S | E | protect the main branch: changes only through approved pull… | REQ-PLT-006 |  |
| REQ-PFC-200 | J2 | W2 | M | E | produce a business-language diff between any two versions or… | — |  |
| REQ-PFC-201 | J2 | W2 | S | E | classify each diff entry by change class (structural, cover-reducing,… | — |  |
| REQ-PFC-202 | J2 | W2 | S | B | show the diff of a submitted version against the… | — |  |
| REQ-PFC-203 | J2 | W2 | M | E | derive the required sign-off roles from the change classes… | REQ-PLT-004 |  |
| REQ-PFC-204 | J2 | W2 | M | B | Each sign-off shall pass authority.check for authority type PFC.ProductApproval… | REQ-PLT-003, REQ-PLT-004 |  |
| REQ-PFC-205 | J2 | W2 | M | E | freeze an evidence pack at approval containing the diff,… | REQ-DOC-004 |  |
| REQ-PFC-206 | J2 | W2 | S | B | Any required approver shall be able to return a… | — |  |
| REQ-PFC-207 | J2 | W2 | M | B | produce an impact report before approval listing counts of… | REQ-DAT-002, REQ-POL-014 |  |
| REQ-PFC-208 | J2 | W2 | S | B | request premium-impact analysis from RAT per REQ-RAT-007 (compare versions… | REQ-RAT-007 |  |
| REQ-PFC-209 | J2 | W2 | S | E | list downstream effects: forms whose bindings change, UW rule-set… | REQ-DOC-003 |  |
| REQ-PFC-210 | J2 | W2 | S | E | promote the same artefact hash through the environments development,… | REQ-PLT-008 |  |
| REQ-PFC-211 | J2 | W2 | M | B | schedule activation of an Approved version for a date… | REQ-PLT-007 |  |
| REQ-PFC-212 | J2 | W2 | S | E | be able to cancel or reschedule a scheduled activation… | — |  |
| REQ-PFC-213 | J2 | W2 | S | E | fall-back for a defective Locked version by closing it… | — |  |
| REQ-PFC-214 | J2 | W2 | M | E | emergency change path with a reduced but still segregated… | REQ-PLT-003 |  |
| REQ-PFC-215 | J2 | W2 | S | E | Every emergency change shall have a retrospective review by… | REQ-WRK-001 |  |
| REQ-PFC-216 | J2 | W2 | S | B | enforce segregation of duties: the deployer (release manager scheduling… | REQ-PLT-001 |  |
| REQ-PFC-218 | J2 | W2 | S | E | notify required approvers when a version is submitted, and… | REQ-WRK-007 |  |
| REQ-PFC-219 | J2 | W2 | S | B | promote product reference tables through environments with the same… | — |  |
| REQ-PFC-220 | J2 | W2 | S | B | serve resolution from an in-memory index of Locked versions… | — |  |
| REQ-PFC-221 | J2 | W2 | M | E | contain a resolution manifest listing the artefact hash, floating… | REQ-POL-001 |  |
| REQ-PFC-222 | J2 | W2 | M | B | pfc.Product.describe returning, for a product, channel, transaction type, language… | REQ-CHN-001 |  |
| REQ-PFC-223 | J2 | W2 | S | E | Describe responses shall include machine-evaluable rule expressions or rule… | — |  |
| REQ-PFC-224 | J2 | W2 | M | B | pfc.PolicyDraft.validate that validates a draft risk tree and coverage… | REQ-POL-001 |  |
| REQ-PFC-225 | J2 | W2 | S | E | Draft validation shall accept partial drafts and validate only… | — | mentions 'partial' (behavioural, not scope) |
| REQ-PFC-226 | J2 | W2 | S | B | catalogue query operations for coverages, terms, options, exclusions, conditions,… | — |  |
| REQ-PFC-227 | J2 | W2 | S | B | pfc.Artifact.get shall return any artefact by hash regardless of… | — |  |
| REQ-PFC-228 | J2 | W2 | S | E | load all Locked artefacts of its deployment stamp at… | REQ-PLT-013 |  |
| REQ-PFC-229 | J2 | W2 | S | B | refresh its index within 5 seconds of ProductVersionPublished, ProductVersionRetired… | REQ-PLT-005 |  |
| REQ-PFC-232 | J2 | W2 | S | B | Runtime errors shall follow RFC 9457 with codes PFC-ERR-*… | — |  |
| REQ-PFC-234 | J2 | W2 | S | B | maintain per product a golden set of example policies… | REQ-RAT-001 |  |
| REQ-PFC-235 | J2 | W2 | S | B | CI shall fail a pull request when any golden… | — |  |
| REQ-PFC-236 | J2 | W2 | S | B | Golden policies and scenario tests shall contain only synthetic… | REQ-PLT-011 |  |
| REQ-PFC-238 | J2 | W2 | M | B | implement the lint catalogue of section 14.x, including at… | — |  |
| REQ-PFC-239 | J2 | W2 | S | B | CI shall compile, validate and golden-test the Cyprus stub… | REQ-MKT-008 | Cyprus stub pack |
| REQ-PFC-240 | J2 | W2 | S | B | pfc.ProductImport.import for migration of legacy product definitions into Draft… | REQ-MIG-001 |  |
| REQ-PFC-241 | J2 | W2 | S | B | maintain legacy-to-core cross-references for product, coverage, option and charge… | REQ-MIG-002 |  |
| REQ-PFC-242 | J2 | W2 | S | E | support conversion rules whose source is a legacy product… | REQ-MIG-003 |  |
| REQ-PFC-244 | J2 | W2 | M | E | the motor product shall carry a tax charge type… | REQ-RAT-009, REQ-FIN-005 | Greece pack |
| REQ-PFC-245 | J2 | W2 | M | E | Every ProductVersion* event shall carry the version's rating-slot declaration… | REQ-RAT-061, REQ-RAT-066 |  |
| REQ-PFC-246 | J2 | W2 | M | E | lint, at compile time, every field and question that… | REQ-MKT-110 |  |
| REQ-PFC-247 | J2 | W2 | M | E | lint, at compile time, that every clause returned by… | REQ-MKT-115, REQ-DOC-003 |  |
| REQ-PFC-248 | J2 | W2 | M | E | disable every AI-PFC feature within the contract kill-switch target… | REQ-PLT-010 |  |
| REQ-PFC-249 | J2 | W2 | M | E | record an AiInteractionRecord through the PLT AI control plane… | REQ-PLT-010, REQ-CMP-007 |  |

## A.1.5 RAT (214 Musts, 295 points): 214 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-RAT-001 | J2 | W3 | XL | B | rat.Rate.rate, a synchronous, side-effect-free rating operation that, given a… | REQ-PFC-002, REQ-POL-001 |  |
| REQ-RAT-002 | J2 | W3 | XL | B | rat.Rate.rateBatch, an asynchronous operation that rates up to 10,000… | REQ-POL-009 |  |
| REQ-RAT-003 | J2 | W3 | XL | B | store every worksheet produced for a quote or transaction,… | — |  |
| REQ-RAT-004 | J2 | W3 | XL | B | deterministic proration and day-count service, rat.Proration.prorate, shared with POL,… | REQ-PFC-135 |  |
| REQ-RAT-005 | J2 | W3 | XL | B | let authorised users apply pricing modifications (deviations) to a… | REQ-PLT-003, REQ-PLT-004 |  |
| REQ-RAT-006 | J2 | W3 | XL | B | emit referral signals in every rating response — coded,… | REQ-UW-001 |  |
| REQ-RAT-007 | J2 | W8 | XL | B | CompareVersions, ShadowRun and impact analysis: rating the same inputs… | REQ-PFC-208, REQ-DAT-002 | W8 |
| REQ-RAT-008 | J2 | W3 | XL | B | rat.Breakdown.get returning customer premium breakdown data for a worksheet:… | REQ-CHN-004 |  |
| REQ-RAT-009 | J2 | W3 | XL | B | compute taxes and levies after the final premium, through… | REQ-MKT-087, REQ-PFC-117 |  |
| REQ-RAT-030 | J2 | W3 | M | B | carry an envelope with legal entity, jurisdiction, product code,… | — |  |
| REQ-RAT-031 | J2 | W3 | S | B | Each rating artefact shall define a versioned input schema… | REQ-PFC-130 |  |
| REQ-RAT-032 | J2 | W3 | S | E | normalise every input to a canonical form (RFC 8785… | — |  |
| REQ-RAT-033 | J2 | W3 | S | E | reject any input attribute not declared in the rating… | — |  |
| REQ-RAT-034 | J2 | W3 | S | E | allow per-attribute provenance (user-entered, MotorDataProvider, Geocoder, artefact default, prior… | — |  |
| REQ-RAT-035 | J2 | W3 | S | B | accept up to 400 segments per request (configurable), rate… | REQ-POL-006 |  |
| REQ-RAT-036 | J2 | W3 | M | B | contain, per segment, per element and per charge type:… | REQ-PFC-114 |  |
| REQ-RAT-037 | J2 | W3 | S | B | distinguish errors (no result returned, RFC 9457) from warnings… | — |  |
| REQ-RAT-038 | J2 | W3 | S | B | support mode FULL for new-business and rewrite quotes, requiring… | REQ-POL-001 |  |
| REQ-RAT-039 | J2 | W3 | S | B | support mode ENDORSEMENT for policy changes, cancellations, reinstatements and… | REQ-POL-006 |  |
| REQ-RAT-040 | J2 | W3 | S | E | support mode QUICK, filling missing optional and required-for-binding inputs… | REQ-POL-003 |  |
| REQ-RAT-041 | J2 | W3 | S | B | support mode RENEWAL, requiring the prior term's annual rates… | REQ-POL-009 |  |
| REQ-RAT-042 | J2 | W3 | M | B | support mode DRY_RUN for partners and AI agents: identical… | REQ-CHN-001, REQ-CHN-003 |  |
| REQ-RAT-043 | J2 | W3 | S | E | support an internal mode CANDIDATE used only by ShadowRun… | — |  |
| REQ-RAT-044 | J2 | W3 | S | B | accept an origin = MIGRATION flag on rating and… | REQ-MIG-001 |  |
| REQ-RAT-045 | J2 | W3 | S | B | hold no mutable state between requests other than immutable… | — |  |
| REQ-RAT-046 | J2 | W3 | S | B | guarantee that the same rating key and normalised input… | — |  |
| REQ-RAT-047 | J2 | W3 | M | E | Algorithms shall have no access to the system clock,… | REQ-PLT-265 |  |
| REQ-RAT-048 | J2 | W3 | S | B | make rat.Rate.rate safe to replay: an optional idempotency key… | — |  |
| REQ-RAT-049 | J2 | W3 | S | B | never substitute a rating artefact other than the one… | — |  |
| REQ-RAT-050 | J2 | W3 | S | B | Rating errors shall follow RFC 9457 with codes RAT-ERR-*… | — |  |
| REQ-RAT-051 | J2 | W3 | S | B | A rating call shall either complete fully or fail;… | — |  |
| REQ-RAT-052 | J2 | W3 | S | B | echo POL's stable element ids and coverage codes in… | REQ-POL-010 |  |
| REQ-RAT-053 | J2 | W3 | S | B | rate in the currency of the product version and… | REQ-MKT-006 |  |
| REQ-RAT-054 | J2 | W3 | S | B | Batch rating shall process items in parallel across nodes,… | — |  |
| REQ-RAT-055 | J2 | W3 | S | B | Batch items shall carry an item key; resubmitting a… | REQ-PLT-007 |  |
| REQ-RAT-056 | J2 | W3 | S | E | Batch, impact and ShadowRun work shall run on worker… | — |  |
| REQ-RAT-058 | J2 | W3 | S | B | rate with any rating artefact in status Approved, Scheduled,… | REQ-POL-011 |  |
| REQ-RAT-059 | J2 | W3 | S | B | Each rating call shall emit OpenTelemetry spans and metrics… | REQ-PLT-013 |  |
| REQ-RAT-060 | J2 | W3 | M | B | compile each rating artefact from an algorithm version, the… | — |  |
| REQ-RAT-061 | J2 | W3 | M | B | Each rating artefact shall declare the rating slots it… | REQ-PFC-002, REQ-PFC-003, REQ-PFC-004 |  |
| REQ-RAT-062 | J2 | W3 | S | B | refuse to schedule a rating artefact for a slot… | REQ-PFC-001 |  |
| REQ-RAT-063 | J2 | W3 | M | B | rate every in-term transaction (policy change, cancellation, reinstatement, out-of-sequence… | REQ-POL-001, REQ-POL-006 |  |
| REQ-RAT-064 | J2 | W3 | S | B | rat.RatingArtifact.resolve(slot, transactionType, basisDate, knownAt) returning the rating artefact active… | — |  |
| REQ-RAT-066 | J2 | W3 | S | B | publish RatingArtifactPublished, RateVersionScheduled and RateVersionActivated with slot, artefact hash,… | REQ-PLT-005 |  |
| REQ-RAT-067 | J2 | W3 | S | E | On ProductVersionApproved or ProductVersionPublished, the system shall compute compatibility… | REQ-WRK-007 |  |
| REQ-RAT-068 | J2 | W3 | S | B | hold each loaded rating artefact as immutable in-memory structures… | — |  |
| REQ-RAT-069 | J2 | W3 | S | B | On start-up each node shall load and verify the… | REQ-PLT-013 |  |
| REQ-RAT-070 | J2 | W3 | S | E | load non-active retained artefacts on demand within 2 seconds… | — |  |
| REQ-RAT-071 | J2 | W3 | S | B | Rating artefacts and table versions shall be stored write-once;… | REQ-PLT-002 |  |
| REQ-RAT-072 | J2 | W3 | M | B | resolve MKT configuration values (rounding, order of operations, tax… | REQ-MKT-001, REQ-MKT-051 |  |
| REQ-RAT-073 | J2 | W3 | M | B | obtain SPI implementations (TaxCalculator, PricingConstraint) through the MKT binding… | REQ-MKT-002, REQ-MKT-085 |  |
| REQ-RAT-074 | J2 | W3 | S | B | cache product artefacts and catalogues retrieved from PFC by… | REQ-PFC-002 |  |
| REQ-RAT-075 | J2 | W3 | S | B | require, for every charge type that the product version… | REQ-PFC-003 |  |
| REQ-RAT-076 | J2 | W3 | S | E | express algorithms in a typed, declarative, side-effect-free step language… | — |  |
| REQ-RAT-077 | J2 | W3 | M | B | step types base rate, multiplicative factor, additive component, lookup,… | — |  |
| REQ-RAT-078 | J2 | W3 | S | B | support multiplicative factor chains (base rate × ordered factors)… | — |  |
| REQ-RAT-079 | J2 | W3 | S | B | support additive components (fixed loadings, scorecard points, per-unit charges)… | — |  |
| REQ-RAT-080 | J2 | W3 | M | E | Every factor and additive component shall carry a declaration:… | — |  |
| REQ-RAT-081 | J2 | W3 | S | B | support exact-match lookups on one to six keys with… | — |  |
| REQ-RAT-082 | J2 | W3 | S | B | support banded lookups on numeric inputs with explicit lower… | — |  |
| REQ-RAT-083 | J2 | W3 | S | B | support linear and log-linear interpolation between points of a… | — |  |
| REQ-RAT-084 | J2 | W3 | S | B | Each lookup, band and curve shall declare its domain… | — |  |
| REQ-RAT-085 | J2 | W3 | S | E | Multi-row lookups shall declare a hit policy — unique,… | — |  |
| REQ-RAT-086 | J2 | W3 | S | B | support caps and floors on any step output, on… | — |  |
| REQ-RAT-087 | J2 | W3 | S | B | apply minimum premiums per coverage and per policy as… | — |  |
| REQ-RAT-088 | J2 | W3 | S | B | apply a declared maximum overall premium, either as a… | — |  |
| REQ-RAT-089 | J2 | W3 | S | B | evaluate conditional steps whose applicability is a typed boolean… | — |  |
| REQ-RAT-090 | J2 | W3 | S | B | aggregate across elements within a segment (counts, sums, maxima)… | — |  |
| REQ-RAT-091 | J2 | W3 | S | B | support steps at policy, policy-line, risk-unit and coverage level,… | REQ-PFC-113 |  |
| REQ-RAT-093 | J2 | W3 | S | E | separate technical premium (risk and cost factors) from commercial… | — |  |
| REQ-RAT-095 | J2 | W3 | S | B | use coverage term and option codes from the product… | — |  |
| REQ-RAT-096 | J2 | W3 | M | B | In RENEWAL mode the system shall cap the year-on-year… | — |  |
| REQ-RAT-097 | J2 | W3 | S | B | record capping state (uncapped rate, capped rate, cap applied)… | — |  |
| REQ-RAT-098 | J2 | W3 | S | B | apply any maximum renewal change returned by PricingConstraint.maxChange for… | REQ-MKT-110 |  |
| REQ-RAT-099 | J2 | W3 | S | E | emit signal RS-RENEWAL-CHANGE when the final renewal premium changes… | REQ-UW-001 |  |
| REQ-RAT-100 | J2 | W3 | M | B | perform all factor, rate and money arithmetic in decimal… | REQ-PLT-265 |  |
| REQ-RAT-101 | J2 | W3 | S | B | Rate tables shall store factors and values at a… | — |  |
| REQ-RAT-102 | J2 | W3 | S | B | output annual rates at scale 4 using mkt.Rounding.apply with… | REQ-MKT-195 |  |
| REQ-RAT-104 | J2 | W3 | S | E | Every step shall carry an explanation template in Greek… | — |  |
| REQ-RAT-105 | J2 | W3 | S | B | attribute every premium rate output to a coverage (or… | REQ-PFC-005 |  |
| REQ-RAT-106 | J2 | W3 | M | B | allocate policy-level discounts, loadings, minimum-premium top-ups and modifications to… | — |  |
| REQ-RAT-108 | J2 | W3 | S | B | verify after allocation that the sum of allocated lines… | — |  |
| REQ-RAT-109 | J2 | W3 | M | B | After the final premium stage the system shall call… | REQ-MKT-087 |  |
| REQ-RAT-110 | J2 | W3 | M | B | return tax and levy lines as separate charge types… | REQ-PFC-128, REQ-MKT-265 |  |
| REQ-RAT-111 | J2 | W3 | S | B | include flat fee charge types defined by the product… | — |  |
| REQ-RAT-112 | J2 | W3 | S | B | fail closed when TaxCalculator returns RULE_MISSING or VALIDATION, returning… | — |  |
| REQ-RAT-113 | J2 | W3 | S | E | report taxes separately from premium in CompareVersions and impact… | — |  |
| REQ-RAT-114 | J2 | W3 | S | B | declare, per discount or loading, whether it is emitted… | REQ-PFC-127 |  |
| REQ-RAT-115 | J2 | W3 | S | B | Build shall reconcile the rating artefact against the product's… | REQ-PFC-004 |  |
| REQ-RAT-117 | J2 | W3 | S | B | hold a discount and loading catalogue with code, Greek… | — |  |
| REQ-RAT-118 | J2 | W3 | S | B | apply discounts within a stacking group by the group's… | — |  |
| REQ-RAT-119 | J2 | W3 | S | B | enforce an overall discount cap per coverage and per… | — |  |
| REQ-RAT-120 | J2 | W3 | M | B | support multi-vehicle and multi-policy discounts driven by input counts… | REQ-PTY-102, REQ-POL-010 |  |
| REQ-RAT-121 | J2 | W3 | M | B | support a pay-in-full discount driven by the payment-plan code… | REQ-BIL-003, REQ-PFC-133 |  |
| REQ-RAT-122 | J2 | W3 | S | B | support new-business and loyalty discounts using tenure input, subject… | — | condition truncated in source ('subject to the renewal…') |
| REQ-RAT-124 | J2 | W3 | S | B | support pricing modification types: percentage per coverage, percentage on… | — |  |
| REQ-RAT-125 | J2 | W3 | S | B | Each pricing modification shall record proposer, job and quote… | REQ-PLT-002 |  |
| REQ-RAT-126 | J2 | W3 | M | B | register authority type RAT.PricingDeviation with dimensions product, line of… | REQ-PLT-003 |  |
| REQ-RAT-127 | J2 | W3 | M | B | When the authority check returns refer-to, the system shall… | REQ-PLT-004 |  |
| REQ-RAT-128 | J2 | W3 | S | B | apply approved modifications at the declared step (after discounts,… | — |  |
| REQ-RAT-129 | J2 | W3 | S | B | show each modification as a step naming proposer, approver… | — |  |
| REQ-RAT-130 | J2 | W3 | S | B | A modification shall become Invalidated when the risk inputs'… | REQ-UW-003 |  |
| REQ-RAT-131 | J2 | W3 | S | B | emit signal RS-DEVIATION-APPLIED whenever a modification affects the price… | REQ-UW-001 |  |
| REQ-RAT-135 | J2 | W3 | M | B | the input schema shall support rating region | REQ-POL-010 | Greece pack |
| REQ-RAT-136 | J2 | W3 | S | B | accept vehicle value and attributes from MotorDataProvider (obtained by… | — |  |
| REQ-RAT-137 | J2 | W3 | M | B | model a bonus-malus scale as a versioned transition table… | — |  |
| REQ-RAT-138 | J2 | W3 | S | B | rat.BonusMalus.transition(scaleRef, currentClass, claimCount, protectionHeld, period) as a pure function… | REQ-POL-009 |  |
| REQ-RAT-139 | J2 | W3 | S | B | Validation shall check that bonus-malus factors are non-decreasing as… | — |  |
| REQ-RAT-140 | J2 | W3 | M | B | accept a structured claims-history certificate summary mirroring the sections… | REQ-CLM-008 |  |
| REQ-RAT-141 | J2 | W3 | M | B | reject at build any algorithm step, eligibility or entry-class… | — |  |
| REQ-RAT-142 | J2 | W3 | S | B | record which certificate fields affected the price and how… | — |  |
| REQ-RAT-143 | J2 | W3 | S | B | When no certificate is supplied the system shall apply… | REQ-UW-001 |  |
| REQ-RAT-144 | J2 | W3 | M | B | obtain the prohibited factors for the jurisdiction and line… | REQ-MKT-110 |  |
| REQ-RAT-145 | J2 | W3 | S | E | Factors classified commercial (not risk or cost based) shall… | REQ-PFC-007 |  |
| REQ-RAT-146 | J2 | W3 | S | E | In RENEWAL mode the system shall compute the new-business-equivalent… | — |  |
| REQ-RAT-152 | J2 | W3 | S | B | Age-based factors shall be allowed only where the factor… | — |  |
| REQ-RAT-154 | J2 | W3 | M | B | rate the Cyprus motor variant with | REQ-MKT-008, REQ-MKT-266 | Cyprus stub pack |
| REQ-RAT-155 | J2 | W3 | S | B | rat.Proration.prorate shall accept annual rates per element × charge… | REQ-PFC-135 |  |
| REQ-RAT-156 | J2 | W3 | S | B | support day-count conventions ACT/365F, ACT/ACT, 30E/360 and TERM_RATIO (segment… | REQ-PFC-135 |  |
| REQ-RAT-157 | J2 | W3 | S | B | Segment periods shall be dates in the legal entity's… | REQ-POL-006 |  |
| REQ-RAT-158 | J2 | W3 | S | B | Under TERM_RATIO the sum of unrounded amounts of contiguous… | — |  |
| REQ-RAT-159 | J2 | W3 | S | B | round each amount through mkt.Rounding.apply with purpose PREMIUM or… | REQ-MKT-195 |  |
| REQ-RAT-160 | J2 | W3 | S | B | Flat charge types shall not be prorated, and fully-earned… | REQ-PFC-114 |  |
| REQ-RAT-161 | J2 | W3 | S | B | hold short-rate tables as RAT rate tables of type… | REQ-PFC-134 |  |
| REQ-RAT-162 | J2 | W3 | S | B | Where the configured order of operations computes tax on… | REQ-MKT-087 |  |
| REQ-RAT-163 | J2 | W3 | S | B | Prorating a reversal (negative period sign) shall produce the… | REQ-POL-006 |  |
| REQ-RAT-164 | J2 | W3 | S | B | run in-process for POL and as an API with… | REQ-POL-005 |  |
| REQ-RAT-165 | J2 | W3 | S | B | read the day-count convention and term length from the… | REQ-PFC-135 |  |
| REQ-RAT-167 | J2 | W3 | S | B | import rate tables from CSV, spreadsheet workbooks and JSON… | — |  |
| REQ-RAT-168 | J2 | W3 | S | B | apply a saved column-mapping profile per source (source column… | — |  |
| REQ-RAT-169 | J2 | W3 | M | B | Each rate table shall declare code, Greek and English… | — |  |
| REQ-RAT-170 | J2 | W3 | S | B | Validation shall check types, scales, units, required cells and… | — |  |
| REQ-RAT-171 | J2 | W3 | S | B | Validation shall check key completeness against PFC catalogue codes… | — |  |
| REQ-RAT-172 | J2 | W3 | S | B | Validation shall detect gaps between numeric or date bands… | — |  |
| REQ-RAT-173 | J2 | W3 | S | B | Validation shall detect overlapping bands and, for unique hit… | — |  |
| REQ-RAT-174 | J2 | W3 | S | B | Validation shall check declared monotonicity (non-decreasing or non-increasing in… | — |  |
| REQ-RAT-175 | J2 | W3 | S | B | Validation shall compare each cell with the prior active… | — |  |
| REQ-RAT-176 | J2 | W3 | S | E | Table versions shall be content-addressed; importing identical content shall… | — |  |
| REQ-RAT-177 | J2 | W3 | S | B | show a cell-level diff between any two versions of… | — |  |
| REQ-RAT-178 | J2 | W3 | S | E | Algorithm sources, table versions and declarations shall be kept… | REQ-PFC-182 |  |
| REQ-RAT-179 | J2 | W3 | S | B | compile a change set into a candidate rating artefact… | — |  |
| REQ-RAT-180 | J2 | W3 | S | E | Build lint shall report unreachable steps, unused tables, undeclared… | — |  |
| REQ-RAT-181 | J2 | W3 | S | B | call PricingConstraint.check with a summary of the artefact's factor… | REQ-MKT-110 |  |
| REQ-RAT-182 | J2 | W3 | S | B | Imports and golden cases shall be scanned for personal… | REQ-PFC-236 |  |
| REQ-RAT-183 | J2 | W3 | M | B | Every import shall archive the original file with its… | REQ-DOC-004, REQ-PLT-002 |  |
| REQ-RAT-185 | J2 | W3 | S | B | hold, per rating slot, a golden suite of input… | REQ-PFC-234 |  |
| REQ-RAT-186 | J2 | W3 | S | B | A golden run shall fail on any difference at… | — |  |
| REQ-RAT-187 | J2 | W3 | S | B | Golden runs shall execute on every build and before… | REQ-PLT-265 |  |
| REQ-RAT-188 | J2 | W3 | S | B | Re-baselining golden expectations shall list every changed case with… | REQ-PLT-004 |  |
| REQ-RAT-189 | J2 | W3 | S | E | Golden suites shall carry tagged regulatory cases: gender-neutral pairs,… | — |  |
| REQ-RAT-190 | J2 | W3 | M | B | run property-based tests per artefact over generated valid inputs:… | — |  |
| REQ-RAT-191 | J2 | W3 | S | E | fuzz inputs within the input schema and require zero… | — |  |
| REQ-RAT-192 | J2 | W3 | S | B | compare the engine with an independent oracle — the… | — |  |
| REQ-RAT-193 | J2 | W3 | M | B | Oracle runs shall export sampled inputs (synthetic or pseudonymised)… | REQ-PLT-011, REQ-DAT-001 |  |
| REQ-RAT-194 | J2 | W3 | M | B | CI shall run the Cyprus stub rating variant's golden… | REQ-MKT-008, REQ-PFC-239 | Cyprus stub pack |
| REQ-RAT-195 | J2 | W3 | S | E | measure engine compute latency per artefact on a reference… | — |  |
| REQ-RAT-196 | J2 | W3 | S | B | keep golden run history per artefact (date, result, failing… | — |  |
| REQ-RAT-197 | J2 | W8 | S | B | rat.Comparison.compareVersions shall rate one input set on two to… | — | W8 |
| REQ-RAT-198 | J2 | W8 | S | B | Input sets for comparison shall be selectable from the… | REQ-DAT-002 | W8 |
| REQ-RAT-199 | J2 | W8 | S | B | Comparison results shall provide distribution metrics (mean, median, 5th,… | — | W8 |
| REQ-RAT-200 | J2 | W8 | S | B | rat.ShadowRun.start shall rate a sampled share of live production… | — | W8 |
| REQ-RAT-201 | J2 | W8 | S | B | A ShadowRun shall be configured with candidate artefact, slot,… | — | W8 |
| REQ-RAT-202 | J2 | W8 | S | E | ShadowRun results shall store per sampled request only a… | — | W8 |
| REQ-RAT-203 | J2 | W8 | S | B | On completion the system shall produce a comparison report… | REQ-PFC-208 | W8 |
| REQ-RAT-204 | J2 | W8 | M | B | rat.ImpactAnalysis.run shall rate the in-force book of a slot… | REQ-DAT-002, REQ-POL-002 | W8 |
| REQ-RAT-205 | J2 | W8 | S | B | Impact analysis shall rate upcoming renewals in a chosen… | — | W8 |
| REQ-RAT-206 | J2 | W8 | S | B | Impact results shall be sliceable by the dimensions of… | — | W8 |
| REQ-RAT-207 | J2 | W8 | S | E | Impact analysis shall include a fairness view comparing renewal… | — | W8 |
| REQ-RAT-209 | J2 | W8 | S | B | Impact and comparison results shall be attached to the… | REQ-PFC-208 | W8 |
| REQ-RAT-210 | J2 | W8 | S | B | Impact analysis shall run asynchronously with progress, be resumable… | — | W8 |
| REQ-RAT-211 | J2 | W8 | S | B | Each impact run shall record the population snapshot hash… | REQ-DAT-007 | W8 |
| REQ-RAT-213 | J2 | W3 | M | B | Submission of a candidate artefact shall require: lint and… | — |  |
| REQ-RAT-214 | J2 | W3 | M | B | On submission the system shall freeze an governance bundle… | REQ-DOC-004 |  |
| REQ-RAT-215 | J2 | W3 | M | B | Approval shall run through maker-checker approval type RAT.RateArtifactApproval requiring… | REQ-PLT-004 |  |
| REQ-RAT-216 | J2 | W3 | S | B | enforce segregation of duties author ≠ approver ≠ activator… | REQ-PLT-082 |  |
| REQ-RAT-217 | J2 | W3 | S | B | Any approver shall be able to return a Submitted… | — |  |
| REQ-RAT-220 | J2 | W3 | M | B | schedule an Approved artefact per slot with separate new-business… | — |  |
| REQ-RAT-221 | J2 | W3 | S | B | At the scheduled time the workflow engine shall activate… | REQ-PLT-007 |  |
| REQ-RAT-222 | J2 | W3 | S | B | Rollback shall activate the predecessor (or another Approved artefact)… | — |  |
| REQ-RAT-223 | J2 | W3 | S | E | After a rollback the system shall list quotes and… | REQ-WRK-001 |  |
| REQ-RAT-224 | J2 | W3 | M | E | emergency change path (authority type RAT.EmergencyRateChange) with approval by… | REQ-WRK-009, REQ-PLT-003 |  |
| REQ-RAT-225 | J2 | W3 | S | B | be able to cancel or reschedule a scheduled activation… | — |  |
| REQ-RAT-226 | J2 | W3 | S | E | Artefacts shall be promoted through environments (development, test, acceptance,… | REQ-PLT-265 |  |
| REQ-RAT-227 | J2 | W3 | M | B | Each worksheet shall contain a header (worksheet id, rating… | — |  |
| REQ-RAT-228 | J2 | W3 | S | E | Worksheets shall be stored compressed and content-addressed (worksheet id… | — |  |
| REQ-RAT-229 | J2 | W3 | M | B | rat.Worksheet.attach(worksheetIds, transactionRef) called by POL at bind and issue… | REQ-POL-001, REQ-WRK-001 |  |
| REQ-RAT-230 | J2 | W3 | S | B | Worksheets attached to transactions shall be retained for the… | REQ-PLT-011 |  |
| REQ-RAT-231 | J2 | W3 | S | B | export, for a DSAR, all worksheets attached to the… | REQ-CMP-005 |  |
| REQ-RAT-232 | J2 | W3 | S | B | Worksheets under legal hold shall not be deleted by… | REQ-PLT-011 |  |
| REQ-RAT-233 | J2 | W3 | M | B | rat.Worksheet.explain shall return a structured explanation: the step tree… | — |  |
| REQ-RAT-234 | J2 | W3 | S | B | embeddable worksheet viewer component (SCR-RAT-10) used inside UW and… | — |  |
| REQ-RAT-235 | J2 | W3 | M | B | Breakdown data (REQ-RAT-008) shall group lines by the display… | REQ-PFC-113 |  |
| REQ-RAT-236 | J2 | W3 | S | B | rat.ChangeExplanation.get(worksheetBefore, worksheetAfter) shall decompose a premium change into effects… | — |  |
| REQ-RAT-237 | J2 | W3 | S | E | use a declared fixed order of effects per product… | — |  |
| REQ-RAT-238 | J2 | W3 | M | B | generate, per product and language, a "how we calculate… | REQ-DOC-001 |  |
| REQ-RAT-239 | J2 | W3 | S | B | Each rating response in modes FULL, ENDORSEMENT and RENEWAL… | REQ-CHN-004 |  |
| REQ-RAT-240 | J2 | W3 | S | B | rat.PriceReview.request that creates a human review case for a… | REQ-WRK-001 |  |
| REQ-RAT-241 | J2 | W3 | S | B | A review outcome shall record reviewer, decision (price confirmed,… | REQ-PLT-002 |  |
| REQ-RAT-242 | J2 | W3 | S | B | Only users with the pricing-review permission may decide reviews,… | REQ-PLT-001 |  |
| REQ-RAT-243 | J2 | W3 | S | E | rat.Worksheet.reperform(worksheetId) that re-rates the stored input under the stored… | — |  |
| REQ-RAT-244 | J2 | W3 | S | E | A nightly job shall re-perform a random sample of… | REQ-PLT-012 |  |
| REQ-RAT-245 | J2 | W3 | M | B | generate and version a claims-history usage statement per product… | REQ-DOC-001 |  |
| REQ-RAT-247 | J2 | W3 | S | B | Explanations and breakdowns shall be available in Greek and… | REQ-MKT-005 |  |
| REQ-RAT-249 | J2 | W8 | M | E | publish event RatingCalculated (accepted, R-22) for every FULL, ENDORSEMENT,… | REQ-DAT-001 | per contract ruling; W8 |
| REQ-RAT-250 | J2 | W8 | S | B | Each rating artefact shall declare its rating-cell definition (the… | — | W8 |
| REQ-RAT-251 | J2 | W8 | S | B | consume DAT monitoring views (conversion, retention, loss ratio, premium… | REQ-DAT-003 | W8 |
| REQ-RAT-252 | J2 | W8 | S | E | show renewal premium versus new-business-equivalent premium by tenure band… | — | W8 |
| REQ-RAT-255 | J2 | W8 | S | B | expose business SLIs: ratings per minute by mode, p95… | REQ-PLT-013 | W8 |
| REQ-RAT-269 | J13 | W9 | S | B | rate converted renewal populations in RENEWAL mode with legacy… | REQ-MIG-003 | migration (J13/W9) |
| REQ-RAT-270 | J13 | W9 | S | B | accept legacy prior-term rates per coverage, mapped by MIG… | REQ-MIG-002 | migration (J13/W9) |
| REQ-RAT-272 | J13 | W9 | S | B | hold a mapping table from legacy bonus-malus classes to… | — | migration (J13/W9) |
| REQ-RAT-273 | J13 | W9 | S | B | rate converted policies only with new rating artefacts and… | REQ-MIG-004 | migration (J13/W9) |
| REQ-RAT-274 | J13 | W9 | S | B | reconciliation counts for migration batches (submitted, rated, failed by… | REQ-MIG-005 | migration (J13/W9) |
| REQ-RAT-275 | J13 | W9 | M | B | Every RAT import or migration operation — rat.RateTable.import, rat.LegacyRating.import… | REQ-MIG-001, REQ-MIG-002 | migration (J13/W9) |

## A.1.6 UW (183 Musts, 269 points): 183 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-UW-001 | J2 | W3 | XL | B | uw.Rules.evaluate(jobRef, checkpoint, riskSnapshot, signals, dryRun) for checkpoints PRE_QUOTE, PRE_BIND,… | REQ-PFC-001, REQ-PFC-131, REQ-PLT-174, REQ-RAT-006, REQ-POL-001 |  |
| REQ-UW-002 | J2 | W3 | XL | B | maintain the UW issue lifecycle (section 7.3) and provide… | REQ-POL-003 |  |
| REQ-UW-003 | J2 | W3 | XL | B | determine approval validity and invalidation: each approval holds a… | — |  |
| REQ-UW-004 | J2 | W3 | XL | B | create a referral for each job whose blocking issues… | REQ-WRK-001, REQ-WRK-003, REQ-WRK-009 |  |
| REQ-UW-005 | J2 | W3 | XL | B | record declines as structured records (reasons by peril ×… | REQ-MKT-002, REQ-MKT-103, REQ-MKT-307, REQ-DOC-001, REQ-POL-011 |  |
| REQ-UW-006 | J2 | W3 | XL | B | contingency API uw.Contingency.create / update / satisfy / waive… | REQ-WRK-001, REQ-WRK-196 |  |
| REQ-UW-007 | J2 | W3 | XL | B | uw.PolicyHold.check(jobType, product, lineOfBusiness, coverages, riskLocations, transactionDate, effectiveDate) returning the… | REQ-POL-003, REQ-MKT-002 |  |
| REQ-UW-009 | J2 | W3 | XL | B | order, receive, cache, expire and re-order external reports and… | REQ-PTY-005, REQ-PLT-006 |  |
| REQ-UW-010 | J2 | W3 | XL | B | register its underwriting authority types with the PLT authority… | REQ-PLT-100, REQ-PLT-103, REQ-PLT-110 |  |
| REQ-UW-030 | J2 | W3 | M | B | hold UW rule sets with code, name (GR/EN), checkpoint,… | REQ-PLT-175 |  |
| REQ-UW-031 | J2 | W3 | M | B | hold each rule inside a rule-set version with name… | — |  |
| REQ-UW-032 | J2 | W3 | M | E | express rule conditions as decision tables with typed input… | REQ-PLT-174 |  |
| REQ-UW-033 | J2 | W3 | S | B | support rule variables (name, description, expression over inputs, type)… | — |  |
| REQ-UW-035 | J2 | W3 | S | B | decision-table editor in business vocabulary with Greek and English… | — |  |
| REQ-UW-036 | J2 | W3 | S | B | validate every rule on save (types, unreachable rows, overlapping… | — |  |
| REQ-UW-037 | J2 | W3 | S | E | require each rule to declare its approval-sensitive inputs with… | — |  |
| REQ-UW-038 | J2 | W3 | S | B | require test cases with every rule-set version (inputs, expected… | REQ-PLT-176 |  |
| REQ-UW-039 | J2 | W3 | S | E | let authors run the test table on demand and… | — |  |
| REQ-UW-040 | J2 | W3 | M | B | activate a rule-set version only after maker-checker approval through… | REQ-PLT-004, REQ-PLT-115 |  |
| REQ-UW-041 | J2 | W3 | S | E | schedule activation of an Approved version for an effective… | REQ-PLT-332 |  |
| REQ-UW-042 | J2 | W3 | S | B | record on every evaluation the rule-set code, version and… | — |  |
| REQ-UW-043 | J2 | W3 | S | B | resolve the rule-set version through the product version's floating… | REQ-PFC-001 |  |
| REQ-UW-044 | J2 | W3 | S | B | enable and disable individual rules in a new version… | — |  |
| REQ-UW-046 | J2 | W3 | M | E | simulate a Draft rule-set version against a sample of… | REQ-DAT-001 |  |
| REQ-UW-047 | J2 | W3 | S | B | show rule-set and rule history: who changed what and… | REQ-PLT-002 |  |
| REQ-UW-048 | J2 | W3 | M | B | import and export rule-set versions as files in a… | — |  |
| REQ-UW-049 | J2 | W3 | S | E | validate imported rule sets with the same validation and… | — |  |
| REQ-UW-050 | J2 | W3 | M | B | maintain an issue-type catalogue (code, name and description in… | — |  |
| REQ-UW-051 | J2 | W3 | M | B | list rules with filters (name/description, status, availability, job type,… | — |  |
| REQ-UW-052 | J2 | W3 | S | B | delete only Draft rules that were never part of… | — |  |
| REQ-UW-055 | J2 | W3 | L | B | build the evaluation input from the POL risk snapshot… | REQ-POL-002, REQ-RAT-006, REQ-PFC-006, REQ-PTY-001 |  |
| REQ-UW-056 | J2 | W3 | S | B | evaluate PRE_QUOTE before rating results are released to channels,… | REQ-POL-011 |  |
| REQ-UW-057 | J2 | W3 | S | B | evaluate within the quote latency budget (NFR-UW-001) and, if… | REQ-PLT-174 |  |
| REQ-UW-058 | J2 | W3 | S | B | support dry-run evaluation that returns issues without persisting or… | REQ-CHN-001 |  |
| REQ-UW-059 | J2 | W3 | S | B | reconcile hits with existing issues by issue key: new… | — |  |
| REQ-UW-060 | J2 | W3 | M | B | apply severity outcomes: decline rules mark the job as… | — |  |
| REQ-UW-061 | J2 | W3 | S | B | return per hit an explanation trace (rule, version hash,… | REQ-PLT-177 |  |
| REQ-UW-063 | J2 | W3 | S | B | record, for every evaluation that uses question-set answers, the… | REQ-PFC-006 |  |
| REQ-UW-064 | J2 | W3 | S | E | support a lane output on referral rule sets with… | — |  |
| REQ-UW-065 | J2 | W3 | S | E | never derive a lane, eligibility or referral outcome from… | — |  |
| REQ-UW-066 | J2 | W3 | S | E | attach to every issue three texts from the issue… | REQ-CHN-005 |  |
| REQ-UW-068 | J2 | W3 | S | B | evaluate question-set knock-out and referral flags defined by PFC… | REQ-PFC-006 |  |
| REQ-UW-069 | J2 | W3 | M | B | consume RAT referral signals and pricing modifications outside a… | REQ-RAT-005, REQ-RAT-006 |  |
| REQ-UW-070 | J2 | W3 | S | B | raise issues from policy holds (REQ-UW-007) and accumulation (REQ-UW-008)… | — |  |
| REQ-UW-071 | J2 | W3 | M | B | raise a blocking issue SANCTIONS_REVIEW when the policyholder, insured… | REQ-PTY-006, REQ-POL-003 |  |
| REQ-UW-072 | J2 | W3 | M | B | evaluate rules in bulk for renewal and campaign batches… | REQ-PLT-172, REQ-PLT-173 |  |
| REQ-UW-075 | J2 | W3 | M | B | hold each UW issue with issue id, job, issue… | — |  |
| REQ-UW-076 | J2 | W3 | S | B | apply the transitions, guards and events of section 7.3… | — |  |
| REQ-UW-077 | J2 | W3 | S | B | group issues by blocking point and status in the… | — |  |
| REQ-UW-078 | J2 | W3 | S | B | filter the issue list by "issues I can decide",… | REQ-PLT-103 |  |
| REQ-UW-079 | J2 | W3 | S | B | let a user approve, reject or approve with conditions… | — |  |
| REQ-UW-080 | J2 | W3 | M | B | let a user with authority reopen an Approved, ApprovedWithConditions… | — |  |
| REQ-UW-081 | J2 | W3 | S | B | show the decision history of an issue key across… | — |  |
| REQ-UW-082 | J2 | W3 | S | B | let a user lock a job for underwriting review… | REQ-POL-001 |  |
| REQ-UW-084 | J2 | W3 | S | B | uw.Issue.listForChannel(jobRef, audience) returning issues with intermediary or customer texts,… | REQ-CHN-005 |  |
| REQ-UW-085 | J2 | W3 | S | B | uw.Referral.request(jobRef, issueIds, comment, attachments) for producers and staff, creating… | REQ-WRK-260 |  |
| REQ-UW-087 | J2 | W3 | S | B | present the blocking-issues interstitial when a user tries to… | REQ-POL-003 |  |
| REQ-UW-088 | J2 | W3 | S | B | default an approval's scope, validity and edit tolerance from… | — |  |
| REQ-UW-089 | J2 | W3 | S | B | support approval scope values PRE_QUOTE, PRE_BIND and PRE_ISSUE ("approved… | — |  |
| REQ-UW-090 | J2 | W3 | S | B | support validity values THIS_JOB, NEXT_CHANGE, END_OF_TERM, ONE_YEAR, THREE_YEARS (configurable… | — |  |
| REQ-UW-091 | J2 | W3 | S | E | compute and store, on approval, a fingerprint of the… | — |  |
| REQ-UW-092 | J2 | W3 | S | B | retain an approval on re-evaluation when the same issue… | — |  |
| REQ-UW-093 | J2 | W3 | S | B | invalidate an approval when any sensitive input leaves tolerance,… | — |  |
| REQ-UW-095 | J2 | W3 | M | B | let an approver approve with conditions, selecting condition templates… | — |  |
| REQ-UW-096 | J2 | W3 | M | E | apply PRODUCT_CHANGE conditions by calling the POL job command… | REQ-POL-001, REQ-RAT-001 |  |
| REQ-UW-097 | J2 | W3 | S | B | turn CONTINGENCY conditions into contingencies that become active at… | — |  |
| REQ-UW-098 | J2 | W3 | S | B | carry approvals forward to renewal and mid-term change jobs… | — |  |
| REQ-UW-099 | J2 | W3 | S | E | not carry forward approvals with validity THIS_JOB, approvals of… | — |  |
| REQ-UW-100 | J2 | W3 | S | B | let underwriters add a manual issue (type from catalogue,… | — |  |
| REQ-UW-101 | J2 | W3 | M | B | re-evaluate issues automatically when POL publishes a risk change… | REQ-POL-006, REQ-PTY-006 |  |
| REQ-UW-105 | J2 | W3 | S | B | register with PLT the authority types of section 12.2… | REQ-PLT-100 |  |
| REQ-UW-106 | J2 | W3 | S | B | map each issue type to the authority types and… | — |  |
| REQ-UW-107 | J2 | W3 | S | B | support the issue-type lookup as the selection source for… | REQ-PLT-109 |  |
| REQ-UW-108 | J2 | W3 | S | B | compute the authority dimensions of each decision from the… | REQ-POL-002 |  |
| REQ-UW-109 | J2 | W3 | S | B | run a preview authority check when the decision panel… | REQ-PLT-103 |  |
| REQ-UW-110 | J2 | W3 | M | B | show in the decision panel, for each required authority… | REQ-PLT-103, REQ-WRK-201 |  |
| REQ-UW-111 | J2 | W3 | S | B | store with every decision the check ids, the authority… | REQ-PLT-110 |  |
| REQ-UW-112 | J2 | W3 | M | B | escalate a referral automatically to the refer-to target returned… | REQ-WRK-201, REQ-WRK-010 |  |
| REQ-UW-113 | J2 | W3 | M | B | require a second approver through the maker-checker service (approval… | REQ-PLT-004, REQ-PLT-115 |  |
| REQ-UW-115 | J2 | W3 | S | B | never accept an AI identity or a service identity… | REQ-PLT-111 |  |
| REQ-UW-116 | J2 | W3 | S | E | prevent the producer or user who requested the referral,… | REQ-PLT-001 |  |
| REQ-UW-120 | J2 | W3 | S | E | create referrals automatically, without a producer request, when the… | REQ-CHN-002 |  |
| REQ-UW-121 | J2 | W3 | M | B | maintain a referral routing profile (UW-owned data): routing key… | REQ-WRK-003, REQ-WRK-120 |  |
| REQ-UW-122 | J2 | W3 | S | B | pass the referral's routing hints, priority score, amount band… | REQ-WRK-185 |  |
| REQ-UW-123 | J2 | W3 | S | B | publish ReferralAssigned when WRK reports the referral activity assigned… | REQ-WRK-139 |  |
| REQ-UW-124 | J2 | W3 | S | E | consolidate referrals per job: further issues raised while a… | REQ-WRK-196 |  |
| REQ-UW-126 | J2 | W3 | S | B | route referrals of decline proposals, non-renewal proposals and sanctions-related… | REQ-WRK-200 |  |
| REQ-UW-127 | J2 | W3 | M | B | compute a referral priority score from a versioned scoring… | — |  |
| REQ-UW-128 | J2 | W3 | S | B | explain each priority score with factor contributions (IB-08) in… | — |  |
| REQ-UW-129 | J2 | W3 | S | B | recompute priority scores when a factor changes (deadline approaching… | — |  |
| REQ-UW-130 | J2 | W3 | S | B | never use individual underwriter behaviour, performance or traits as… | — |  |
| REQ-UW-131 | J2 | W3 | M | B | start SLA timing through WRK SLA policies per referral… | REQ-WRK-009 |  |
| REQ-UW-132 | J2 | W3 | S | B | pause the referral SLA while the referral waits for… | REQ-WRK-085 |  |
| REQ-UW-133 | J2 | W3 | S | B | publish ReferralSLABreached when WRK reports SLABreached or an escalation… | REQ-WRK-089 |  |
| REQ-UW-134 | J2 | W3 | M | B | show a team lens of referrals by underwriter and… | REQ-WRK-129, REQ-WRK-132 |  |
| REQ-UW-135 | J2 | W3 | M | B | support pull ("Get next") and push modes for referral… | REQ-WRK-126, REQ-WRK-127 |  |
| REQ-UW-137 | J2 | W3 | M | B | offer a referral routing and priority configuration screen (SCR-UW-20)… | REQ-WRK-135 |  |
| REQ-UW-140 | J2 | W3 | M | B | underwriter workbench (SCR-UW-01) with sidebar work views and live… | REQ-WRK-111 |  |
| REQ-UW-141 | J2 | W3 | S | B | filter the workbench by status, assignee, line, product, channel,… | REQ-WRK-006 |  |
| REQ-UW-145 | J2 | W3 | M | B | risk analysis panel inside every job (submission, policy change,… | REQ-POL-001 |  |
| REQ-UW-146 | J2 | W3 | M | B | show prior policies of the parties and vehicles or… | REQ-POL-014, REQ-PTY-262 |  |
| REQ-UW-147 | J2 | W3 | M | B | show claims and prior losses from CLM (own claims)… | REQ-CLM-009, REQ-CLM-011 |  |
| REQ-UW-148 | J2 | W3 | S | B | replace the US motor-vehicle-record tab with a "Driving and… | — |  |
| REQ-UW-149 | J2 | W3 | M | B | risk and account review (SCR-UW-05) with tabs Summary, Risk… | REQ-PTY-260, REQ-POL-002 |  |
| REQ-UW-150 | J2 | W3 | S | B | show on Summary: party and account essentials, producer and… | REQ-PTY-009 |  |
| REQ-UW-151 | J2 | W3 | S | E | show Risk details per risk unit (vehicles, drivers, buildings,… | REQ-POL-010 |  |
| REQ-UW-153 | J2 | W3 | S | B | show the Sanctions tab with each relevant party's screening… | REQ-PTY-184 |  |
| REQ-UW-154 | J2 | W3 | M | B | embed the DOC Documents tab and WRK inbound documents… | REQ-DOC-008, REQ-WRK-285 |  |
| REQ-UW-155 | J2 | W3 | S | B | show History: decisions, issue events, authority checks, external report… | REQ-PLT-002 |  |
| REQ-UW-156 | J2 | W3 | S | B | let the underwriter request information from the producer or… | REQ-WRK-008 |  |
| REQ-UW-157 | J2 | W3 | S | B | let underwriters add notes on the referral, job or… | REQ-WRK-004 |  |
| REQ-UW-161 | J2 | W3 | S | B | show the job info bar with job type and… | REQ-WRK-010 |  |
| REQ-UW-185 | J2 | W3 | M | B | maintain an external data provider catalogue: provider, report types,… | REQ-PLT-006 |  |
| REQ-UW-186 | J2 | W3 | S | B | link each provider to the PLT ICT third-party register… | REQ-PLT-012 |  |
| REQ-UW-187 | J2 | W3 | M | B | record each provider's processing and storage location in the… | REQ-PLT-285 |  |
| REQ-UW-188 | J2 | W3 | S | B | check consent or another recorded lawful basis before every… | REQ-PTY-005 |  |
| REQ-UW-189 | J2 | W3 | S | B | send to providers only the fields defined for the… | — |  |
| REQ-UW-190 | J2 | W3 | S | B | never use protected characteristics or their proxies from external… | REQ-MKT-002 |  |
| REQ-UW-191 | J2 | W3 | S | B | reuse a cached report of the same type for… | — |  |
| REQ-UW-192 | J2 | W3 | S | B | expire reports at the end of their expiry period,… | — |  |
| REQ-UW-195 | J2 | W3 | M | B | ingest claims-history statements (from another insurer, uploaded by customer… | REQ-WRK-273, REQ-CLM-008 |  |
| REQ-UW-196 | J2 | W3 | S | B | obtain vehicle and licence data through MotorDataProvider (Greece pack:… | — |  |
| REQ-UW-199 | J2 | W3 | S | B | show the external reports panel (SCR-UW-16) with type, provider,… | — |  |
| REQ-UW-200 | J2 | W3 | S | B | order inspections (photo self-inspection, field inspection, survey) before or… | REQ-WRK-001 |  |
| REQ-UW-201 | J2 | W3 | M | E | support photo self-inspection by the customer through CHN with… | REQ-CHN-004, REQ-WRK-260 |  |
| REQ-UW-203 | J2 | W3 | S | B | record inspection results: findings (code list per subject type),… | — |  |
| REQ-UW-204 | J2 | W3 | M | B | offer actions from inspection results: approve, raise or update… | REQ-POL-001, REQ-POL-012 |  |
| REQ-UW-205 | J2 | W3 | S | B | make an inspection a contingency automatically when ordered post-bind… | — |  |
| REQ-UW-213 | J2 | W3 | S | B | show the inspection and valuation screen (SCR-UW-15) with orders,… | — |  |
| REQ-UW-215 | J2 | W3 | M | B | let a user with UW.DECLINE authority decline a draft… | REQ-POL-011 |  |
| REQ-UW-216 | J2 | W3 | S | B | issue a decline number from the PLT numbering service… | REQ-PLT-014 |  |
| REQ-UW-218 | J2 | W3 | S | B | compose customer-facing decline wording from the reasons' customer texts… | — |  |
| REQ-UW-219 | J2 | W3 | M | B | allow automated declines only when the product's automated-decline capability… | REQ-CHN-001 |  |
| REQ-UW-220 | J2 | W3 | S | B | handle a human-review request of an automated decline by… | — |  |
| REQ-UW-221 | J2 | W3 | S | B | hand a customer complaint about a decline to CMP… | REQ-CMP-004 |  |
| REQ-UW-222 | J2 | W3 | M | B | call RefusalDocumentRule.evaluate(declineRecord) for every decline and store the result… | REQ-MKT-002, REQ-MKT-103, REQ-MKT-307 |  |
| REQ-UW-235 | J2 | W3 | M | B | hold contingencies with type (document, inspection, payment of arrears… | — |  |
| REQ-UW-236 | J2 | W3 | S | B | let underwriters add contingencies manually from the risk analysis… | — |  |
| REQ-UW-237 | J2 | W3 | M | B | send reminders to the owner and the producer (via… | REQ-WRK-007, REQ-CHN-009 |  |
| REQ-UW-238 | J2 | W3 | M | B | accept evidence through WRK inbound documents linked to the… | REQ-WRK-289 |  |
| REQ-UW-239 | J2 | W3 | S | B | let a user with UW.CONDITION_WAIVER authority waive or extend… | REQ-WRK-196 |  |
| REQ-UW-240 | J2 | W3 | S | B | publish ContingencyOverdue when the due date passes unsatisfied and… | REQ-PLT-172 |  |
| REQ-UW-241 | J2 | W3 | S | B | define the consequence on expiry per contingency type: none… | — |  |
| REQ-UW-242 | J2 | W3 | S | B | never apply a consequence automatically; the underwriter confirms each… | REQ-POL-001 |  |
| REQ-UW-243 | J2 | W3 | S | B | apply a confirmed consequence through POL (policy change or… | REQ-POL-012 |  |
| REQ-UW-245 | J2 | W3 | S | B | show the contingency tracker (SCR-UW-14) across policies with filters… | — |  |
| REQ-UW-246 | J2 | W3 | S | B | expose contingencies to CHN for producers and customers with… | REQ-CHN-005 |  |
| REQ-UW-248 | J2 | W3 | S | B | list policy holds with hold type, code, description, start… | — |  |
| REQ-UW-249 | J2 | W3 | S | B | create a hold with hold type (underwriting hold, regulatory… | — |  |
| REQ-UW-250 | J2 | W3 | S | B | define hold rules as rows of line of business,… | — |  |
| REQ-UW-251 | J2 | W3 | S | B | define hold regions by country subdivision, postcode sets or… | — |  |
| REQ-UW-252 | J2 | W3 | S | B | copy an existing hold into a new Draft and… | — |  |
| REQ-UW-253 | J2 | W3 | S | B | activate holds only by users with UW.POLICY_HOLD authority, publish… | REQ-WRK-189 |  |
| REQ-UW-254 | J2 | W3 | S | B | evaluate hold applicability at job evaluation (REQ-UW-007) and also… | REQ-CHN-004 |  |
| REQ-UW-255 | J2 | W3 | S | E | hold dry-run listing open jobs and upcoming renewals that… | REQ-POL-014 |  |
| REQ-UW-256 | J2 | W3 | S | B | let authorised underwriters approve hold issues on individual jobs… | — |  |
| REQ-UW-257 | J2 | W3 | S | B | release a hold at its end date or on… | — |  |
| REQ-UW-259 | J2 | W3 | S | B | keep hold history including region changes and show it… | REQ-PLT-002 |  |
| REQ-UW-260 | J6 | W6 | S | B | set a renewal direction per policy term before the… | REQ-POL-009 |  |
| REQ-UW-261 | J6 | W6 | S | E | publish RenewalDirectionSet (R-30) and provide uw.RenewalDirection.get(termRef). | REQ-POL-009 | per contract ruling |
| REQ-UW-262 | J6 | W6 | S | B | evaluate renewal rule sets in bulk on terms entering… | — |  |
| REQ-UW-263 | J6 | W6 | S | B | place REFER directions on the renewal review queue (SCR-UW-17)… | REQ-WRK-001 |  |
| REQ-UW-264 | J6 | W6 | S | B | evaluate RENEWAL checkpoint rules again on the renewal job… | — |  |
| REQ-UW-266 | J6 | W6 | S | B | require UW.NON_RENEWAL authority and a reason code from the… | REQ-PLT-103 |  |
| REQ-UW-267 | J6 | W6 | S | B | check that a NON_RENEW direction is set early enough… | REQ-MKT-009 |  |
| REQ-UW-270 | J6 | W6 | S | B | record a non-disclosure or aggravation finding on an in-force… | REQ-CLM-001 |  |
| REQ-UW-271 | J6 | W6 | M | B | start the statutory clock UW_NONDISCLOSURE_ACTION (or UW_AGGRAVATION_ACTION) from the… | REQ-CMP-003 |  |
| REQ-UW-272 | J6 | W6 | M | B | support the decisions: no action, propose amendment (POL policy-change… | REQ-POL-012 |  |
| REQ-UW-285 | J2 | W3 | S | B | write an audit event for every decision, override, rule-set… | REQ-PLT-002 |  |
| REQ-UW-286 | J2 | W3 | M | B | store a decision snapshot reference (POL transaction or quote… | REQ-RAT-003, REQ-POL-002 |  |
| REQ-UW-287 | J2 | W3 | S | B | stamp the configuration hash on every evaluation and decision. | REQ-MKT-047 |  |
| REQ-UW-288 | J2 | W3 | S | B | obtain the current time only from the PLT time… | REQ-PLT-332 |  |
| REQ-UW-289 | J2 | W3 | S | B | uw.DataSubject.export(partyId) returning issues, decisions, declines, refusal register entries, external… | REQ-CMP-005 |  |
| REQ-UW-290 | J2 | W3 | S | B | erasure and restriction handling that keeps decline and refusal… | REQ-PLT-011 |  |
| REQ-UW-291 | J2 | W3 | S | B | pseudonymise UW data in non-production environments through PLT services. | REQ-PLT-011 |  |
| REQ-UW-292 | J2 | W3 | S | B | map every UW entity to a retention class (section… | REQ-PLT-235 |  |
| REQ-UW-293 | J2 | W3 | S | B | keep external reports only until expiry plus the pack's… | REQ-PLT-011 |  |
| REQ-UW-294 | J2 | W3 | S | B | keep declined-application data under a distinct retention class so… | REQ-WRK-379 |  |
| REQ-UW-295 | J2 | W3 | L | B | import APIs, per ruling R-88, only for configuration and… | REQ-MIG-001, REQ-MIG-141 | per contract ruling |
| REQ-UW-296 | J2 | W3 | S | B | register all UW configuration keys (section 10.2) in the… | REQ-MKT-034 |  |
| REQ-UW-297 | J2 | W3 | S | B | expose operational metrics (evaluation latency, issues raised per checkpoint,… | REQ-PLT-013 |  |
| REQ-UW-298 | J2 | W3 | S | B | serve all UW screens in Greek and English with… | REQ-MKT-005 |  |
| REQ-UW-301 | J2 | W3 | S | B | uw.DisclosureFinding.get and .list (by term or claim) returning the… | — |  |

## A.1.7 POL (293 Musts, 450 points): 293 rows in range

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-POL-001 | J3 | W4 | XL | B | expose a job command API for every job type… | REQ-PLT-005 |  |
| REQ-POL-002 | J3 | W4 | XL | B | pol.Policy.get and pol.Term.get returning the policy as valid at… | — |  |
| REQ-POL-003 | J3 | W4 | XL | B | orchestrate bind gates in one evaluation — open blocking… | REQ-UW-002, REQ-BIL-003, REQ-PTY-006, REQ-PTY-008, REQ-UW-007 |  |
| REQ-POL-004 | J3 | W4 | XL | B | enforce the policy and term status model and the… | — |  |
| REQ-POL-005 | J3 | W4 | XL | B | publish ChargeDeltaEmitted for every bound transaction, one event per… | REQ-BIL-002, REQ-FIN-001 |  |
| REQ-POL-006 | J3 | W4 | XL | B | handle out-of-sequence transactions by reverse-and-reapply with conflict handling as… | — |  |
| REQ-POL-007 | J3 | W4 | XL | B | pol.Snapshot.get(policyId, lossAt, knownAt) returning a stable snapshot reference (segment… | REQ-CLM-002 |  |
| REQ-POL-008 | J3 | W4 | XL | B | enforce effective-date permissions: maximum backdating and future-dating per role,… | REQ-PLT-003 |  |
| REQ-POL-009 | J6 | W6 | XL | B | run the renewal engine: renewal window and job creation… | — |  |
| REQ-POL-010 | J3 | W4 | XL | B | hold risk-unit data — vehicles, drivers (PolicyDriver), buildings, locations,… | REQ-PFC-048, REQ-PTY-002 |  |
| REQ-POL-011 | J3 | W4 | XL | B | manage the quote lifecycle: quick and full quotes, multiple… | — |  |
| REQ-POL-012 | J5 | W6 | XL | B | accept cancellation requests from BIL (CancellationForNonPaymentRequested), from CHN (customer… | REQ-BIL-006, REQ-CHN-007 |  |
| REQ-POL-013 | J13 | W9 | XL | B | pol.Import.policy and pol.Import.term for migration that load policies, terms,… | REQ-MIG-001, REQ-MIG-002 | migration (J13/W9) |
| REQ-POL-014 | J3 | W4 | XL | B | pol.Policy.search by policy number, job number, account number, party… | REQ-PTY-068, REQ-PTY-070, REQ-WRK-006 |  |
| REQ-POL-030 | J3 | W4 | M | B | create a Policy with a UUIDv7 policy_id, a policy… | REQ-PLT-014, REQ-PLT-209, REQ-MKT-096 |  |
| REQ-POL-031 | J3 | W4 | S | B | keep the policy number unchanged across renewal terms, reinstatements… | — |  |
| REQ-POL-032 | J3 | W4 | S | B | number terms with a 1-based integer per policy, never… | — |  |
| REQ-POL-033 | J3 | W4 | L | B | pin on each term at inception, immutably: product code,… | REQ-PFC-002, REQ-PFC-227, REQ-RAT-063, REQ-MKT-047, REQ-MKT-221 |  |
| REQ-POL-034 | J3 | W4 | M | B | propose IFRS 17 tags on each term at bind… | REQ-PFC-005, REQ-PFC-143, REQ-FIN-011 |  |
| REQ-POL-035 | J3 | W4 | S | B | store legal_entity_id and jurisdiction (risk location jurisdiction) and optional… | REQ-MKT-216 |  |
| REQ-POL-036 | J3 | W4 | M | B | assign a static locator (UUIDv7) to every risk-tree element… | — |  |
| REQ-POL-037 | J3 | W4 | S | B | store the account reference on the policy and change… | REQ-PTY-004 |  |
| REQ-POL-038 | J3 | W4 | M | B | store on each term the billing account reference and… | REQ-BIL-003, REQ-PTY-009, REQ-PTY-218 |  |
| REQ-POL-039 | J3 | W4 | M | B | store the written date (date the insurer accepted the… | REQ-PLT-332 |  |
| REQ-POL-040 | J3 | W4 | S | B | support term types from the product (annual, six months,… | REQ-PFC-034 | see XMR-F-420 (6-month GS-02 vs annual-only MVP) |
| REQ-POL-041 | J3 | W4 | S | B | represent effective instants as timestamps with time zone in… | REQ-MKT-005 |  |
| REQ-POL-042 | J3 | W4 | S | B | record on each policy the line of business, product… | REQ-MKT-001 |  |
| REQ-POL-045 | J3 | W4 | S | B | keep the full change history of policy and term… | — |  |
| REQ-POL-046 | J3 | W4 | M | B | store a primary language per policy (from the account's… | REQ-PTY-004, REQ-DOC-001 |  |
| REQ-POL-047 | J3 | W4 | S | B | generate job numbers and transaction numbers through the numbering… | REQ-PLT-014 |  |
| REQ-POL-050 | J3 | W4 | M | B | manage jobs of types Submission, PolicyChange, Cancellation, Reinstatement, Rewrite… | — |  |
| REQ-POL-051 | J3 | W4 | S | E | allow sub-states inside canonical states only where section 7.3… | — |  |
| REQ-POL-052 | J3 | W4 | S | B | hold each job's base transaction (the head of the… | — |  |
| REQ-POL-053 | J3 | W4 | M | B | allow several open jobs on one term concurrently according… | — |  |
| REQ-POL-054 | J3 | W4 | S | B | show, when a job is started, every other open… | — |  |
| REQ-POL-055 | J3 | W4 | S | E | give the editing user a draft edit lease (default… | — |  |
| REQ-POL-056 | J3 | W4 | S | B | serialise binds on a term by an optimistic version… | — |  |
| REQ-POL-057 | J3 | W4 | S | B | when a job binds, flag every other open job | REQ-WRK-007 |  |
| REQ-POL-058 | J3 | W4 | S | B | block bind of a Preempted job until it is… | — |  |
| REQ-POL-059 | J3 | W4 | S | B | flag an open renewal job and an open rewrite… | — |  |
| REQ-POL-060 | J3 | W4 | S | B | allow a policy change and a cancellation to coexist;… | — |  |
| REQ-POL-061 | J3 | W4 | S | B | make a Reinstatement job creatable only when the term… | — |  |
| REQ-POL-062 | J3 | W4 | S | B | expire Draft and Quoted jobs automatically at their validity… | — |  |
| REQ-POL-063 | J3 | W4 | S | E | detect preemption at three points — job open, quote… | — |  |
| REQ-POL-064 | J3 | W4 | M | B | rebase a Preempted job by re-applying its intent to… | — |  |
| REQ-POL-065 | J3 | W4 | S | B | requote a rebased job automatically and clear the Preempted… | — |  |
| REQ-POL-067 | J3 | W4 | M | B | record on each job the channel (shared channel code… | REQ-WRK-008 |  |
| REQ-POL-069 | J3 | W4 | S | B | give every job an optional description and reason code… | — |  |
| REQ-POL-070 | J3 | W4 | S | B | store idempotency keys per command with the request hash… | REQ-PLT-005 |  |
| REQ-POL-071 | J3 | W4 | S | E | execute every dry-run through the same code path as… | — |  |
| REQ-POL-072 | J3 | W4 | S | B | fail commands with typed precondition errors, never partially, and… | — | mentions 'partial' (behavioural, not scope) |
| REQ-POL-073 | J3 | W4 | S | B | allow withdraw of a Draft or Quoted job by… | — |  |
| REQ-POL-074 | J3 | W4 | S | B | keep withdrawn, declined, not-taken and expired jobs with all… | — |  |
| REQ-POL-075 | J3 | W4 | L | B | record every bound change as an append-only PolicyTransaction with… | REQ-PLT-123 |  |
| REQ-POL-076 | J3 | W4 | M | E | store each transaction's intent as typed change instructions (add… | — |  |
| REQ-POL-077 | J3 | W4 | M | B | materialise, for each bound transaction, the segments of the… | REQ-RAT-001 |  |
| REQ-POL-078 | J3 | W4 | S | B | make segments immutable: a superseded segment's record period is… | — |  |
| REQ-POL-079 | J3 | W4 | M | B | enforce with a PostgreSQL 18 temporal key (UNIQUE (term_id,… | — |  |
| REQ-POL-080 | J3 | W4 | S | B | store the segment snapshot as canonical JSON (RFC 8785)… | — |  |
| REQ-POL-082 | J3 | W4 | S | B | keep the current head pointer per term and per… | — |  |
| REQ-POL-083 | J3 | W4 | M | B | preserve the effective-date order of changes in segment content… | — |  |
| REQ-POL-084 | J3 | W4 | S | B | answer as-of queries: segment valid at validAt from the… | — |  |
| REQ-POL-085 | J3 | W4 | S | B | pol.Term.timeline returning all segments of a term (current or… | — |  |
| REQ-POL-086 | J3 | W4 | S | B | never delete or mutate a segment referenced by a… | — |  |
| REQ-POL-087 | J3 | W4 | M | B | carry the correlation id, actor, AI interaction id (where… | REQ-PLT-132, REQ-MKT-051 |  |
| REQ-POL-088 | J3 | W4 | S | B | pin the configuration hash for the duration of one… | REQ-MKT-051 |  |
| REQ-POL-089 | J3 | W4 | M | E | rate only segments whose risk tree or rating-relevant context… | REQ-RAT-002, REQ-RAT-035 |  |
| REQ-POL-090 | J3 | W4 | M | B | store the rating worksheet reference per segment and expose… | REQ-RAT-003, REQ-RAT-229 |  |
| REQ-POL-091 | J3 | W4 | S | E | re-materialise a daily random sample (default 1% of terms,… | REQ-PLT-012 |  |
| REQ-POL-092 | J3 | W4 | S | B | record a rating-result record per segment (rates, rating artefact… | REQ-RAT-001 |  |
| REQ-POL-093 | J3 | W4 | M | B | rate every in-term transaction — policy change, cancellation, reinstatement,… | REQ-RAT-039, REQ-RAT-063, REQ-RAT-163 |  |
| REQ-POL-094 | J3 | W4 | M | B | expose a segment-level read feed (pol.Segment.changes since a cursor)… | REQ-FIN-003, REQ-DAT-001 |  |
| REQ-POL-095 | J3 | W4 | S | B | implement steps 1–11 above for every bound transaction whose… | — |  |
| REQ-POL-096 | J3 | W4 | S | B | detect out-of-sequence position automatically; callers never declare it. | — |  |
| REQ-POL-097 | J3 | W4 | S | B | show, before bind (quote and dry-run), the list of… | — |  |
| REQ-POL-098 | J3 | W4 | S | B | create reversal and reapplication transactions with links reverses, reapplicationOf,… | — |  |
| REQ-POL-099 | J3 | W4 | S | B | allow an issued out-of-sequence aggregate itself to be reversed… | — |  |
| REQ-POL-100 | J3 | W4 | M | B | resolve field-level conflicts using the product version's conflict rule… | REQ-PFC-136 |  |
| REQ-POL-101 | J3 | W4 | S | E | raise a referable conflict, never ignore an instruction, when… | — |  |
| REQ-POL-102 | J3 | W4 | M | B | allow users with permission pol.conflict.resolve to resolve referable conflicts… | REQ-PLT-003 |  |
| REQ-POL-103 | J3 | W4 | S | B | refer an out-of-sequence change to UW when the product… | REQ-UW-004 |  |
| REQ-POL-104 | J3 | W4 | S | B | Guard G1: the system shall reject any policy change,… | — |  |
| REQ-POL-105 | J3 | W4 | M | B | Guard G2: when an out-of-sequence cancellation is bound, the… | — |  |
| REQ-POL-107 | J3 | W4 | M | B | Guard G4: the system shall never mutate claims; when… | REQ-WRK-191, REQ-WRK-001 |  |
| REQ-POL-110 | J3 | W4 | S | B | rebuild conflicts if any input changes between quote and… | — |  |
| REQ-POL-111 | J3 | W4 | S | B | treat a reinstatement without gap as a reversal of… | — |  |
| REQ-POL-112 | J3 | W4 | S | B | publish TransactionReversed and TransactionReapplied once per member transaction with… | REQ-PLT-005 |  |
| REQ-POL-113 | J3 | W4 | S | B | keep the order of reapplication equal to the original… | — |  |
| REQ-POL-114 | J3 | W4 | S | B | support out-of-sequence changes in P1 for all motor element… | — |  |
| REQ-POL-115 | J3 | W4 | M | B | compute the charge of a segment for each element… | REQ-RAT-004, REQ-PFC-135 |  |
| REQ-POL-116 | J3 | W4 | S | B | apply flat charge types once per transaction or per… | REQ-PFC-114 |  |
| REQ-POL-117 | J3 | W4 | S | B | compute written premium of a transaction as the sum… | — |  |
| REQ-POL-118 | J3 | W4 | M | B | pol.Earning.compute(termId, asOf, knownAt) returning earned premium as the sum… | REQ-FIN-003, REQ-RAT-004 |  |
| REQ-POL-119 | J3 | W4 | S | B | emit charge deltas per element locator × charge type… | REQ-PFC-113 |  |
| REQ-POL-120 | J3 | W4 | M | B | carry on each charge delta the coverage code, charge… | REQ-PFC-004, REQ-PFC-005, REQ-PFC-122 |  |
| REQ-POL-121 | J3 | W4 | M | B | support two delta modes, configured per legal entity and… | REQ-BIL-002, REQ-MKT-001 |  |
| REQ-POL-122 | J3 | W4 | S | B | guarantee that the sum of all charge deltas emitted… | REQ-BIL-011 |  |
| REQ-POL-123 | J3 | W4 | M | B | round charge amounts through mkt.Rounding.apply at the level the… | REQ-MKT-195, REQ-MKT-194 |  |
| REQ-POL-124 | J3 | W4 | S | B | compute taxes and levies only through RAT's after-premium computation… | REQ-RAT-009 |  |
| REQ-POL-125 | J3 | W4 | S | B | assign each charge delta a booking date (the bind… | REQ-FIN-001 |  |
| REQ-POL-126 | J3 | W4 | M | B | present a premium breakdown per transaction: annual premium, prorated… | REQ-RAT-008 |  |
| REQ-POL-128 | J3 | W4 | S | B | expose the charge history of a term per element… | REQ-BIL-002 |  |
| REQ-POL-129 | J3 | W4 | S | B | never emit a charge delta from a dry-run, a… | — |  |
| REQ-POL-130 | J3 | W4 | M | B | manage PolicyTerm states Scheduled, InForce, PendingCancellation, Cancelled and Expired… | — |  |
| REQ-POL-131 | J3 | W4 | M | B | move terms Scheduled → InForce and InForce → Expired… | REQ-PLT-172, REQ-PLT-332 |  |
| REQ-POL-132 | J3 | W4 | M | B | derive a policy display status from its terms: the… | — |  |
| REQ-POL-134 | J3 | W4 | S | B | check the transition table inside the same database transaction… | — |  |
| REQ-POL-135 | J3 | W4 | S | B | resolve effective-date limits per transaction type, role, channel, product… | REQ-MKT-001 |  |
| REQ-POL-136 | J3 | W4 | S | B | compute the earliest and latest permitted effective dates for… | — |  |
| REQ-POL-137 | J3 | W4 | S | B | forbid new-business and reinstatement-with-gap effective times earlier than the… | — |  |
| REQ-POL-138 | J3 | W4 | M | B | allow overrides of effective-date limits through plt.Authority.check(POL.EffectiveDateOverride, {product, txnType,… | REQ-PLT-103, REQ-PLT-110 |  |
| REQ-POL-139 | J3 | W4 | S | B | forbid any override for a transaction type where the… | REQ-MKT-001 |  |
| REQ-POL-140 | J3 | W4 | S | B | require future-dated submissions to resolve the product version at… | REQ-PFC-001 |  |
| REQ-POL-141 | J3 | W4 | S | E | restrict backdated changes that reduce cover (remove coverage, raise… | REQ-CLM-002 |  |
| REQ-POL-142 | J3 | W4 | S | B | default the effective date of customer-initiated cancellations to the… | REQ-WRK-008 |  |
| REQ-POL-143 | J3 | W4 | S | B | audit every effective-date default, user edit and override with… | REQ-PLT-002 |  |
| REQ-POL-145 | J3 | W4 | M | B | create a submission from an account (existing or created… | REQ-PTY-004, REQ-PFC-001, REQ-PFC-222 |  |
| REQ-POL-146 | J3 | W4 | S | B | offer only products available for the legal entity, jurisdiction,… | REQ-PFC-011 |  |
| REQ-POL-147 | J3 | W4 | M | B | support a quick quote (minimum rating data set defined… | REQ-PFC-046, REQ-RAT-040 |  |
| REQ-POL-148 | J3 | W4 | S | B | mark quick-quote prices as indicative and never bind a… | REQ-RAT-040 |  |
| REQ-POL-149 | J3 | W4 | S | B | store on each quote version the answered question sets… | REQ-PFC-006 |  |
| REQ-POL-150 | J3 | W4 | S | B | allow several quote versions per job, each with its… | — |  |
| REQ-POL-151 | J3 | W4 | S | E | compare up to four quote versions side by side… | — |  |
| REQ-POL-152 | J3 | W4 | S | B | set a validity end date on each quoted version… | REQ-MKT-001 |  |
| REQ-POL-153 | J3 | W4 | S | B | allow editing a quoted version, returning it to Draft… | — |  |
| REQ-POL-154 | J3 | W4 | S | B | copy a submission (any state) into a new Draft… | REQ-PFC-001 |  |
| REQ-POL-155 | J3 | W4 | M | B | withdraw a submission (customer or user decision), decline it… | REQ-UW-005 |  |
| REQ-POL-156 | J3 | W4 | S | B | require a decline to be executed through UW so… | REQ-UW-005 |  |
| REQ-POL-157 | J3 | W4 | M | B | run UW evaluation at pre-quote on quote and show… | REQ-UW-001, REQ-UW-002 |  |
| REQ-POL-158 | J3 | W4 | M | B | bind a quoted submission or renewal at its quoted… | REQ-PFC-221, REQ-RAT-058, REQ-MKT-047 |  |
| REQ-POL-159 | J3 | W4 | M | B | produce a printable quote document (quote summary, coverages, premium,… | REQ-DOC-001 |  |
| REQ-POL-162 | J3 | W4 | S | E | save drafts automatically on every committed field change (autosave),… | — |  |
| REQ-POL-163 | J3 | W4 | S | E | price the draft on every committed change in the… | REQ-RAT-001 |  |
| REQ-POL-165 | J3 | W4 | S | B | support channels quoting through partner APIs with the same… | REQ-CHN-001 |  |
| REQ-POL-166 | J3 | W4 | S | B | return to channels a customer premium breakdown from RAT… | REQ-RAT-008 |  |
| REQ-POL-168 | J3 | W4 | S | B | record pricing modifications (deviations) requested on a quote through… | REQ-RAT-005 |  |
| REQ-POL-169 | J3 | W4 | S | B | publish QuoteIssued on every quoted version with job, version… | — |  |
| REQ-POL-170 | J3 | W4 | S | B | configure bind gates per product, channel (R-84 channel codes)… | REQ-MKT-001 |  |
| REQ-POL-171 | J3 | W4 | S | B | screen the policyholder, insureds, payer and interested parties at… | REQ-PTY-006 |  |
| REQ-POL-172 | J3 | W4 | L | B | require, per channel and product, evidence that mandatory pre-contractual… | REQ-DOC-005, REQ-DOC-007, REQ-CHN-316 |  |
| REQ-POL-173 | J3 | W4 | S | B | check consents required for bind (for example electronic communication,… | REQ-PTY-005 |  |
| REQ-POL-174 | J3 | W4 | S | B | check producer validity and bind authority (code status, licence,… | REQ-PTY-008 |  |
| REQ-POL-175 | J3 | W4 | M | B | check policy holds (REQ-UW-007), accumulation (REQ-UW-008), cross-border authorisation (mkt.CrossBorder.check)… | REQ-UW-007, REQ-WRK-037 |  |
| REQ-POL-176 | J3 | W4 | M | B | request a provisional proof of cover (cover note) from… | REQ-DOC-007 |  |
| REQ-POL-177 | J3 | W4 | S | B | never block bind on fiscal registration (MARK); issuance documents… | REQ-CMP-001 |  |
| REQ-POL-178 | J3 | W4 | M | B | orchestrate issuance after bind as a workflow (PLT) that… | REQ-PLT-007, REQ-DOC-001, REQ-CMP-002 |  |
| REQ-POL-179 | J3 | W4 | S | E | show issuance progress per step (pending, done, failed) on… | REQ-WRK-001 |  |
| REQ-POL-181 | J3 | W4 | S | B | require explicit confirmation of bind by the acting user… | REQ-PLT-111 |  |
| REQ-POL-182 | J3 | W4 | S | B | create, at bind, the bound transaction, term (Scheduled or… | REQ-PLT-014 |  |
| REQ-POL-183 | J3 | W4 | S | B | show a bind confirmation with links to the job,… | — |  |
| REQ-POL-184 | J3 | W4 | S | B | ask BIL for the down-payment status of the selected… | REQ-BIL-003 |  |
| REQ-POL-185 | J3 | W4 | M | B | obtain a Green Card certificate number per motor vehicle… | REQ-PFC-062, REQ-PLT-014 |  |
| REQ-POL-186 | J3 | W4 | M | B | start statutory clocks at the configured trigger (objection, withdrawal)… | REQ-CMP-003, REQ-DOC-234 |  |
| REQ-POL-187 | J3 | W4 | S | B | pass the term's producer of record to PTY at… | REQ-PTY-215 |  |
| REQ-POL-188 | J3 | W4 | S | B | request the payment-plan instance and billing account for the… | REQ-BIL-001 |  |
| REQ-POL-189 | J3 | W4 | S | B | record the bind gate results, authority checks and confirmations… | REQ-PLT-002 |  |
| REQ-POL-190 | J5 | W6 | S | B | start a policy change with effective date (default now,… | REQ-PFC-168 |  |
| REQ-POL-191 | J5 | W6 | M | B | allow in a policy change every element operation the… | REQ-PFC-066 |  |
| REQ-POL-192 | J5 | W6 | S | B | preview premium before commit: full-term annual premium before and… | — |  |
| REQ-POL-193 | J5 | W6 | S | B | show a transaction diff (element, field, before, after) grouped… | — |  |
| REQ-POL-194 | J5 | W6 | S | B | support self-service changes from CHN within the transaction-permission matrix… | REQ-CHN-002 |  |
| REQ-POL-195 | J5 | W6 | S | B | replace a vehicle as one change: remove old vehicle… | — |  |
| REQ-POL-196 | J5 | W6 | S | B | change the policyholder only through rewrite to a new… | — |  |
| REQ-POL-197 | J5 | W6 | S | B | publish PolicyChanged with transaction, effective date and changed element… | — |  |
| REQ-POL-198 | J5 | W6 | S | B | request change documents (endorsement schedule, updated certificate) from DOC… | REQ-DOC-001 |  |
| REQ-POL-199 | J5 | W6 | S | B | show a confirmation after change bind with links to… | — |  |
| REQ-POL-200 | J5 | W6 | S | B | support mid-term address change of the policy address via… | REQ-PTY-078 |  |
| REQ-POL-201 | J5 | W6 | S | B | let UW referral decisions on a change flow back… | REQ-UW-002 |  |
| REQ-POL-204 | J5 | W6 | S | B | require the customer's explicit acceptance of a premium increase… | REQ-DOC-006 |  |
| REQ-POL-205 | J5 | W6 | M | B | own and publish, as an MKT-held code list (REQ-MKT-001),… | REQ-MKT-001 |  |
| REQ-POL-206 | J5 | W6 | M | B | derive the refund method from the product keyed by… | REQ-PFC-134 |  |
| REQ-POL-207 | J5 | W6 | S | B | compute the refund per element × charge type using… | — |  |
| REQ-POL-208 | J5 | W6 | M | B | compute the default cancellation effective date by source: Policyholder… | REQ-MKT-009 |  |
| REQ-POL-209 | J5 | W6 | S | B | offer "Cancel now" for effective dates at or before… | REQ-PLT-172 |  |
| REQ-POL-210 | J5 | W6 | M | B | cancel for non-payment on CancellationForNonPaymentRequested at the date BIL… | REQ-BIL-006, REQ-PTY-011 |  |
| REQ-POL-211 | J5 | W6 | S | B | rescind a scheduled cancellation before its effective time (CancellationRescinded),… | REQ-BIL-004 |  |
| REQ-POL-212 | J5 | W6 | S | B | send cancellation notices through DOC to the policyholder, payer… | REQ-DOC-005 |  |
| REQ-POL-214 | J5 | W6 | S | B | emit cancellation deltas that reverse future charges from the… | REQ-PFC-113 |  |
| REQ-POL-215 | J5 | W6 | S | B | treat premium-tax charge types as non-refundable on cancellation per… | REQ-PFC-116 |  |
| REQ-POL-216 | J5 | W6 | S | B | publish PolicyCancelled with transaction, source, reason, effective date and… | — |  |
| REQ-POL-217 | J5 | W6 | S | B | support flat cancellation (effective at inception, no cover provided)… | — |  |
| REQ-POL-218 | J5 | W6 | M | B | void a term ab initio on exercise of the… | REQ-CMP-002 |  |
| REQ-POL-219 | J5 | W6 | M | B | start a refund-due clock (POL_REFUND_DUE, accepted R-35) with start… | REQ-CMP-003, REQ-BIL-007 | per contract ruling |
| REQ-POL-221 | J5 | W6 | S | B | cancel from the insurer side after aggravation of risk… | REQ-PLT-003 |  |
| REQ-POL-222 | J5 | W6 | S | B | show the cancellation preview with policy, period, insured, address,… | — |  |
| REQ-POL-223 | J5 | W6 | S | B | cancel all terms after a cancelled term (Scheduled renewals)… | — |  |
| REQ-POL-224 | J5 | W6 | S | B | cancel a single vehicle on a multi-vehicle policy as… | — |  |
| REQ-POL-225 | J5 | W6 | S | B | reinstate a Cancelled term without gap by reversing the… | — |  |
| REQ-POL-226 | J5 | W6 | M | B | reinstate with gap by creating a reinstatement transaction at… | REQ-CMP-002 |  |
| REQ-POL-227 | J5 | W6 | S | B | capture reinstatement reason (Payment received, Cancellation error, Customer request,… | — |  |
| REQ-POL-229 | J5 | W6 | S | B | require authority POL.Reinstatement for reinstatements outside the window, with… | REQ-PLT-003 |  |
| REQ-POL-230 | J5 | W6 | S | B | rewrite a term as full term (new term from… | REQ-PFC-001 |  |
| REQ-POL-231 | J5 | W6 | S | B | bind a rewrite atomically: cancel the old term at… | — |  |
| REQ-POL-233 | J5 | W6 | M | B | execute a policy move between accounts of the same… | REQ-PTY-124 |  |
| REQ-POL-234 | J5 | W6 | S | B | let the user select whether a rewrite assigns a… | — |  |
| REQ-POL-235 | J5 | W6 | S | B | rate rewritten terms on the product version resolved for… | REQ-PFC-008 |  |
| REQ-POL-236 | J5 | W6 | M | B | suspend a vehicle's cover from a date (reason: plates… | — |  |
| REQ-POL-237 | J5 | W6 | S | B | require the evidence document class configured for the suspension… | REQ-WRK-005 |  |
| REQ-POL-239 | J5 | W6 | S | B | show suspended vehicles with a display flag on the… | — |  |
| REQ-POL-244 | J5 | W6 | S | B | publish PolicyReinstated (with gap data) and PolicyRewritten (old and… | — |  |
| REQ-POL-245 | J6 | W6 | M | B | create renewal jobs for terms whose expiry falls within… | — |  |
| REQ-POL-246 | J6 | W6 | M | B | copy the expiring term's risk tree as valid at… | REQ-PFC-001 |  |
| REQ-POL-247 | J6 | W6 | M | B | run renewal creation, conversion, rating and offer as a… | REQ-PLT-172, REQ-PLT-173 |  |
| REQ-POL-248 | J6 | W6 | M | B | convert the expiring risk tree to the renewal version… | REQ-PFC-008 |  |
| REQ-POL-249 | J6 | W6 | L | B | evaluate UW rules at the renewal checkpoint and rate… | REQ-UW-001, REQ-RAT-002, REQ-RAT-041, REQ-RAT-138 |  |
| REQ-POL-250 | J6 | W6 | M | B | offer the renewal (job Quoted.Offered) by requesting the renewal… | REQ-DOC-001, REQ-CMP-003 |  |
| REQ-POL-251 | J6 | W6 | M | B | support non-renewal by the insurer with reason codes and… | REQ-PLT-003, REQ-CMP-003 |  |
| REQ-POL-252 | J6 | W6 | S | B | record customer non-renewal (customer declines the offer) as NotTaken… | — |  |
| REQ-POL-253 | J6 | W6 | M | B | support acceptance modes per market and product (pol.renewal.acceptance_mode): BY_PAYMENT… | REQ-BIL-003 |  |
| REQ-POL-254 | J6 | W6 | S | B | lapse a renewal not accepted or not paid by… | REQ-CMP-002 |  |
| REQ-POL-255 | J6 | W6 | S | B | allow staff to edit an offered renewal (requote) and… | — |  |
| REQ-POL-256 | J6 | W6 | S | B | show renewal workflow status (created, converting, rating, referred, offered,… | — |  |
| REQ-POL-257 | J6 | W6 | M | B | record explicit renewal acceptance with channel, authenticated identity or… | REQ-DOC-006, REQ-DOC-270, REQ-CHN-004 |  |
| REQ-POL-258 | J6 | W6 | S | B | let staff renew manually ahead of the batch ("Renew… | — |  |
| REQ-POL-259 | J6 | W6 | M | B | resolve the producer for the renewal term from PTY,… | REQ-PTY-009, REQ-PTY-222 |  |
| REQ-POL-261 | J6 | W6 | S | E | re-offer automatically when a carried-forward change or rebase changes… | — |  |
| REQ-POL-262 | J6 | W6 | M | B | carry forward every change bound on the expiring term… | — |  |
| REQ-POL-263 | J6 | W6 | S | B | publish RenewalCreated, RenewalOffered, RenewalBound, PolicyNonRenewed and PolicyLapsed. | — |  |
| REQ-POL-264 | J6 | W6 | S | B | report renewal run metrics (created, converted, referred, offered, accepted,… | — |  |
| REQ-POL-265 | J6 | W6 | S | B | suspend renewal creation for a product or region when… | REQ-UW-007 |  |
| REQ-POL-270 | J5 | W6 | S | B | allow a policy change effective in an earlier term… | — |  |
| REQ-POL-271 | J5 | W6 | M | B | after binding a prior-term change in term *n*, create | — |  |
| REQ-POL-272 | J5 | W6 | S | B | bind the prior-term change and all carry-forwards as one… | — |  |
| REQ-POL-273 | J5 | W6 | S | B | apply a prior-term change to an in-flight renewal job… | — |  |
| REQ-POL-274 | J5 | W6 | S | B | emit charge deltas per term with the term reference… | REQ-BIL-002 |  |
| REQ-POL-275 | J5 | W6 | S | B | show the ripple preview (terms affected, deltas per term,… | — |  |
| REQ-POL-277 | J3 | W4 | M | B | hold vehicles with the fields of the PFC vehicle… | REQ-PFC-050, REQ-PFC-062 |  |
| REQ-POL-279 | J3 | W4 | S | B | normalise plates (Greek and Latin look-alike letters, spaces, hyphens)… | REQ-MKT-002 |  |
| REQ-POL-280 | J3 | W4 | M | B | hold drivers on the policy (PolicyDriver) referencing a PTY… | REQ-PTY-002, REQ-PTY-093 |  |
| REQ-POL-281 | J3 | W4 | M | B | create or select the driver's party through PTY (search… | REQ-PTY-001 |  |
| REQ-POL-282 | J3 | W4 | S | B | assign drivers to vehicles with usage percentages per vehicle… | — |  |
| REQ-POL-283 | J3 | W4 | S | B | support excluded drivers (named persons not covered) with the… | REQ-DOC-003 |  |
| REQ-POL-285 | J3 | W4 | M | B | hold garaging and risk locations referencing PTY account locations… | REQ-PTY-085, REQ-MKT-305 |  |
| REQ-POL-288 | J3 | W4 | M | B | hold policy parties with roles PrimaryNamedInsured, SecondaryNamedInsured, AdditionalNamedInsured (with… | REQ-PTY-002, REQ-PTY-094 |  |
| REQ-POL-289 | J3 | W4 | S | B | hold interested-party details per element: interest type, contract or… | REQ-CLM-002 |  |
| REQ-POL-290 | J3 | W4 | S | B | support an assignee role (bank holding an assignment of… | — |  |
| REQ-POL-292 | J3 | W4 | M | B | select coverages, coverage terms, options, exclusions and conditions from… | REQ-PFC-003, REQ-PFC-069, REQ-PFC-088 |  |
| REQ-POL-293 | J3 | W4 | M | B | let users search the catalogue of exclusions and conditions… | REQ-PFC-083, REQ-PFC-084 |  |
| REQ-POL-294 | J3 | W4 | S | B | support offering selection with defaults and restrictions from the… | REQ-PFC-095 |  |
| REQ-POL-295 | J3 | W4 | S | B | validate the draft risk tree through pfc.PolicyDraft.validate at each… | REQ-PFC-224 |  |
| REQ-POL-296 | J3 | W4 | M | B | record per term the producer of record and producer… | REQ-PTY-009, REQ-PTY-218 |  |
| REQ-POL-297 | J3 | W4 | S | B | re-point party references when PTY publishes PartiesMerged (and restore… | REQ-PTY-007 |  |
| REQ-POL-299 | J3 | W4 | S | B | support product extension fields defined by PFC without code… | REQ-PFC-050 |  |
| REQ-POL-300 | J3 | W4 | S | B | store official identifiers only in PTY; the policy shows… | REQ-PTY-060 |  |
| REQ-POL-301 | J3 | W4 | S | B | evaluate driver and vehicle validation rules defined by the… | REQ-PFC-055 |  |
| REQ-POL-302 | J3 | W4 | S | B | allow driver and vehicle removal with effective date and… | — |  |
| REQ-POL-303 | J3 | W4 | S | B | keep the vehicle element's history across replacement (old element… | REQ-CLM-002 |  |
| REQ-POL-305 | J5 | W6 | M | B | start the objection clock (POL_OBJECTION) via REQ-CMP-003 when the… | REQ-CMP-003 |  |
| REQ-POL-306 | J5 | W6 | M | B | start the 14-day objection clock variant (POL_OBJECTION_INFO, accepted R-35)… | REQ-CMP-003 | per contract ruling |
| REQ-POL-307 | J5 | W6 | S | B | accept an objection as a typed command (pol.Withdrawal.submit with… | — |  |
| REQ-POL-308 | J5 | W6 | L | B | start the withdrawal clock — POL_WITHDRAWAL_DISTANCE for distance contracts… | REQ-CMP-003, REQ-MKT-287, REQ-CHN-161, REQ-CHN-168 |  |
| REQ-POL-309 | J5 | W6 | S | B | pause the withdrawal clock while an objection clock runs… | REQ-CMP-003 |  |
| REQ-POL-310 | J5 | W6 | M | B | treat objection and withdrawal clocks as WAITING_PERIOD clocks (contract… | REQ-CMP-003 | per contract ruling |
| REQ-POL-311 | J5 | W6 | M | B | cancel for non-payment only on BIL's CancellationForNonPaymentRequested, which BIL… | REQ-BIL-006 |  |
| REQ-POL-312 | J5 | W6 | M | B | record an aggravation-of-risk notification (date notified, facts, source) on… | REQ-UW-271, REQ-UW-272, REQ-UW-243 |  |
| REQ-POL-314 | J5 | W6 | M | B | accept withdrawal requests from CHN's online withdrawal function as… | REQ-CHN-007, REQ-DOC-005 |  |
| REQ-POL-315 | J5 | W6 | M | B | publish, for motor products, every fact that changes whether… | REQ-CMP-002 |  |
| REQ-POL-316 | J5 | W6 | S | B | show on the policy file the bureau reporting status… | REQ-CMP-002 |  |
| REQ-POL-317 | J5 | W6 | M | B | expose the statutory clock panel for a term: clock… | REQ-CMP-003 |  |
| REQ-POL-318 | J5 | W6 | S | B | start the renewal notice clock (POL_RENEWAL_NOTICE) and the non-renewal… | REQ-CMP-003 |  |
| REQ-POL-319 | J5 | W6 | S | B | run all clock values from StatutoryClockSet via CMP; POL… | REQ-MKT-009 |  |
| REQ-POL-320 | J3 | W4 | M | B | policy search with criteria: policyholder name (person or company,… | REQ-PTY-068 |  |
| REQ-POL-321 | J3 | W4 | S | B | show search results with policy number, policyholder, product, status,… | — |  |
| REQ-POL-322 | J3 | W4 | S | B | publish policy and job search documents to WRK global… | REQ-WRK-325 |  |
| REQ-POL-323 | J3 | W4 | S | B | restrict search and policy access by ABAC: legal entity,… | REQ-PLT-001 |  |
| REQ-POL-324 | J3 | W4 | M | B | policy summary with details, term financials (total premium, taxes… | REQ-PTY-108 |  |
| REQ-POL-326 | J3 | W4 | M | B | policy file sections policy info, parties, vehicles, drivers, coverages,… | REQ-DOC-008, REQ-WRK-004 |  |
| REQ-POL-327 | J3 | W4 | S | B | list transactions of a policy with transaction number, type,… | — |  |
| REQ-POL-328 | J3 | W4 | S | B | compare any two transactions or as-of states of a… | — |  |
| REQ-POL-329 | J3 | W4 | S | E | show reversed and reapplied transactions linked in the history… | — |  |
| REQ-POL-330 | J3 | W4 | S | B | list the policy jobs of an account with filters… | REQ-PTY-262 |  |
| REQ-POL-331 | J3 | W4 | S | B | pol.Job.list by account, user participation and state for WRK… | REQ-WRK-311 |  |
| REQ-POL-332 | J3 | W4 | M | B | offer the policy actions menu: change, cancel, renew now,… | — |  |
| REQ-POL-333 | J3 | W4 | S | B | premium view per vehicle and coverage with amount, period,… | REQ-RAT-003 |  |
| REQ-POL-334 | J3 | W4 | S | B | present the recent-items list and quick jumps for policies… | REQ-WRK-330 |  |
| REQ-POL-335 | J3 | W4 | S | B | show contacts on the policy with roles, phone and… | REQ-PTY-001 |  |
| REQ-POL-336 | J3 | W4 | S | B | expose the transaction history and current status to CHN… | REQ-CHN-004 |  |
| REQ-POL-337 | J3 | W4 | M | B | embed WRK notes and activities on the policy, job… | REQ-WRK-004, REQ-WRK-231 |  |
| REQ-POL-338 | J3 | W4 | S | B | DSAR export of POL-held personal data per party (roles… | REQ-CMP-005 |  |
| REQ-POL-339 | J3 | W4 | M | B | restrict processing of a party's POL data on a… | REQ-PLT-011, REQ-CMP-005 |  |
| REQ-POL-340 | J3 | W4 | M | B | import a mid-term policy as an opening transaction at… | REQ-MIG-001, REQ-BIL-012 |  |
| REQ-POL-341 | J3 | W4 | S | B | support renewal-based conversion by creating the first core term… | REQ-MIG-003 |  |
| REQ-POL-342 | J3 | W4 | S | B | store legacy identifiers and links via the MIG cross-reference… | REQ-MIG-002 |  |
| REQ-POL-343 | J3 | W4 | S | B | validate imported data with the same product validation and… | REQ-PFC-224 |  |
| REQ-POL-344 | J3 | W4 | S | B | data-correction path for policy data only as a transaction… | REQ-PLT-004 |  |
| REQ-POL-345 | J3 | W4 | S | E | expose operational queues: bind gate failures, issuance failures, conflicts… | — |  |
| REQ-POL-346 | J3 | W4 | S | B | emit OpenTelemetry metrics: binds per minute, quote latency, out-of-sequence… | REQ-PLT-013 |  |
| REQ-POL-347 | J3 | W4 | S | B | respond to ConfigChanged by refreshing cached POL configuration (gates,… | REQ-PLT-008 |  |
| REQ-POL-348 | J3 | W4 | S | B | respond to AiToggleChanged and AiKillSwitchActivated within 60 s by… | REQ-PLT-010 |  |
| REQ-POL-349 | J3 | W4 | S | B | sandbox and test-time facility using the PLT time service… | REQ-PLT-332 |  |
| REQ-POL-352 | J3 | W4 | M | B | declare the ICT third-party services POL depends on (vehicle… | REQ-PLT-012, REQ-PLT-164 |  |
| REQ-POL-353 | J3 | W4 | S | B | write an AiInteractionRecord reference on every POL record touched… | REQ-PLT-010 |  |
| REQ-POL-354 | J3 | W4 | L | E | on PackRolledBack, identify every bound transaction and open quote | REQ-MKT-003, REQ-PLT-181, REQ-WRK-001, REQ-PLT-002 |  |

## A.1.8 BIL (294 Musts, 442 points): 228 rows in range (PARTIAL: stopped at line 6700 / REQ-BIL-279)

| ID | J | W | Sz | Tag | Title | Must deps (other modules) | Notes |
|---|---|---|---|---|---|---|---|
| REQ-BIL-001 | J4 | W5 | XL | B | expose the billing account API bil.BillingAccount with operations create,… | REQ-PTY-001, REQ-PTY-004 |  |
| REQ-BIL-002 | J4 | W5 | XL | B | consume ChargeDeltaEmitted from POL idempotently on charge id, validate… | REQ-POL-005, REQ-POL-122, REQ-PFC-004 |  |
| REQ-BIL-003 | J4 | W5 | XL | B | own the payment-plan catalogue and plan instances per policy… | REQ-POL-003, REQ-POL-184, REQ-PFC-133 |  |
| REQ-BIL-004 | J4 | W5 | XL | B | accept payments from every collection channel (bank statement lines,… | REQ-MKT-098, REQ-MKT-100, REQ-PLT-161 |  |
| REQ-BIL-005 | J4 | W5 | XL | B | process payment reversals — SEPA rejects, returns, refunds and… | — |  |
| REQ-BIL-006 | J4 | W5 | XL | B | run delinquency processes per delinquency plan, send the statutory… | REQ-CMP-003, REQ-MKT-009, REQ-DOC-005, REQ-POL-012, REQ-POL-311 |  |
| REQ-BIL-007 | J5 | W6 | XL | B | create refunds from credit balances, approve them under the… | REQ-PLT-003, REQ-PLT-004, REQ-PTY-006, REQ-MKT-102 |  |
| REQ-BIL-008 | J1 | W6 | XL | B | calculate commission for every commissionable charge delta and, where… | REQ-PTY-010, REQ-PTY-236, REQ-PFC-122 |  |
| REQ-BIL-009 | J4 | W5 | XL | B | disbursement service shared with CLM — bil.Disbursement.request, get, stop,… | REQ-CLM-004, REQ-PTY-006, REQ-MKT-100, REQ-MKT-102 |  |
| REQ-BIL-010 | J1 | W6 | XL | B | support agency bill: recognise premium collected by intermediaries with… | REQ-PTY-008, REQ-PTY-205, REQ-PTY-240 |  |
| REQ-BIL-011 | J4 | W5 | XL | B | publish billing business events to FIN (written, billed, collected,… | REQ-FIN-001, REQ-FIN-007 |  |
| REQ-BIL-012 | J13 | W9 | XL | B | import APIs for migration — bil.Import.account, bil.Import.openItems, bil.Import.mandate, bil.Import.paymentMethodToken,… | REQ-MIG-001, REQ-MIG-002, REQ-MIG-070, REQ-MIG-072, REQ-FIN-010 … | migration (J13/W9); deps list truncated in source |
| REQ-BIL-030 | J4 | W5 | M | B | create a billing account with a UUIDv7 id, a… | REQ-PLT-014, REQ-PLT-209 |  |
| REQ-BIL-031 | J4 | W5 | S | B | allow one billing account to hold any number of… | REQ-POL-188 |  |
| REQ-BIL-032 | J4 | W5 | M | B | require the payer to be a PTY party holding… | REQ-PTY-002, REQ-POL-171 |  |
| REQ-BIL-033 | J4 | W5 | S | E | at bind, find the payer's existing active account in | REQ-POL-188 |  |
| REQ-BIL-034 | J4 | W5 | S | B | hold account preferences: preferred payment method, preferred due day… | REQ-PTY-005 |  |
| REQ-BIL-035 | J4 | W5 | S | B | manage account states Active, Suspended (no new terms; collections… | — |  |
| REQ-BIL-036 | J4 | W5 | M | B | change the payer of an account (ownership change) with… | REQ-PLT-004, REQ-PTY-006 |  |
| REQ-BIL-038 | J4 | W5 | M | B | react to AccountMerged and PartiesMerged by re-pointing payer and… | REQ-PTY-007, REQ-PTY-123 |  |
| REQ-BIL-039 | J4 | W5 | S | B | maintain one invoice stream per term by default and… | — |  |
| REQ-BIL-040 | J4 | W5 | M | B | bil.BillingAccount.search by account number, payer (via PTY party ids),… | REQ-PLT-079, REQ-PLT-287, REQ-WRK-006 |  |
| REQ-BIL-041 | J4 | W5 | S | B | show on the account a statement view (IB-13) with… | — |  |
| REQ-BIL-042 | J4 | W5 | S | B | account history: invoices, receipts, allocations, reversals, refunds, write-offs, transfers,… | REQ-PLT-128 |  |
| REQ-BIL-043 | J4 | W5 | M | B | attach notes and activities to billing accounts, invoices, receipts,… | REQ-WRK-001, REQ-WRK-004 |  |
| REQ-BIL-044 | J4 | W5 | M | B | honour vulnerable-customer handling rules from pty.Vulnerability.query on billing accounts… | REQ-PTY-011, REQ-WRK-069 |  |
| REQ-BIL-045 | J4 | W5 | M | B | maintain a payment-plan catalogue as versioned, effective-dated configuration per… | REQ-PFC-133, REQ-MKT-001 |  |
| REQ-BIL-046 | J4 | W5 | S | E | require maker-checker approval to activate a payment-plan version. | REQ-PLT-004 |  |
| REQ-BIL-047 | J4 | W5 | M | B | evaluate plan eligibility by product version (plans offered by… | REQ-PFC-133, REQ-PFC-011, REQ-MKT-001 |  |
| REQ-BIL-048 | J4 | W5 | M | B | compute instalment amounts from the plan, the charges and… | REQ-MKT-194, REQ-MKT-195 |  |
| REQ-BIL-049 | J4 | W5 | S | B | enforce the minimum instalment amount by reducing the number… | — |  |
| REQ-BIL-050 | J4 | W5 | S | B | add instalment fees as a fee charge from the… | REQ-PFC-113 |  |
| REQ-BIL-051 | J4 | W5 | M | B | pass the selected plan code to POL for rating… | REQ-RAT-121, REQ-POL-188 |  |
| REQ-BIL-052 | J4 | W5 | M | B | create a plan instance per policy term at bind,… | REQ-POL-038, REQ-POL-188 |  |
| REQ-BIL-053 | J4 | W5 | S | B | carry the plan to the renewal term by default… | REQ-POL-009 |  |
| REQ-BIL-054 | J4 | W5 | S | B | compute the down payment required for a quote as… | — |  |
| REQ-BIL-055 | J4 | W5 | S | B | accept a down payment before bind against a quote… | — |  |
| REQ-BIL-056 | J4 | W5 | S | B | return a deposit that was paid for a job… | — |  |
| REQ-BIL-057 | J4 | W5 | M | B | evaluate "bind before payment" only where the plan allows… | REQ-POL-184, REQ-PTY-205 |  |
| REQ-BIL-058 | J4 | W5 | M | B | billing preview (bil.BillingPreview.compute) for a quote, a change, a… | REQ-POL-001 |  |
| REQ-BIL-059 | J4 | W5 | S | B | change the plan of a term mid-term (bil.PaymentPlan.change) by… | — |  |
| REQ-BIL-060 | J4 | W5 | S | B | change the due day of an account or term,… | — |  |
| REQ-BIL-061 | J4 | W5 | M | B | apply the effect of a plan change on the… | REQ-POL-001, REQ-RAT-121 |  |
| REQ-BIL-063 | J4 | W5 | S | B | expose plans, preview and down-payment status to CHN for… | REQ-CHN-002 |  |
| REQ-BIL-065 | J4 | W5 | M | B | validate every consumed charge delta: term known and linked… | REQ-PFC-004, REQ-WRK-001 |  |
| REQ-BIL-066 | J4 | W5 | M | B | post a written entry for each accepted delta (debit… | REQ-PFC-119 |  |
| REQ-BIL-067 | J4 | W5 | S | B | hold, per invoice item, the POL charge id, charge… | REQ-POL-005 |  |
| REQ-BIL-068 | J4 | W5 | M | B | schedule each delta by the scheduling rule configured per… | REQ-PFC-113, REQ-PFC-119, REQ-PFC-127 |  |
| REQ-BIL-070 | J4 | W5 | S | B | bill flat fees (policy fee, instalment fee) on the… | REQ-PFC-114 |  |
| REQ-BIL-071 | J4 | W5 | S | B | create invoice items in state Planned with planned invoice… | — |  |
| REQ-BIL-072 | J4 | W5 | S | B | re-spread a mid-term positive or negative delta over the… | — |  |
| REQ-BIL-073 | J4 | W5 | S | B | apply negative deltas (credits) first against unbilled items of… | — |  |
| REQ-BIL-074 | J4 | W5 | S | B | bill cancellation credits immediately as a credit item (credit… | REQ-POL-214 |  |
| REQ-BIL-075 | J4 | W5 | S | B | net GROSS-mode delta sets (REVERSAL and REAPPLY sharing one… | REQ-POL-121 |  |
| REQ-BIL-076 | J4 | W5 | M | B | process deltas that affect already billed or paid periods… | REQ-POL-006, REQ-POL-274 |  |
| REQ-BIL-077 | J4 | W5 | S | B | apply delta correlation keys so that a TransactionReversed without… | REQ-POL-006 |  |
| REQ-BIL-078 | J4 | W5 | S | B | bill deltas of a prior term on that term's… | REQ-POL-274 |  |
| REQ-BIL-079 | J4 | W5 | M | B | reject (quarantine) any negative IPT delta whose source transaction… | REQ-PFC-116, REQ-POL-215 |  |
| REQ-BIL-080 | J4 | W5 | S | B | accept reinstatement deltas after a non-payment cancellation and schedule… | — |  |
| REQ-BIL-081 | J4 | W5 | S | B | generate BIL-originated charges (instalment fee, dishonour fee, late fee… | REQ-PFC-113 |  |
| REQ-BIL-082 | J4 | W5 | S | B | reconcile BIL's written totals per term and charge type… | REQ-POL-122 |  |
| REQ-BIL-085 | J4 | W5 | M | B | run the invoice run daily (SYS-02, partitioned with checkpoints)… | REQ-PLT-172, REQ-PLT-173 |  |
| REQ-BIL-086 | J4 | W5 | M | B | number invoices through plt.Number.next with a gapless scheme constrained… | REQ-PLT-014, REQ-PLT-211, REQ-CMP-001 |  |
| REQ-BIL-087 | J4 | W5 | S | B | manage the Invoice lifecycle Planned → Billed → Due… | — |  |
| REQ-BIL-088 | J4 | W5 | M | B | produce invoice content: payer, policyholder reference, policy and term,… | REQ-DOC-001, REQ-MKT-098 |  |
| REQ-BIL-089 | J4 | W5 | L | B | request rendering and delivery of invoices, credit notes and… | REQ-DOC-001, REQ-DOC-005 |  |
| REQ-BIL-091 | J4 | W5 | S | B | issue credit notes for billed credit items and for… | — |  |
| REQ-BIL-092 | J4 | W5 | S | B | produce periodic account statements (monthly for accounts with activity,… | REQ-DOC-001 |  |
| REQ-BIL-095 | J4 | W5 | S | B | regenerate (re-render) an invoice document from its stored payload… | REQ-DOC-004 |  |
| REQ-BIL-096 | J4 | W5 | M | B | trigger a fiscal document through REQ-CMP-001 at the configured… | REQ-CMP-001, REQ-PFC-121 |  |
| REQ-BIL-097 | J4 | W5 | S | B | include BIL-originated fee charges in fiscal triggers and send… | REQ-CMP-001 |  |
| REQ-BIL-098 | J4 | W5 | M | B | consume FiscalDocRegistered to store the MARK on the invoice… | REQ-CMP-001, REQ-WRK-043 |  |
| REQ-BIL-099 | J4 | W5 | S | B | withhold delivery of a customer invoice until the MARK… | REQ-CMP-001 |  |
| REQ-BIL-100 | J4 | W5 | S | B | reconcile daily the fiscal documents registered with invoiced charges… | REQ-CMP-001 |  |
| REQ-BIL-101 | J4 | W5 | M | B | send commission self-billing fiscal triggers on behalf of intermediaries… | REQ-PTY-240, REQ-CMP-001 |  |
| REQ-BIL-102 | J4 | W5 | S | B | invoice and notice viewer (SCR-BIL-02) with items, allocations, fiscal… | REQ-DOC-008 |  |
| REQ-BIL-103 | J4 | W5 | M | B | hold payment instruments per party (payer instruments for collection… | REQ-PLT-287, REQ-PLT-127 |  |
| REQ-BIL-104 | J4 | W5 | M | B | validate IBANs by country format and check digits, and… | REQ-MKT-180, REQ-PTY-012 |  |
| REQ-BIL-105 | J4 | W5 | S | B | set an account's payment method per term or account… | REQ-CHN-004 |  |
| REQ-BIL-106 | J4 | W5 | M | B | create SEPA Core mandates with a unique mandate reference… | REQ-DOC-004, REQ-DOC-006, REQ-CHN-004 |  |
| REQ-BIL-107 | J4 | W5 | S | B | amend mandates (debtor IBAN or bank change, debtor name,… | REQ-MKT-100 |  |
| REQ-BIL-108 | J4 | W5 | S | B | cancel mandates on debtor request, on payer change, on… | — |  |
| REQ-BIL-109 | J4 | W5 | M | B | send a pre-notification of every SDD collection's amount and… | REQ-DOC-005 |  |
| REQ-BIL-110 | J4 | W5 | M | B | run SDD collection runs per creditor bank account: select… | REQ-MKT-100, REQ-PLT-006, REQ-PLT-155 |  |
| REQ-BIL-111 | J4 | W5 | S | B | post no cash for a submitted collection instruction until… | — |  |
| REQ-BIL-113 | J4 | W5 | S | B | track the debtor's SEPA refund window per collection (8… | — |  |
| REQ-BIL-114 | J4 | W5 | S | B | generate payment references through PaymentReferenceGenerator per billing account and… | REQ-MKT-098 |  |
| REQ-BIL-116 | J4 | W5 | S | B | take card payments only through the acquirer's hosted fields… | REQ-CHN-004 |  |
| REQ-BIL-117 | J4 | W5 | S | B | perform customer-initiated card payments and card set-up with strong… | — |  |
| REQ-BIL-119 | J4 | W5 | S | B | reconcile card settlements (acquirer settlement report, net of fees)… | — |  |
| REQ-BIL-120 | J4 | W5 | S | B | accept bank transfers without reference and route them to… | — |  |
| REQ-BIL-124 | J4 | W5 | S | B | keep a payment-method history per account and term with… | REQ-PLT-002 |  |
| REQ-BIL-125 | J4 | W5 | M | B | ingest bank statements (camt.053 end of day, camt.054 intraday)… | REQ-PLT-161, REQ-MKT-100 |  |
| REQ-BIL-126 | J4 | W5 | S | B | create a Receipt (state Received) for every incoming credit… | REQ-PLT-014 |  |
| REQ-BIL-127 | J4 | W5 | M | B | match receipts deterministically in this order: collection instruction end-to-end… | — |  |
| REQ-BIL-128 | J4 | W5 | M | E | run fuzzy matching for receipts not matched deterministically (counterparty… | REQ-PTY-001 |  |
| REQ-BIL-129 | J4 | W5 | M | B | allocate a matched receipt by the allocation waterfall configured… | — |  |
| REQ-BIL-130 | J4 | W5 | S | B | never allocate more than the receipt amount, never more… | — |  |
| REQ-BIL-131 | J4 | W5 | M | E | post receipts in two steps where settlement is asynchronous:… | — |  |
| REQ-BIL-132 | J4 | W5 | S | B | handle partial payments by allocating to items per the… | — | mentions 'partial' (behavioural, not scope) |
| REQ-BIL-133 | J4 | W5 | M | B | handle overpayments by allocating to the next billed or… | — |  |
| REQ-BIL-134 | J4 | W5 | S | B | absorb small differences within tolerance at allocation (under- or… | — |  |
| REQ-BIL-135 | J4 | W5 | S | B | place unmatched or unallocatable receipts in suspense with a… | REQ-WRK-001 |  |
| REQ-BIL-136 | J4 | W5 | S | B | let a user allocate a suspense item to one… | — |  |
| REQ-BIL-137 | J4 | W5 | S | B | refund a suspense item to the sender's IBAN taken… | — |  |
| REQ-BIL-138 | J4 | W5 | M | B | age suspense items and raise WRK activities (BIL-SUSPENSE) with… | REQ-WRK-001, REQ-WRK-009 |  |
| REQ-BIL-139 | J4 | W5 | S | E | present ranked match suggestions for each suspense item (rule-based… | REQ-PLT-010 |  |
| REQ-BIL-141 | J4 | W5 | S | B | unallocate (reverse an allocation) with reason and re-allocate in… | REQ-PLT-004 |  |
| REQ-BIL-142 | J4 | W5 | S | B | publish PaymentReceived when a receipt is created against an… | REQ-PLT-005 |  |
| REQ-BIL-144 | J4 | W5 | S | B | allow a CSR to take a payment by phone… | — |  |
| REQ-BIL-145 | J4 | W5 | S | B | show receipts with their matching rule, allocations, reversals and… | — |  |
| REQ-BIL-146 | J4 | W5 | M | B | apply a configurable cut-off time per channel so receipts… | REQ-PLT-197, REQ-PLT-332 |  |
| REQ-BIL-147 | J4 | W5 | S | B | ingest SEPA R-transactions (pain.002 rejects, camt.054 returns, refunds and… | REQ-MKT-100 |  |
| REQ-BIL-148 | J4 | W5 | S | B | map each R-transaction reason code through the reason-code table… | — |  |
| REQ-BIL-149 | J4 | W5 | M | B | process card chargebacks from acquirer notifications: reverse the receipt… | REQ-WRK-001, REQ-DOC-004 |  |
| REQ-BIL-150 | J4 | W5 | S | B | post reversals as linked entries that exactly negate the… | — |  |
| REQ-BIL-151 | J4 | W5 | S | B | reopen reversed invoice items to Due or Overdue according… | — |  |
| REQ-BIL-153 | J4 | W5 | S | B | process debtor refund claims within 8 weeks (MD06) as… | REQ-WRK-001 |  |
| REQ-BIL-154 | J4 | W5 | S | B | process failed or recalled outgoing transfers and bounced incoming… | — |  |
| REQ-BIL-155 | J4 | W5 | M | B | apply repeated-failure rules: after N failed collections within M… | — |  |
| REQ-BIL-156 | J4 | W5 | S | B | handle reversals that arrive after the term is cancelled… | — |  |
| REQ-BIL-157 | J4 | W5 | S | B | reverse commission calculated on a reversed collection under COLLECTED… | — |  |
| REQ-BIL-158 | J4 | W5 | S | B | reverse a DownPaymentCleared fact when the down-payment receipt is… | REQ-POL-003 |  |
| REQ-BIL-159 | J4 | W5 | S | B | payment exceptions and returns queue (SCR-BIL-06) listing only R-transactions,… | — |  |
| REQ-BIL-160 | J4 | W5 | S | B | publish PaymentReversed with receipt, reason category, reason code, amount,… | REQ-PLT-005 |  |
| REQ-BIL-161 | J4 | W5 | S | B | start a delinquency process when an invoice item remains… | REQ-WRK-187 |  |
| REQ-BIL-162 | J4 | W5 | M | B | maintain delinquency plans as versioned configuration per legal entity,… | REQ-PLT-004, REQ-PLT-167 |  |
| REQ-BIL-163 | J4 | W5 | M | B | run each delinquency process as a workflow with durable… | REQ-PLT-007, REQ-PLT-168, REQ-PLT-169 |  |
| REQ-BIL-164 | J4 | W5 | S | B | re-check the open overdue amount before every step and… | — |  |
| REQ-BIL-165 | J4 | W5 | M | B | support step action types: customer reminder (DOC, preferred channel),… | REQ-DOC-001, REQ-WRK-001 |  |
| REQ-BIL-166 | J4 | W5 | S | B | for agency-bill terms, direct delinquency steps to the intermediary | — |  |
| REQ-BIL-167 | J4 | W5 | S | B | allow a collections specialist to agree a payment arrangement… | REQ-PLT-003 |  |
| REQ-BIL-168 | J4 | W5 | M | B | send the statutory non-payment notice as a DOC document… | REQ-DOC-001, REQ-DOC-005, REQ-MKT-002 |  |
| REQ-BIL-169 | J4 | W5 | M | B | start clock BIL_NONPAY_NOTICE via cmp.Clock.start only when DOC reports… | REQ-CMP-003, REQ-MKT-009 |  |
| REQ-BIL-170 | J4 | W5 | S | B | handle failed or unproven delivery of the statutory notice… | REQ-DOC-005 |  |
| REQ-BIL-171 | J4 | W5 | S | B | stop the WAITING_PERIOD clock (cmp.Clock.stop, outcome Cured, resulting state… | REQ-CMP-003 |  |
| REQ-BIL-172 | J4 | W5 | M | B | publish CancellationForNonPaymentRequested on ClockElapsed of BIL_NONPAY_NOTICE (R-78) only if… | REQ-POL-012, REQ-POL-311 | per contract ruling |
| REQ-BIL-173 | J4 | W5 | S | B | not shorten or bypass the notice clock for any… | REQ-POL-311 |  |
| REQ-BIL-174 | J4 | W5 | S | B | hold the cancellation request when PTY vulnerable-customer rules or… | REQ-PTY-011 |  |
| REQ-BIL-175 | J4 | W5 | M | B | react to CancellationRescinded and PolicyCancelled for non-payment by closing… | REQ-POL-209, REQ-POL-211 |  |
| REQ-BIL-176 | J4 | W5 | S | B | run a post-cancellation collection plan (reminders, then write-off proposal… | — |  |
| REQ-BIL-177 | J4 | W5 | M | B | accept reinstatement payments after a non-payment cancellation: compute the… | — |  |
| REQ-BIL-178 | J4 | W5 | S | E | prioritise the delinquency workbench queue by an explained score… | — |  |
| REQ-BIL-181 | J5 | W6 | M | B | create refunds from: cancellation and return-premium credits, voids after… | REQ-POL-218, REQ-POL-219 |  |
| REQ-BIL-182 | J5 | W6 | M | B | net a credit balance against open debit items on… | — |  |
| REQ-BIL-183 | J5 | W6 | S | B | never refund IPT and shall refund levies only as… | — |  |
| REQ-BIL-184 | J5 | W6 | S | B | show a refund calculation breakdown: credits by charge type,… | — |  |
| REQ-BIL-186 | J5 | W6 | M | B | choose the refund payout method (the payout route, distinct… | — |  |
| REQ-BIL-187 | J5 | W6 | M | B | determine the payee: the payer by default; the policyholder… | REQ-POL-010 |  |
| REQ-BIL-188 | J5 | W6 | L | B | check refund authority with plt.Authority.check on authority type BIL.Refund… | REQ-PLT-003, REQ-PLT-004, REQ-WRK-200, REQ-WRK-202 |  |
| REQ-BIL-189 | J5 | W6 | S | B | enforce segregation of duties: the user who requested or… | REQ-PLT-082 |  |
| REQ-BIL-190 | J5 | W6 | S | B | publish RefundApproved on approval and hand the refund to… | REQ-POL-219 |  |
| REQ-BIL-191 | J5 | W6 | S | B | let an approver reject a refund with reason, returning… | REQ-PLT-114 |  |
| REQ-BIL-192 | J5 | W6 | M | B | pay refunds of agency-bill terms directly to the customer… | — |  |
| REQ-BIL-194 | J5 | W6 | S | E | detect duplicate refunds (same payee, amount and source within… | — |  |
| REQ-BIL-195 | J5 | W6 | S | B | refund approval and disbursement queue (SCR-BIL-07) with reason, amount,… | — |  |
| REQ-BIL-196 | J5 | W6 | S | B | expose refund status to CHN for customers and intermediaries… | REQ-CHN-004 |  |
| REQ-BIL-197 | J4 | W5 | L | B | accept disbursement requests from BIL (refunds, commission payments, suspense… | REQ-CLM-004, REQ-PLT-014 |  |
| REQ-BIL-198 | J4 | W5 | M | B | verify that the source module's approval evidence exists and… | REQ-PLT-117, REQ-CLM-003 |  |
| REQ-BIL-199 | J4 | W5 | M | B | maintain payee bank accounts per party with verification status,… | REQ-PLT-004 |  |
| REQ-BIL-200 | J4 | W5 | M | B | screen every payee with pty.Screening.screen immediately before approval and… | REQ-PTY-006, REQ-PTY-175, REQ-PTY-182 |  |
| REQ-BIL-201 | J4 | W5 | S | B | stop held disbursements automatically on SanctionsHitRaised for a payee… | REQ-PTY-179 |  |
| REQ-BIL-202 | J4 | W5 | S | E | detect duplicate disbursements (same payee account, amount and source… | — |  |
| REQ-BIL-203 | J4 | W5 | S | B | verify the payee with PayeeVerification (name and IBAN) before… | REQ-MKT-102 |  |
| REQ-BIL-204 | J4 | W5 | M | B | proceed on Match; require user confirmation with the suggested… | REQ-PLT-003 |  |
| REQ-BIL-205 | J4 | W5 | S | E | perform VoP itself for every payee in a batch… | REQ-MKT-102 |  |
| REQ-BIL-206 | J4 | W5 | M | B | batch Approved disbursements into release batches per paying bank… | REQ-MKT-100, REQ-PLT-004 |  |
| REQ-BIL-207 | J4 | W5 | S | B | track bank acknowledgement (pain.002) to move disbursements to Issued,… | — |  |
| REQ-BIL-208 | J4 | W5 | M | B | stop a disbursement before release (Stopped) by the source… | REQ-CLM-004, REQ-CLM-125, REQ-CLM-126 |  |
| REQ-BIL-209 | J4 | W5 | M | B | support disbursement methods: SEPA credit transfer, SEPA instant credit… | REQ-MKT-100 |  |
| REQ-BIL-210 | J4 | W5 | S | B | process returned disbursements (beneficiary bank return) as Returned, reversing… | — |  |
| REQ-BIL-211 | J4 | W5 | S | B | post disbursement entries: on release, debit the source payable… | — |  |
| REQ-BIL-213 | J4 | W5 | S | B | expose bil.Disbursement.get/list to CLM and CHN with status, dates,… | REQ-CLM-009 |  |
| REQ-BIL-214 | J4 | W5 | S | B | stay operable when the VoP or sanctions service is… | REQ-PLT-164 |  |
| REQ-BIL-215 | J5 | W6 | S | B | apply small-balance tolerance configured per legal entity and currency… | — |  |
| REQ-BIL-216 | J5 | W6 | S | B | never apply tolerance to amounts subject to a running… | — |  |
| REQ-BIL-217 | J5 | W6 | S | B | create write-off requests for bad debt on items or… | — |  |
| REQ-BIL-218 | J5 | W6 | S | B | show a posting preview for a write-off (sub-ledger entries… | — |  |
| REQ-BIL-219 | J5 | W6 | M | B | check write-off authority with authority type BIL.WriteOff (amount, reason,… | REQ-PLT-003, REQ-PLT-004 |  |
| REQ-BIL-220 | J5 | W6 | S | B | post approved write-offs, set the written-off items to WrittenOff,… | REQ-FIN-001 |  |
| REQ-BIL-222 | J5 | W6 | S | B | record recoveries of written-off amounts by reversing the write-off… | — |  |
| REQ-BIL-225 | J5 | W6 | S | B | transfer money between billing accounts (receipt allocation or credit… | REQ-PLT-004 |  |
| REQ-BIL-226 | J5 | W6 | S | B | transfer money between terms of one account without approval… | — |  |
| REQ-BIL-227 | J5 | W6 | S | B | show a transfer preview with resulting balances and delinquency… | — |  |
| REQ-BIL-228 | J5 | W6 | M | B | move a policy term between billing accounts (on request,… | REQ-PTY-123 |  |
| REQ-BIL-229 | J5 | W6 | S | B | re-evaluate payment method and mandate validity after a move… | — |  |
| REQ-BIL-230 | J5 | W6 | S | B | audit moves and transfers with before and after account… | REQ-PLT-002 |  |
| REQ-BIL-231 | J5 | W6 | S | B | move money and move policy screen (SCR-BIL-10). | — |  |
| REQ-BIL-233 | J1 | W6 | M | B | set the bill mode of each term (DIRECT_BILL or… | REQ-PTY-009, REQ-PTY-240 |  |
| REQ-BIL-234 | J1 | W6 | M | B | accept an intermediary collection report only when pty.ProducerCode.validate returns… | REQ-PTY-008, REQ-PTY-205 |  |
| REQ-BIL-235 | J1 | W6 | M | B | recognise a reported intermediary collection as the customer's payment… | — |  |
| REQ-BIL-236 | J1 | W6 | S | B | require intermediaries in agency bill to report collections or… | — |  |
| REQ-BIL-237 | J1 | W6 | M | B | accept collection reports through CHN (portal, API) and file… | REQ-CHN-005, REQ-PLT-161 |  |
| REQ-BIL-238 | J1 | W6 | M | B | produce account-current statements per intermediary and settlement frequency, listing… | REQ-DOC-001, REQ-CHN-005 |  |
| REQ-BIL-239 | J1 | W6 | S | B | settle account currents net of commission (intermediary deducts commission)… | REQ-PTY-240 |  |
| REQ-BIL-240 | J1 | W6 | S | B | accept remittances from intermediaries by bank transfer with statement… | — |  |
| REQ-BIL-241 | J1 | W6 | S | B | record remittance differences per item (short payment, over-payment, disputed… | REQ-WRK-001 |  |
| REQ-BIL-243 | J1 | W6 | S | B | age intermediary receivables (current, 1–30, 31–60, 61–90, > 90… | REQ-PTY-008 |  |
| REQ-BIL-245 | J1 | W6 | S | B | apply a remittance-overdue rule: when an account current stays… | REQ-WRK-007 |  |
| REQ-BIL-247 | J1 | W6 | S | B | agency account current and reconciliation screen (SCR-BIL-11) for agency… | — |  |
| REQ-BIL-248 | J1 | W6 | S | B | handle returned intermediary remittances (bounced transfer) as reversals that… | — |  |
| REQ-BIL-249 | J1 | W6 | M | B | resolve the commission agreement per charge delta through pty.CommissionAgreement.resolve(producerCode,… | REQ-PTY-009, REQ-PTY-010 |  |
| REQ-BIL-250 | J1 | W6 | M | B | compute commission on the commissionable base (charge types flagged… | REQ-PFC-122, REQ-PTY-229 |  |
| REQ-BIL-251 | J1 | W6 | S | B | apply splits between producers (shares summing to 100%) and… | REQ-PTY-230 |  |
| REQ-BIL-252 | J1 | W6 | S | B | make commission payable on the configured basis per agreement:… | — |  |
| REQ-BIL-253 | J1 | W6 | S | B | calculate return commission on return-premium deltas using the agreement's… | REQ-PTY-228 |  |
| REQ-BIL-254 | J1 | W6 | S | B | apply chargeback rules on cancellation per agreement (pro rata… | REQ-PTY-232 |  |
| REQ-BIL-255 | J1 | W6 | S | B | recalculate commission when PTY notifies a backdated agreement version… | REQ-PTY-235 |  |
| REQ-BIL-256 | J1 | W6 | M | B | handle producer-of-record changes per the commission consequence on ProducerOfRecordChanged… | REQ-PTY-217, REQ-PTY-218 |  |
| REQ-BIL-258 | J1 | W6 | S | B | publish CommissionCalculated per statement period or per calculation batch… | REQ-FIN-001 |  |
| REQ-BIL-259 | J1 | W6 | M | B | produce commission statements per intermediary and frequency with lines… | REQ-DOC-001, REQ-CHN-005 |  |
| REQ-BIL-260 | J1 | W6 | S | B | net commission payable against amounts the intermediary owes (agency-bill… | — |  |
| REQ-BIL-261 | J1 | W6 | S | B | withhold commission payment when the intermediary's licence is not… | REQ-PTY-008 |  |
| REQ-BIL-262 | J1 | W6 | S | B | run commission payment runs: select payable statements, create disbursements… | — |  |
| REQ-BIL-263 | J1 | W6 | M | B | hand commission statements to FIN for expense and payable… | REQ-FIN-001, REQ-CMP-001 |  |
| REQ-BIL-265 | J1 | W6 | S | B | commission statements and payment run screen (SCR-BIL-12) with statement… | — |  |
| REQ-BIL-268 | J1 | W6 | M | B | expose commission data (calculations, statements, payments) to CHN for… | REQ-CHN-005, REQ-DAT-001 |  |
| REQ-BIL-269 | J4 | W5 | M | B | accrue levy payables on the written basis from levy… | REQ-PFC-124, REQ-PFC-125 |  |
| REQ-BIL-270 | J4 | W5 | S | B | aggregate levy payables per levy type, legal entity and… | REQ-MKT-087 |  |
| REQ-BIL-271 | J4 | W5 | S | B | publish LevyAccrued per levy type and period (daily increments… | REQ-FIN-005 |  |
| REQ-BIL-272 | J4 | W5 | M | B | create the remittance payable at period end with due… | REQ-CMP-003 |  |
| REQ-BIL-273 | J4 | W5 | S | B | publish LevyRemitted when the remittance disbursement clears and stop… | REQ-CMP-003 |  |
| REQ-BIL-274 | J4 | W5 | S | B | reconcile levy accrual per period to written MTPL premium… | REQ-MKT-087 |  |
| REQ-BIL-275 | J4 | W5 | M | B | accrue IPT payables per IPT class in the sub-ledger… | REQ-FIN-005 |  |
| REQ-BIL-276 | J4 | W5 | S | B | IPT and levy data to FIN for returns (REQ-FIN-005)… | REQ-FIN-005 |  |
| REQ-BIL-277 | J4 | W5 | M | B | support receipt-level taxes or stamps computed by TaxCalculator on… | REQ-MKT-087, REQ-MKT-008, REQ-MKT-265 |  |
| REQ-BIL-278 | J4 | W5 | S | B | handle levy and IPT corrections caused by out-of-sequence and… | REQ-POL-125 |  |
| REQ-BIL-279 | J4 | W5 | S | B | keep the billing sub-ledger as BillingLedgerEntry (header) and BillingLedgerLine… | — |  |

_End of range (line 6700). BIL continues with REQ-BIL-280 onward in the next digest._
