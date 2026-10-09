Current code was rendered through the actual Vite application routes using an isolated Playwright browser and synthetic API payloads shaped from generated RI/PTY/PFC contracts. These are presentation checks, not real-backend acceptance.

Registry, treaty detail and draft editor were checked at 1440 light, 1440 dark and 840 light. Editor bottom captures include participation controls and the save action. No browser page errors or document horizontal overflow occurred. Tables retain their own horizontal scrolling on narrower screens.

The local harness is recovery/2026-10-09-codex-takeover/review-ri-ui.mjs outside the repository. Real API acceptance remains required after the registry/recovery backend merges. The list layers field is additive and optional; no per-contract detail requests are made from the registry.
