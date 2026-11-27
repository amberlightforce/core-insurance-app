# Architecture Decisions — Core Insurance System (Greece / EU P&C)

**Status:** Accepted · **Date:** 2026-10-07 · **Applies to:** all modules (PRD-01 … PRD-18), all environments

This page records the technology stack and engineering rules for the core insurance system, and the reason for each choice. It overrides any older wording in the PRD prompt pack. The infrastructure detail is in [INFRASTRUCTURE.md](INFRASTRUCTURE.md).

**Guiding principle:** use the mainstream, well-supported choice at every layer. The system must run correctly for 15+ years, so we prefer boring, proven technology, few dependencies, and rules the database enforces over cleverness.

---

## 1. Technology stack

| Layer | Decision | Reason |
|---|---|---|
| Backend language | **C# on .NET 10 (LTS)**, upgrading to each new LTS release every two years | One of the two standard languages for insurance core systems. Built-in exact `decimal` arithmetic for money. Large standard library (HTTP, JSON, XML with XSD validation, cryptography), so few third-party packages are needed |
| Web framework | **ASP.NET Core** (controllers), REST + **OpenAPI** | Standard .NET web stack; the OpenAPI document is generated from the code |
| Architecture | **Modular monolith**: one deployable backend; one project and one PostgreSQL schema per module | Clear module boundaries without the operational cost of microservices. Any module can be split out later if there is a proven need |
| Database | **PostgreSQL 17** (move to 18 for temporal constraints once available on the Azure managed service) | Default open-source relational database, available on every cloud. Strong constraints, row-level security, full-text search |
| Data access | **Entity Framework Core** (Npgsql provider); **Dapper** for performance-critical or set-based SQL (ledger, reporting marts) | Standard .NET pairing |
| Schema migrations | EF Core migrations, run as a separate deployment job | Versioned, reviewable, never run on application start-up |
| Background jobs and timers | **Hangfire** with PostgreSQL storage | The most widely used .NET job scheduler: retries, delayed and recurring jobs, and a dashboard. Statutory deadlines are stored as domain records; Hangfire only executes them |
| Domain events | **Transactional outbox pattern** in PostgreSQL, dispatched in order to in-process handlers | Standard pattern for reliable events. Azure Service Bus can be attached later without changing producers |
| Validation | **FluentValidation** at the API boundary; database constraints as the final guard | Standard |
| Front end | **React 19 + TypeScript + Vite**, served as static files by the ASP.NET Core application | The most widely used UI framework and the largest hiring pool. Same setup as Microsoft's ASP.NET Core + React template |
| Front-end libraries | React Router, TanStack Query, React Hook Form + Zod, react-i18next (Greek/English), React Aria (accessibility) | Standard React toolset; React Aria covers the WCAG 2.2 AA requirement |
| API client | Generated from the OpenAPI document (`openapi-typescript`) | The UI and API cannot drift apart |
| Identity | **Microsoft Entra ID** for staff (OpenID Connect, app roles mapped to business authority); **Microsoft Entra External ID** for customers, brokers and bank staff. Libraries: `Microsoft.Identity.Web` and MSAL React | Managed identity platform; we never build our own identity system |
| Documents | HTML templates rendered to PDF/A by headless Chromium (**Gotenberg** container) | Common approach; correct Greek typography; isolates the browser engine from the core process |
| Logging and monitoring | **Serilog** + **OpenTelemetry**, exported to Azure Monitor / Application Insights | Industry standard; vendor-neutral instrumentation |
| Testing | **xUnit**, **Testcontainers** (real PostgreSQL), **FsCheck** (property-based), **Stryker.NET** (mutation testing on money code), **NetArchTest** (module boundaries), **Playwright** (end-to-end), **Vitest** (UI) | Standard tool for each layer |
| Containers and CI/CD | **Docker**; Docker Compose for local development; **GitHub Actions** with OIDC federation to Azure | Default everywhere |
| Infrastructure as code | **Bicep** | Azure's own IaC tool, with no state file to manage |
| Hosting | **Azure** (EU region): Container Apps, Azure Database for PostgreSQL Flexible Server, Key Vault, Blob Storage, Application Insights, Communication Services (email) | Standard managed Azure setup for containerised applications; EU data residency |

**Not used, on purpose:**
- **Microservices, Kubernetes, message brokers, workflow servers, data lakehouses and low-code or packaged insurance platforms.** They add cost and operational risk without a requirement that justifies them today.
- **MediatR, AutoMapper, MassTransit (v9+) and FluentAssertions (v8+).** These moved to commercial licences in 2025. Use plain C# handlers, explicit mapping, and xUnit or Shouldly assertions instead.

---

## 2. Engineering rules (apply to every module)

1. **The database enforces the invariants.**
   - Money is `NUMERIC`, never floating point.
   - Every relationship has a foreign key; every enumerated value has a check constraint.
   - Effective-dated rows cannot overlap (exclusion constraints).
   - The billing and finance ledgers are **append-only**: the application role has no `UPDATE` or `DELETE` rights on ledger tables, and a deferred constraint guarantees every journal balances.
2. **Money is `decimal` in C#.** Rounding is explicit, with mode and precision per currency and tax line from configuration. No `double` or `float` anywhere in financial code (enforced by an analyser rule).
3. **Calculations are pure functions.** Rating, premium splitting, tax, commission, earning and reserve maths take inputs and return outputs: no database, clock or randomness inside. Every result can be reproduced years later from the stored inputs and the configuration hash.
4. **Nothing financial is edited or deleted.** Corrections are reversals plus new entries. Policy history is bitemporal, using reverse-and-reapply for out-of-sequence changes.
5. **Every lifecycle is an explicit state machine.** Quote, policy term, transaction, invoice, payment, claim, exposure and reserve each have a declared set of states and allowed transitions; anything else is rejected.
6. **Every command is idempotent.** Each state-changing API call carries an `Idempotency-Key`, so a retry can never bind, invoice or pay twice. Errors use RFC 9457 Problem Details.
7. **Events are written in the same transaction as the change** (outbox). Consumers are idempotent and can replay safely.
8. **Module boundaries are enforced in code.**
   - A module exposes only its public contract project.
   - It never reads another module's tables.
   - Core code never references a country pack.
   - NetArchTest checks all of this in CI.
9. **Every action is audited** in an insert-only audit table: actor, role, authority used, object, before and after values, reason and correlation ID.
10. **Country specifics live in country packs** behind typed interfaces (IdValidator, TaxCalculator, FiscalDocumentChannel, BureauAdapter, StatutoryClockSet, and so on). A Cyprus stub pack runs in CI.
11. **Minimal dependencies.** Each third-party package needs a stated reason. Versions are pinned and updated weekly by a bot. An SBOM is produced on every build.
12. **Synthetic data only outside production.**

---

## 3. Testing rules

- **Every Must requirement's Given/When/Then acceptance criterion becomes an automated test.**
- **Property-based tests** on financial invariants. For example: ledgers always balance; refunds never exceed premium paid; out-of-sequence changes give the same result as in-sequence ones.
- **Golden tests** for rating (any cent difference fails CI; re-baselining needs actuarial approval) and for rendered documents.
- **Mutation testing** on rating, billing, finance and reserve code to prove the tests catch bugs.
- **Integration tests run against a real PostgreSQL** (Testcontainers), never an in-memory substitute.
- **Nightly reconciliation jobs in every environment:** policy ↔ billing ↔ sub-ledger ↔ myDATA. Any break raises an incident.

---

## 4. Questions reviewers usually ask

| Question | Answer |
|---|---|
| Why .NET rather than Java? | Both are industry standard for insurance. .NET has exact decimal arithmetic in the language, a broad standard library (fewer dependencies) and first-class Azure support |
| Why not microservices? | A modular monolith is the recommended starting point. Boundaries are enforced in CI, so a module can be extracted later without rewriting it |
| Why no message broker? | One application doesn't need one. The outbox pattern gives reliable, ordered events today, and attaching Azure Service Bus later is a configuration change |
| Why Hangfire and not a workflow engine? | Insurance processes here are state machines with deadlines. The state and deadlines live in domain tables (auditable, queryable by regulators); Hangfire runs the due work with retries. No extra server to operate |
| Why no data lakehouse? | Reporting and regulatory marts are PostgreSQL schemas built by scheduled jobs. At a mid-size Greek insurer's volumes this is ample. A read replica is added when reporting load requires it |
| Why React and not Angular or Vue? | All are viable. React has the largest ecosystem and hiring pool, and React Aria gives the strongest accessibility support |
| Why Entra ID? | It is a managed identity platform with MFA, conditional access and audit. Building our own identity service would be a security and compliance liability |
| Why Bicep and not Terraform? | The hosting target is Azure only; Bicep is Microsoft's native tool and needs no state file. Terraform remains an option if a second cloud is ever required |
