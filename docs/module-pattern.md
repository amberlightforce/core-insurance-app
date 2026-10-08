# Module pattern (reference vertical: Party)

Every business module (MKT, PFC, RAT, UW, POL, BIL, FIN, …) is built the same way. The Party module
(`src/CoreIns.Modules.Party`) is the worked example: copy its shape, not its business rules. Rulings behind the
pattern: D-ARC-16 (in-process calls through contracts), D-ARC-24 (what a module may reference), D-API-04/06 (owners
type their own pre-release operations), D-SLC-01 (one pattern for every slice).

## 1. Layout

```
src/CoreIns.Modules.<X>/
  <X>Module.cs            Schema constant, Databases (migrate job), Add<X>Module (DI), error definitions
  Domain/                 State models (StateMachine), value rules, pack-facing helpers (no I/O)
  Persistence/            <X>DbContext (internal), row classes, design-time factory, Migrations/ (EF Core)
  Commands/               one file per command: record + FluentValidation validator + handler + auditor
  Queries/                Dapper reads and searches over the scope's connection
  Api/                    internal [ApiController]s: thin REST facades of the contract operations
  Services/               implementations of the generated in-process contracts (I<X><Resource>Service)
src/CoreIns.Modules.<X>.Contracts/Generated   generated from contracts/ (never edit by hand)
```

Party: `PartyModule.cs`, `Domain/StateModels.cs`, `Persistence/PartyDbContext.cs`, `Commands/CreateParty.cs`,
`Queries/PartyReader.cs`, `Queries/PartySearch.cs`, `Api/PartiesController.cs`, `Services/InProcessServices.cs`.

A module references only `CoreIns.Platform*`, `CoreIns.SharedKernel`, `CoreIns.Rules` and other modules'
`*.Contracts` (architecture tests). Country rules come through the SPIs in `CoreIns.Modules.Market.Contracts`
(`IIdValidator`, `INameTransliterator`, `IAddressFormatter`, `ILanguageRuleSet`, …), bound by the Host
(`CountryPackBinding`) until MKT's `mkt.Spi.bind` exists. A module never references a country pack.

Types are `internal` unless another assembly needs them. Controllers can be internal too: the Host registers module
assemblies with `AddModuleControllers` (`src/CoreIns.Platform/Http/ModuleControllers.cs`).

## 2. Contracts first

1. Type your operations in `contracts/openapi/<x>.yaml` (request/response components, parameters), keeping
   `x-maturity: pre-release`; set `x-status: full` on operations you complete. Events: `contracts/events/<x>/`.
2. `python contracts/openapi/validate.py --write-index`, then `dotnet run --project tools/CoreIns.ContractGen -c Release`
   (regenerates DTOs, events, in-process interfaces, fakes and samples); `-- --check` must pass in CI.
3. Code against the generated DTOs (`Contracts.Api`), event records (`Contracts.Events`, e.g. `PartyCreatedV1`) and
   interfaces (`IPartyPartyService`). Other modules call you only through those interfaces (or their fakes before you
   merge).

## 3. Persistence

- **One DbContext per module** deriving from `ModuleDbContext` (default schema = the module's `Schema`, migrations
  history in that schema, SharedKernel value converters for ids, business numbers, `Instant`, `BusinessDate`).
  Register it with `services.AddModuleDbContext<XDbContext>(Schema)`: it shares the scope's `DbSession` connection and
  transaction, so module rows, outbox events and audit records commit together (F-1b).
- Name tables, columns, keys, indexes and check constraints explicitly in snake_case (`PartyDbContext`). Put state
  lists into check constraints (`Codes.CheckSql<PartyStatus>("status")`).
- **Bitemporal child rows** (`BitemporalRow`): `valid_from/valid_to` (dates, half-open) and
  `recorded_from/recorded_to` (instants). Change = close the current row and insert its successor; never DELETE.
- **Migrations live in the module**: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/CoreIns.Modules.<X> --startup-project src/CoreIns.Modules.<X> --context <X>DbContext --output-dir Persistence/Migrations` (no `-c`: it means `--context`, not configuration)
  (copy `Persistence/Migrations/.editorconfig` from Party: generated code).
- **The migrate job applies them**: expose `public static IReadOnlyList<ModuleDatabaseDefinition> Databases { get; } =
  [ModuleDbContextRegistration.Define<XDbContext>(ModuleCode.X, Schema)];` and add `.. XModule.Databases` to
  `ModuleCatalog.Databases` in the Host. `Define` grants the `app` role SELECT, INSERT, UPDATE on every table of the
  model (no DELETE, nothing on the migrations history); pass other privileges only with a reason. Nothing migrates at
  api/worker start-up.
- **Reads with Dapper** on `session.OpenConnectionAsync()` + `session.Transaction` (a command reads its own writes).
  Map array columns into classes with setters, not positional records. Filter every query by the caller's legal entity
  (`legal_entity_id`, REQ-PTY-035): another entity's row is "not found", never an error.

## 4. Commands

Each state change is a command through the platform pipeline: **validation → transaction → idempotency → audit →
authority → handler** (`CommandRegistration.AddCommand`). Per command:

```csharp
internal sealed record CreateParty(PartyCreateRequest Request) : ICommand<PartyCreateResponse>;
internal sealed class CreatePartyValidator : AbstractValidator<CreateParty> { … }       // shape → <MOD>-ERR-VALIDATION
internal sealed class CreatePartyHandler(…) : ICommandHandler<CreateParty, PartyCreateResponse> { … }
internal sealed class CreatePartyAuditor : ICommandAuditor<CreateParty, PartyCreateResponse> { … } // object, number, keys

services.AddScoped<IValidator<CreateParty>, CreatePartyValidator>();
services.AddCommandAuditor<CreateParty, PartyCreateResponse, CreatePartyAuditor>();
services.AddCommand<CreateParty, PartyCreateResponse, CreatePartyHandler>(CommandDescriptor.For("pty.Party.create") with { SupportsDryRun = true });
```

- Expected failures are `Result` failures with module codes (`DomainError.Of(ModuleCode.PTY, "ID-CHECKDIGIT", …)`);
  register each code's status and Greek/English title with `services.AddErrorDefinitions(...)` (`PartyModule.Errors`).
- When a unique index can lose a race, call `db.SaveChangesAsync` inside the handler and turn the violation into a
  typed error (`CreatePartyHandler`); otherwise the pipeline's commit saves.
- Lifecycles go through a `StateMachine` (`IntermediaryStateModel.Machine.Fire(...)`); optimistic concurrency with
  `record_version` → `<MOD>-ERR-STALE` (`UpdateIntermediaryHandler`).
- Audited reads (e.g. revealing P2 data) are commands too, with `RequiresIdempotencyKey = false, Idempotent = false`
  (`RevealParty`). `Idempotent = false` skips the idempotency decorator entirely: a caller's key (HTTP header or an
  in-process caller's unit of work) must never cause a result holding personal data to be stored in
  `plt.idempotency_record`. Never return P2 data from a command that keeps `Idempotent = true`.
- Business numbers come from `INumberingService.NextAsync(new NumberRequest(NumberingSchemes.Party, validAt))`
  inside the handler (gapless series allocate in your transaction; formats per `Platform:Numbering`).

## 5. Events

Publish generated payload records through the outbox inside the handler; they commit with your rows:

```csharp
events.Publish(new OutgoingEvent(EventDescriptor.From(PartyCreatedV1.Descriptor), "Party", partyId.Value.ToString(),
    new PartyCreatedV1 { … }, BusinessKeys.Empty.With("partyId", partyId.Value.ToString())));
```

No P2/P3 value in a payload. Consumers register `AddEventHandler<TPayload, THandler>(...)`; handlers run in the worker
and must be idempotent and order-aware (D-ARC-26).

## 6. Personal data

Classify per the PRD. P2 identifiers (and other P2 values the PRD lists) are encrypted with `FieldEncryptor`
(AES-256-GCM, row-bound: pass the row id as row key) and searched through a `BlindIndexer` blind index (one index name
per scheme, candidates over all readable key versions); see `Domain/PartyProtection.cs`. Responses mask P2 by default;
unmasking needs a permission and a purpose and is audited. P2+ data is never a path or query parameter (D-SLC-05): searches by identifier take a POST body (pty.Party.searchByCriteria).

## 7. Controllers and permissions

Internal `[ApiController]` per resource, route `api/<mod>/v<major>/…` as in the OpenAPI document, one
`[Authorize(Policy = "<mod>.<Resource>.<verb>")]` per action (the operation's `x-permission`). Permissions map to roles
in `Platform:Permissions:Grants`, which is **not** in `appsettings.json` any more (D-PRG-21): each module owns one file,
`src/CoreIns.Host/permissions/<module>.json` (`pty`, `mkt`, `pfc`, `rat`, `uw`, `pol`, `bil`, `cmp`, `fin`, `clm`, `plt`, ...).
A new module adds its own file, shaped `{ "Platform": { "Permissions": { "Grants": { "<mod>.<Resource>.<verb>": [ "Role", ... ] } } } }`,
and, if it has authority grants, a `"Authority": { "Grants": [ ... ] }` sibling (illustrative ones carry `"Illustrative": true` and are
dropped in Production). The Host merges all files in `permissions/` at start-up (`Hosting/ModuleSettings.cs`; no registration needed,
the csproj copies `permissions/**/*.json` to build, publish and the container image); the same operation key or grant id in two
files stops the Host with an error naming both files. Never add permissions to another module's file or to `appsettings.json`.
Development sign-in users live in `src/CoreIns.Host/dev-users.Development.json`, loaded only in Development. Bind the generated DTO, call the command handler (or the
query), return `Results.Created/Ok` or `ProblemDetailsMapper` Problem Details. The platform middleware enforces
`Idempotency-Key` on POST/PUT/PATCH/DELETE; mark a query sent as POST with `[SkipIdempotency]`.

## 8. In-process contracts

Implement the generated `I<X><Resource>Service` in `Services/`, register it scoped. Commands run through the same
pipeline with the caller's `CommandOptions` (`using (context.Use(options.IdempotencyKey, options.DryRun))`); failures
throw `DomainException` carrying the module code. Not-yet-built members throw `<MOD>-ERR-NOT-AVAILABLE` and are listed
in the work package report.

## 9. Tests

- Integration tests in `tests/CoreIns.IntegrationTests/<X>/` on Testcontainers PostgreSQL 17 through the real Host
  (`ApiHostFactory`, roles via the `X-Test-Roles` header): happy path, every PTY-ERR code you raise, idempotent replay,
  permission refusal, outbox row and audit row in the same transaction, privileges of the `app` role
  (`PartyApiTests`, `IntermediaryApiTests`, `PartyDatabaseTests`).
- In-process contract tests resolve `I<X>…Service` from `factory.Services` in a scope with a filled `RequestContext`.
- Architecture tests must stay green (`tests/CoreIns.ArchitectureTests`).
- Requirement ids in test names or comments, so the work package report can map REQ → test.

## 10. Ledgers (D-ARC-34)

Every append-only ledger (BIL sub-ledger, FIN journals, later CLM, RI, commissions) follows one pattern; the reference is
`src/CoreIns.Modules.Billing/Persistence/BillingDatabaseSql.cs`.

- **Header + lines**, written only by the owning module's ledger writer, each entry published as one event in the same
  transaction (BIL: `LedgerWriter` → `BillingEntryPosted`).
- **Grants:** the app role gets SELECT, INSERT only on header and line tables; no UPDATE/DELETE.
- **Append-only triggers:** BEFORE UPDATE/DELETE (row) and BEFORE TRUNCATE (statement) refuse for every role,
  including the owner, and log a `SECURITY:` line.
- **Balance trigger:** a DEFERRABLE INITIALLY DEFERRED constraint trigger checks at commit that each entry has at least
  two lines and debits = credits per currency.
- **Seal trigger:** the header stores `created_txid bigint DEFAULT txid_current()`; a BEFORE INSERT trigger on the line
  table refuses a line whose header was not created by the current transaction (`txid_current()` is the top-level id,
  so EF savepoints are fine). The balance trigger alone is not enough: a balanced pair appended later passes it.
- **Tests, connected as the `app` role:** append a balanced pair to an existing entry in a later transaction (refused),
  UPDATE/DELETE/TRUNCATE (refused), an unbalanced entry (refused at commit), and the role's privileges.
- Amount and number columns of the documents the ledger refers to (invoices, items, receipts) are frozen by triggers too;
  only states move.

## Record time and the policy watermark (POL, D-SL3-03)

Bitemporal modules that must answer "as known at T" repeatably cannot stamp record time with a plain clock: a slow writer
or a replica with a lagging clock commits rows "in the past" of a reader that already answered. POL's rule, reusable by any
module with the same need:

- **Watermark.** `pol.policy.last_recorded_at` is the record time of the last command that wrote for the policy. It moves
  forward only (trigger), and the policy row is frozen otherwise (`tr_policy_frozen`).
- **Writers: lock, then stamp.** A command first calls `PolicyWriteLock.AcquireAsync` (`SELECT ... FOR UPDATE` on the policy
  row, then `t = max(IClock.Now, last_recorded_at + 1 µs)` truncated to microseconds, stored as the new watermark in the same
  transaction). Every row it writes uses that one `t`: record-period starts, transactions, charge lines and the closing
  `recorded_to`. A new policy's insert is its own lock (`ForNewPolicy`). The loser of a race waits `Policy:LockWaitSeconds`
  and then gets `POL-ERR-STALE` (409), never a 500.
- **The database enforces it.** `pol.require_stamp` refuses an insert whose record time is not the policy's current
  watermark; `pol.only_close_record_period` closes a record period only at the watermark (not at the database clock).
- **Readers take no lock.** Effective knownAt = `min(requested or now, committed watermark)`, carried in snapshot
  references. Anything committed later is stamped above the watermark, so an answer cannot change, whatever the clock skew
  between replicas. A reference above the watermark was never issued and is refused as forged.
- **Supersession is live metadata outside the hashed content** (`PolicySnapshots.GetDetailedAsync`): the content hash at
  (validAt, current watermark) is compared with the reference's; a new segment id with identical content is not superseded.
- **Tests:** concurrent writers, a reader during an uncommitted write, a writer whose clock is behind the watermark, a forged
  reference, and every trigger connected as the `app` role (`tests/CoreIns.IntegrationTests/Policy/Temporal`).

Review round (D1, D4): the database requires that a record row's policy was locked and stamped in the same transaction (the pol.policy trigger leaves a transaction-local mark that pol.require_stamp and pol.only_close_record_period check), and the watermark may not run more than a day plus the Development dev clock offset (plt.dev_clock) ahead of the database clock (pol.assert_watermark_cap).
