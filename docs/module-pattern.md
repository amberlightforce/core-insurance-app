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
  `dotnet ef migrations add <Name> -c Release --project src/CoreIns.Modules.<X> --startup-project src/CoreIns.Modules.<X> --context <X>DbContext --output-dir Persistence/Migrations`
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
- Audited reads (e.g. revealing P2 data) are commands too, with `RequiresIdempotencyKey = false` (`RevealParty`).
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
in `Platform:Permissions:Grants` (Host `appsettings.json`). Bind the generated DTO, call the command handler (or the
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
