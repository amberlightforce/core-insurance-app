# SLICE PLAN — thin end-to-end slice (E2E-01 happy path)

**Approved approach:** D-USR-04..06, D-USR-08 (Docker). **Started:** 2026-10-07.

**Goal:** a person is quoted for one motor product, binds, gets an invoice with a fiscal document (stub), pays it, and the
journal shows balanced entries. It runs locally with `docker compose` and is proven by an automated E2E-01 test.

Each slice WP implements **only** the requirements the slice needs, drawn from the listed backlog WPs. Anything else
stays in its backlog WP for the widening waves. A builder lists which REQ ids it covered (full or partial).

## Batches (max 4 concurrent agents)

| Batch | WP | Scope | Backlog source | Model | Review |
|---|---|---|---|---|---|
| S0 | **SL-0 Platform wiring + Party reference vertical** | Module persistence pattern (DbContext per module on DbSession, per-module EF migrations applied by the migrate job), Development-only local sign-in, numbering service, command pipeline + outbox usage, Party create/search/get + producer, full stack in `docker compose` | W1-PLT-01/02/04 (subset), W2-PTY-01/04 (subset) | strongest | deep (security) |
| S0 | SL-FIX-WEB | Make web compact-number formatting independent of the runtime ICU version (CI red) | F-1d | Sonnet | spot-check |
| S1 | SL-MKT | Configuration resolver for legal entity GR-TEST, GR pack motor charge values **from the PRDs only** with legalStatus, rounding | W1-MKT-01/03/04 (subset) | Sonnet | light |
| S1 | SL-PFC | One motor product (Motor Private Car): compulsory MTPL + optional covers, question set, charge types, product version resolved by date | W2-PFC-01..04 (subset) | Sonnet | light |
| S1 | SL-RAT-UW | Rating worksheet via decision tables (illustrative tables marked test data), taxes/levies from MKT, UW evaluate (accept/refer/decline) | W3-RAT-01..03, W3-UW-01/02 (subset) | strongest | deep (money) |
| S1 | SL-POL | Quote create / risk data / rate / bind; policy, term, transaction (bitemporal minimum); charges handed to BIL | W4-POL-01..05 (subset) | strongest | deep (temporal) |
| S2 | SL-BIL | Billing account, charge intake, invoice + number, fiscal document via CMP stub, payment receipt + allocation, sub-ledger, FIN hand-off | W5-BIL-01/02/03/06, W5-CMP-01 (subset) | strongest | deep (ledger) |
| S2 | SL-FIN | Posting rules for written premium, levies, invoice, payment; balanced journals; minimal chart of accounts | W5-FIN-01/02 (subset) | strongest | deep (ledger) |
| S2 | SL-UI | Staff screens: party search/create, quote wizard, policy view, billing account/invoice/payment, journal view (Greek-first) | W4-CHN-01, W2-WRK-05 (subset) | Sonnet | light |
| S3 | SL-E2E | E2E-01 happy path on the compose stack: API-level test + Playwright; integration fixes | E2E-01 | Sonnet | light |

S1 builders code against the other modules' **generated interfaces and fakes** (interface-first) so they can run in
parallel; S2 wires the real implementations.

## Rules for every slice builder
- Docker is available: integration tests use **Testcontainers** (PostgreSQL 17). Do not use the embedded `~/pg17`.
- Build and test in WSL Ubuntu (`source ~/dn.sh`), because Windows App Control blocks freshly built DLLs.
- Follow the SL-0 reference vertical (Party) for module layout, persistence, migrations, commands, events, controllers.
- Regulatory values come only from the PRDs (D-REG-01..07); anything not Settled carries its legalStatus.
