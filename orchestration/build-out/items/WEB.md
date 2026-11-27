# Build-out items — WEB: Web shell (cross-cutting)

> Part of the build-out plan. Start at [`../README.md`](../README.md). Waves: [`../WAVES.md`](../WAVES.md).
> Code: `src/CoreIns.Modules.web/src/`, UI: `web/src/modules/`. Generated 2026-11-27 from the gap surveys (base `a49ab865`).

**1 items** — P0 1, P1 0, P2 0. Keep **Status** current: `todo` → `in progress` → `done` (append the merge commit, e.g. `done a1b2c3d`) or `dropped (reason)`.

| ID | Title | Scope | Pri | Size | Depends | Covers | Wave | Status |
|---|---|---|---|---|---|---|---|---|
| <a id="bo-web-01"></a>BO-WEB-01 | Module route and nav auto-registration | Every build-out UI item would otherwise edit `web/src/routes.tsx` and `web/src/app-shell/navigation.ts`, which the wave rules allow only one builder per wave to touch.<br>- Each module exports its routes from `web/src/modules/<mod>/routes.tsx` and its rail entries from `web/src/modules/<mod>/nav.ts` (new files).<br>- `routes.tsx` and `navigation.ts` collect them with `import.meta.glob(..., { eager: true })`, ordered by an explicit `order` number, so adding a module never edits the root files.<br>- Move the existing module routes and nav entries out of the root files; keep lazy loading and `RequireRole` guards unchanged.<br>- Keep the `implemented` placeholder logic working: a nav entry without a module route still shows `ModulePlaceholder`.<br>- Tests: `routes.test.tsx` and `app-shell/navigation.test.ts` must pass unchanged in behaviour; add one test that a fake module file is discovered. | P0 | M | — | (enabler) | 6 | todo |
