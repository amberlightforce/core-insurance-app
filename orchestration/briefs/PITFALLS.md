# Known pitfalls (D-PRG-20)

Every builder self-checks against this list before reporting. Every reviewer checks it first. Each item is a real defect
found by a review or by CI in slices 1–2; the decision id says where.

## Authority, approvals and separation of duties
1. **Check authority on aggregates, never per line.** Reserves: the resulting exposure total. Payments: each payment
   *and* the claim's cumulative paid. Splitting an amount into many small lines must not escape REFER or DENY (D-SL2-13).
2. **One approval per distinct referred authority** (type + cost type). A single approval must never cover a different
   type than the one the checker was checked against (D-SL2-13).
3. **Bind every approval to subject + type + content hash.** `verifyForExecution` must check all three and return the
   authority decided under; the executing module compares it with its own computed dimensions (SL2-PLT D1).
4. **Never let a client set the required authority, referral role or approval subject.** Approval requests are created
   in-process by the owning module only (SL2-PLT D1).
5. **Maker ≠ checker includes everyone who took part**: creator, editors, quoter, binder, evaluator, and the principal
   of an AI or service actor (SL2-PLT D2, D-UW-01).
6. **A rejection must not be launderable**: no public endpoint may feed fake facts that close or reset a decision; only
   the owning module's in-process path with the real stored data may change it (D-UW-01).
7. **System-proposed lines** (top-ups, releases) are set only by the server, never accepted from the client, and are
   still covered by an authority check (D-SL2-13).

## Money and ledgers
8. **Seal ledger entries at their creating transaction** (BEFORE INSERT trigger on lines checking the header's xmin);
   app role SELECT/INSERT only; prove it with a test run *as the app role* (D-ARC-34, twice in slice 1).
9. **Duplicate keys must actually fire**: a unique index that includes the source id is a superset of the source index
   and catches nothing; key duplicates on the business reference (e.g. payee + amount + claim) (D-SL2-10).
10. **Fail closed, never guess**: missing lines, null flags, unexpected methods or currencies suspend with a reason
    instead of posting a default (D-SL2-12).
11. **decimal/NUMERIC only**, explicit rounding from MKT, three-currency amounts equal while EUR-only (D-SL2-06).
12. **Every event field a consumer depends on is always set**, even if the schema marks it optional (D-SL2-12b).

## Time and concurrency
13. **No future knownAt from any input**, including a forged reference or ref string (SL2-POL-SNAP D1).
14. **A date-form validAt is the end of that Athens business day** (D-SLC-13); test DST days and term boundaries
    (half-open).
15. **Racing commands return `*-ERR-STALE` (409), never 500**: lock the aggregate (`SELECT … FOR UPDATE` or an advisory
    lock) and check `expectedRecordVersion`; map unique-violation races to 409 (SL2-CLM-CORE, D-UW-01 m1).
16. **No tight wall-clock or regex timeouts** (≥ 1 s or a hand-written check); a 50 ms regex timeout made binds fail
    under load (D-ARC-36).
17. **Bitemporal history is append-only**; never close or rewrite record periods retroactively (SL-POL M1).

## Personal data
18. **No P2 in URLs, events, logs, audit, idempotency records, ledger dimensions or browser storage.** Search criteria
    go in POST bodies (D-SLC-05). Audited reads use `Idempotent = false` (SL-0).
19. **Browser storage holds only opaque ids and non-sensitive labels**; CodeQL fails on account numbers or names in
    localStorage (`js/clear-text-storage-of-sensitive-data`).
20. **IBANs**: field-encrypted with a blind index, masked to the last 4, never echoed in errors (D-SL2-10).

## Contracts and modules
21. **Interface changes are additive; after typing, run ContractGen and keep `--check` green.** Generated samples must
    satisfy their own schema (min/max length, pattern); `python tools/CoreIns.ContractGen/validate_samples.py` must pass.
22. **When another WP changes an interface you call, use named arguments** and re-read the generated signature after
    merging main (SL2-CLM-CORE D1).
23. **Modules talk only through generated `*.Contracts`**; architecture tests enforce it.
24. **The PRD outranks the brief.** If the brief contradicts the PRD, follow the PRD and report it (D-SLC-12, D-SL2-10b).

## UI
25. **Match the Aegean mockup** (`design-guide/mockups/aegean-screens-v3.html`); use the shared PageHeader
    (`landing` / record), Section cards and design-system components; take side-by-side screenshots (D-USR-11/13).
26. **No render loops**: memoise table rows/columns and query keys; never pass a fresh array literal into DataTable;
    check the page answers within 2 s after every navigation in a real browser.
27. **Required inputs are required in the UI** (e.g. vehicle value for own damage), and every server input error is
    explained in plain Greek/English with a "go to step" action; codes only under "technical details".
28. **String sanitising uses global replacement** (`replaceAll` or `/g`), never `replace` of the first occurrence
    (CodeQL `js/incomplete-sanitization`).

## Tests, E2E and tooling
29. **E2E waits wait for the complete expected set** (e.g. every journal type), not the first matching record.
30. **Select dev users by their exact display name** in Playwright; several names contain "Underwriter".
31. **Absence-of-PII assertions search for the real value** (e.g. the full birth date), not a short substring that ids
    can contain by chance (D-ARC-37).
32. **New shell scripts are committed executable** (`git update-index --chmod=+x`); CI on Linux refuses them otherwise.
33. **Run vitest with `--maxWorkers=2`** on the dev laptop; redirect `dotnet test` output to a file (it hangs when piped).
34. **Commit early and often** (WIP commits are fine): another session deleted worktrees and uncommitted work was lost.
35. **Never touch the user's `coreins` compose project or other projects' containers**; isolated stacks use their own
    project name and ports and are torn down with `down -v`.

## Added during slice 3
36. **Production gates allow only `LegalStatus == Settled`**: never write the gate as "not in the pending set". A
    NotRegulatory (or any new) status on a regulatory row then slips through as if settled (SL3-MKT-TREATMENT D1).
    Load validation also rejects statuses that make no sense for the key (no NotRegulatory on a tax rule).
37. **Config keys that can never match are load errors**: a key shape the lookup never uses (a source on a kind that
    ignores sources, an `ANY` where lookups are always explicit) must be rejected at load, or a pack believes it has
    supplied a rule it hasn't.
38. **No lingering MSBuild nodes**: before any `dotnet build`/`test`/`run`, set `MSBUILDDISABLENODEREUSE=1` and
    `DOTNET_CLI_USE_MSBUILD_SERVER=0` (PowerShell: `$env:MSBUILDDISABLENODEREUSE='1'; $env:DOTNET_CLI_USE_MSBUILD_SERVER='0'`)
    and pass `-m:2`. Node reuse left 114 idle workers (14 GB) on the laptop and capped how many agents could run.
39. **Unique log/output paths**: write test logs under your own worktree or scratchpad (e.g. `<worktree>/.logs/<class>.log`),
    never a shared path like `.claude/worktrees/t1.log`; another agent clobbered one.
