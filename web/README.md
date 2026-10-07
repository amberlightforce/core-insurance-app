# Web (React 19 + TypeScript + Vite)

Staff portal, served by the Host (`APP_ROLE=api`) as static files in Azure and by Vite during development.

| Script                            | What it does                                                                                                                              |
| --------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------- |
| `npm run dev`                     | Vite on <http://localhost:5173>; `/api` is proxied to the Host on port 5000                                                               |
| `npm run build`                   | Typecheck, then build to `dist/` (the Dockerfile copies it into the Host's `wwwroot`)                                                     |
| `npm run lint`                    | ESLint (type-aware) and Stylelint, zero warnings allowed                                                                                  |
| `npm run typecheck`               | `tsc` for the app and the Vite config                                                                                                     |
| `npm test`                        | Vitest (jsdom)                                                                                                                            |
| `npm run format` / `format:check` | Prettier                                                                                                                                  |
| `npm run gen:api`                 | Regenerates `src/api/schema.d.ts` from the Host's OpenAPI document (`http://localhost:5000/openapi/v1.json`, Host running in Development) |
| `npm run sbom`                    | CycloneDX SBOM of runtime dependencies to `../artifacts/sbom/web.cdx.json`                                                                |

Versions are pinned exactly (`.npmrc` sets `save-exact`) and updated weekly by Dependabot.

## Dependencies and why each is here (ADR §2 rule 11)

| Package                                                                                                            | Reason                                                                                                |
| ------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------- |
| `react`, `react-dom`                                                                                               | UI framework (ADR §1 front end)                                                                       |
| `react-router`                                                                                                     | Routing (ADR §1 front-end libraries)                                                                  |
| `@tanstack/react-query`                                                                                            | Server-state caching and fetching (ADR §1)                                                            |
| `react-hook-form`, `zod`, `@hookform/resolvers`                                                                    | Forms with schema validation (ADR §1)                                                                 |
| `i18next`, `react-i18next`                                                                                         | Greek/English UI text (ADR §1, PLAN §4.6)                                                             |
| `i18next-icu`, `intl-messageformat`                                                                                | ICU plural/select messages (D-FE-02); `intl-messageformat` is the peer of `i18next-icu`               |
| `react-aria`                                                                                                       | Accessible primitives for WCAG 2.2 AA; `I18nProvider` sets `el-GR` (ADR §1)                           |
| `typescript`                                                                                                       | Type checking; held on 5.x because `typescript-eslint` and `openapi-typescript` do not yet support 6+ |
| `vite`, `@vitejs/plugin-react`                                                                                     | Dev server and build (ADR §1)                                                                         |
| `vitest`, `jsdom`, `@testing-library/react`, `@testing-library/dom`, `@testing-library/jest-dom`                   | Unit/component tests (ADR §1 testing); `jsdom` 29 is the newest that runs on every Node 24 release    |
| `eslint`, `@eslint/js`, `typescript-eslint`, `eslint-plugin-react-hooks`, `eslint-plugin-react-refresh`, `globals` | Linting                                                                                               |
| `eslint-config-prettier`, `prettier`                                                                               | Formatting, without lint conflicts                                                                    |
| `stylelint`, `stylelint-config-standard`                                                                           | CSS linting; the design system adds token rules (D-FE-01)                                             |
| `openapi-typescript`                                                                                               | Generates the API client types from OpenAPI (ADR §1 API client)                                       |
| `@types/node`, `@types/react`, `@types/react-dom`                                                                  | Type definitions                                                                                      |
| `@cyclonedx/cyclonedx-npm`                                                                                         | SBOM on every build (ADR §2 rule 11)                                                                  |
