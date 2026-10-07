# Digest template (Phase 0 readers)

You are a Phase 0 reader for a Greek-market P&C core insurance build. Your job is to read the assigned
source EXHAUSTIVELY (every line, paging with offset/limit until the end) and write a factual digest.
Do NOT write code. Do NOT invent anything. Cite requirement IDs (REQ-/BR-/NFR-/SPI-/EVT- etc.) and
section numbers for every claim. If something is unclear, say so.

Context you must know:
- Binding stack (overrides any older PRD wording): C:\Users\Karl\Projects\coreinsurance\core-insurance-infra\ARCHITECTURE-DECISIONS.md
  and INFRASTRUCTURE.md. Read ARCHITECTURE-DECISIONS.md (short) first so you can flag conflicts.
  Summary: .NET 10 modular monolith, ASP.NET Core REST+OpenAPI, PostgreSQL 17 one schema per module,
  EF Core + Dapper, Hangfire, transactional outbox (no broker), React 19 + TS + Vite, React Aria,
  react-i18next, Entra ID / External ID, Gotenberg PDF/A, Azure Container Apps, Bicep, GitHub Actions.
  Explicitly NOT used: microservices, Kubernetes, message brokers, workflow servers/BPM engines,
  lakehouses, low-code platforms, MediatR, AutoMapper, MassTransit, FluentAssertions.
- 00-system-contract.md is the binding cross-PRD contract (domain model, events, IDs, conventions).

Write your digest as Markdown to the output path given in your prompt, using these sections
(omit a section only if it truly does not apply to your source; say "n/a"):

1. **Identity** — module code, title, one-paragraph purpose, explicit non-goals / out of scope.
2. **Size metrics** — counts: REQ (split Must/Should/Could if given), BR, NFR, screens, owned entities,
   events produced/consumed, APIs, AI features. How many requirements are tagged Motor MVP / phase P1
   vs later phases (if the PRD tags phases). Your S/M/L build-size estimate with one-line justification
   (S < ~40 Must reqs & few screens; M ~40–100; L > 100 or heavy money/temporal logic).
3. **Owned entities** — name, key attributes, state machine (states + transitions) if any.
4. **Consumed entities / dependencies** — what it reads from which module, via which API/event.
5. **Events** — produced (name, trigger, key payload) and consumed (name, from whom, reaction).
6. **APIs** — exposed (resource paths/operations) and consumed (whose).
7. **SPIs / country-pack interfaces** used or defined.
8. **Screens** — list (ID, name, persona, one line). Note design-guide patterns referenced.
9. **Regulatory, tax and statutory rules** — ONLY those the source states explicitly, verbatim-precise
   (rates, deadlines, levies, compulsory covers, reporting formats), with IDs. Separately list rules the
   source REFERENCES but does NOT specify (gaps), and rules it defers to configuration/country pack.
10. **Greek-market specifics** — AFM, myDATA, gov.gr, Information Centre/bureau, Greek language rules, EUR.
11. **Controls** — authority types registered, maker-checker list, audit specifics, SoD, GDPR handling.
12. **AI features** — each with its toggle/classification, and whether it is needed for MVP.
13. **Open issues / assumptions / CCRs** — as listed in the source (with IDs), and their status if stated.
14. **Conflicts and ambiguities found** — (a) with infra/stack (any mention of Kafka, BPM/Camunda,
    microservices, lakehouse, specific vendor products, other languages/DBs, etc.), (b) with the system
    contract or other PRDs you noticed, (c) internal contradictions, (d) things that cannot be built
    without a decision. Be specific: quote the line/ID.
15. **Build notes** — what would be hardest to build; what must exist first; suggested slicing.

Length: be dense, not verbose. Target 4,000–9,000 words. Tables are welcome. Accuracy over brevity.
When done, reply with: the output path, the S/M/L estimate, the top 5 conflicts/gaps, and the
"Motor MVP" requirement count if available.
