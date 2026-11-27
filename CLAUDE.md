# Core Insurance App — agent guide

Greek-first P&C core insurance system: .NET 10 modular monolith (`src/`), React 19 front end (`web/`), PostgreSQL 17.
Repository layout and how to run it locally: `README.md`.

## Where the plan is

**Start at [`orchestration/README.md`](orchestration/README.md).** It lists every planning document as CURRENT,
REFERENCE or HISTORICAL. Plan and build only from CURRENT documents:

- Status: `orchestration/STATUS.md`
- Hardening waves 1–5: `orchestration/NEXT-STEPS.md` (items `NX-*`)
- Build-out waves 6–27, everything still missing (Products, Reports, Documents, Admin, portals, …):
  `orchestration/build-out/README.md` → `WAVES.md` → `items/<MODULE>.md` (items `BO-<MOD>-NN`)
- Builder process: `orchestration/briefs/LIGHT-WORKFLOW.md`; known defects: `orchestration/briefs/PITFALLS.md`
- Decisions: `orchestration/DECISIONS.md` (append-only)

Requirements (PRDs, design guide, mockup v3) live in the sibling folder `../core-insurance-prds/`.

## Ground rules

- When you finish a work item, set its Status in the item file in the same commit (`done <short-sha>`).
- New REST routes go into `contracts/openapi/<mod>.yaml` first; regenerate with `tools/CoreIns.ContractGen`.
- Modules reference each other only through `.Contracts` projects; country packs are never referenced by core code.
- Money is `decimal` (analyser COREINS001); time comes from `IClock` (COREINS002).
- Unsettled regulatory values stay fail-closed (`legalStatus` Unverified/PendingOpinion, refused in Production).
- UI work follows the Aegean mockup v3 visual language and gets light, dark and 390 px screenshots (D-USR-11).
