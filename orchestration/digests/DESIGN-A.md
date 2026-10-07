# DESIGN-A digest: Aegean design system (Parts 1–2, tokens, top-level brief)

| Item | Value |
|---|---|
| Digest of | `core-insurance-prds/design-guide/design-system-part-1.md` (1,014 lines, read in full), `design-system-part-2.md` (1,178 lines, read in full), `tokens/aegean.css` (695 lines) and `tokens/aegean.tokens.json` (2,871 lines) (structure only), the brief at the top of `AEGEAN-DESIGN-SYSTEM.md` (lines 1–47), and the option-17 CSS in `mockups/aegean-screens-v3.html` and `mockups/button-options.html` |
| Checked against | `core-insurance-infra/ARCHITECTURE-DECISIONS.md` (approved front-end libraries: React Router, TanStack Query, React Hook Form + Zod, react-i18next, React Aria; MSAL React; ADR rule 11: "Minimal dependencies. Each third-party package needs a stated reason.") |
| Not covered here | Part 3 (patterns, data viz, icons) and Part 4 (content/l10n, a11y checklist, signature moments SM storyboards, developer handoff, `toGreekUpper()` code). They are cited where Parts 1–2 depend on them. |
| Approved visual reference | **v3** (`mockups/aegean-screens-v3.html`). v1 and v2 are deprecated and must not be used. |
| Precedence | (1) accessibility and accuracy (WCAG 2.2 AA, correct figures); (2) the design doc; (3) visual taste. Marked ⚖ "Function-first" throughout. |

---

## 0. Stack conflicts and doc defects (read first)

These are ordered by how much they block an implementer.

### 0.1 Dependencies not on the ADR's approved list

The ADR names React Router, TanStack Query, React Hook Form + Zod, react-i18next, React Aria and MSAL React. The design guide (Part 2 §4.0.1 and the brief) adds more. All of them are permissively licensed (MIT, ISC or Apache-2.0), so none has a licence problem. Each one still needs a written "stated reason" under ADR rule 11, plus a pinned version and an SBOM entry.

| Guide requires | Licence | Status vs ADR | Recommendation |
|---|---|---|---|
| **FormatJS** (brief: "i18n: FormatJS") | MIT | **Conflicts.** The ADR picks **react-i18next**. | Use react-i18next for messages, and `Intl.*` plus React Aria's `I18nProvider` / `@internationalized/date` / `@internationalized/number` (which ship with React Aria) for dates and numbers. Do not add FormatJS. The brief should be corrected. |
| **TanStack Table v8 + TanStack Virtual v3** | MIT | Not listed. (TanStack *Query* is listed, so this is the same vendor family.) | Needs a reason entry. The alternative is the React Aria `Table` (sorting, selection, column resize, drag and drop, virtualiser), but it has no pinning, grouping or faceting. The spec (§4.35) needs pinning, multi-sort, grouping and 100k-row virtualisation, so TanStack is justified. Decision needed. |
| **Motion for React** (`motion`) | MIT | Not listed | Needed for springs, FLIP/layout animation, shared elements and interruption (MI-19, 24, 30, 31, 35, 52, 68, springs §2.8.3). A CSS-only fallback is specified for every spring, so the dependency is reducible but not removable without losing the spec. Needs a reason entry. |
| **Lucide** (`lucide-react`) | ISC | Not listed | The whole status map (Part 1 §2.1.4) names Lucide icons. Needs a reason entry. |
| **pdf.js** (`pdfjs-dist`) | Apache-2.0 | Not listed | Document viewer (§4.31). |
| **visx** | MIT | Not listed | Charts and the relationship graph (Part 3 §6, §4.38). Pulls in several d3 sub-packages, which are also ISC/BSD. |
| `web-vitals` (RUM for INP, §2.7.1) | Apache-2.0 | Not listed | Small. Needs a reason entry. |
| fontTools (CI font-coverage check), Capsize (metric tuning) | MIT | Dev/CI tooling only | Fine as build tools. fontTools is Python, so the front-end CI needs a Python step. |
| Stylelint (for the "lint rules" the guide mandates: animated properties, glass tint minimums, no raw values) | MIT | Not stated | Dev-only. The lint rules themselves are custom plugins we have to write. |

**Fonts:** Inter, Noto Sans, JetBrains Mono and Noto Sans Mono are all SIL OFL 1.1. They are allowed, and self-hosting is required (GDPR). Vendor the WOFF2 subsets into the repo (`/web/public/fonts`) rather than adding an npm font package. **Verify:** confirm that Inter 4.1 actually covers Greek Extended (U+1F00–1FFF). The guide's own CI check falls back to Noto Sans for any gap, so the stack is safe either way, but the subset plan (4 files per family) depends on the answer.

### 0.2 Auth wording mismatch

Part 1 A-02 says staff sign in through "Container Apps built-in authentication". The ADR says **MSAL React + Microsoft.Identity.Web**, and **Entra External ID** for brokers, customers and bank staff. This affects the welcome / session-start surface (SM-08) and how the pre-redirect screen is built. Follow the ADR.

### 0.3 Places where React Aria behaviour differs from the spec (custom work needed)

| Spec | React Aria default | What to do |
|---|---|---|
| Disabled controls **stay focusable** with `aria-disabled="true"` and a reason tooltip (§4.0.2, §4.1) | RAC `Button isDisabled` renders the native `disabled` attribute. That removes the button from the tab order, so its tooltip can never show. | Write a wrapper: do not pass `isDisabled`; set `aria-disabled`, swallow `onPress`, keep focusable, wire up `TooltipTrigger` and `aria-describedby`. Apply this to every control that has a reason to explain. |
| Currency input: **live grouping as you type** with the caret preserved, `12k`/`1,5ε` shorthand, paste heuristics with an info hint, **reject extra decimals with a message** (never round), **no ↑/↓ stepper on money** (§4.6) | `NumberField` formats on blur, rounds or clamps to `maximumFractionDigits`, and always binds ↑/↓ to increment. | Build `CurrencyField` on `useNumberFieldState`/`useNumberField` with a custom parser and formatter, or on `TextField` with our own el-GR money parser. Block ↑/↓ for currency. Percentage can mostly use `NumberField` with `step`. |
| Date entry accelerators (`σ`, `σήμερα`, `+30`, `+6μ`, `τμ`, `07102026`, `7.10.26`) parsed on blur (§4.5) | `DateField` is segment-based. Free text such as "σήμερα" or "+30" cannot be typed into segments. | Add a custom key-capture layer on the `DateField` group (intercept letters, `+` and `-` → small overlay input → parse → `setValue`) or offer a text-mode field. Decision needed. The segmented field itself (dd/mm/yyyy) is native. |
| Multi-select combobox with chips inside the field (§4.4) | No native multi `ComboBox` | Compose `TagGroup` + `ComboBox` (the documented pattern) yourself. |
| Greeklish (ELOT 743) and accent-insensitive matching | `useFilter` is accent-insensitive with `sensitivity:'base'`; it has no transliteration | Write a custom `filter` (NFD strip, `toLocaleLowerCase('el')`, ς→σ, ELOT 743 both ways). Share it between combobox, palette and table search. |
| Hover card / quick-look (§4.16), context menu (§4.17), non-modal side sheet (§4.20), non-modal drawer (§4.19), stepper (§4.23), mention composer (§4.33) | No dedicated primitives | Build these from `Popover isNonModal` + `useHover`, a `Popover` with `triggerRef` + `onContextMenu`, and `FocusScope` + `useDialog` with no `Modal`. |
| Tooltip on long-press (touch) (§4.15) | RAC tooltips do not open on touch | Custom long-press with `useLongPress`, or the bottom-sheet label fallback the spec allows. |
| Native `<dialog>.showModal()` and the native `popover` attribute "where supported" (§4.16, §4.18, Part 1 §2.9) | RAC renders overlays into its own portal | **Do not mix.** Use RAC `Modal`/`Popover` everywhere and point their portal at `#layer-root`. Follow §4.0.1's own stack note: "Do not mix two primitive libraries". |
| Toasts: "Our Toaster (no library)" | RAC now ships `UNSTABLE_ToastRegion`/`UNSTABLE_ToastQueue` (hooks `useToastRegion`) | Either is fine. Using RA's hooks avoids writing landmark and F6 logic, but the API is marked unstable. Pin it if used. |

### 0.4 Stale pre-v3 references (the button changed from filled to gradient-border on 2026-10-08)

1. **Checkbox checked hover** (§4.8) says "fill `primary.bg-hover`", and **switch on hover** (§4.10) says "`primary.bg-hover`". After v3, `color.interactive.primary.bg-hover` = `#F7F9FF` / `#1A2030` (a near-surface tint), so a checked checkbox would turn white on hover. **Use `color.interactive.accent.bg-hover`** (`#2743BD` / `#3557E0`).
2. **Split-button divider** (§4.1): `rgba(255,255,255,.24)` was designed for a dark filled button and is invisible on a white surface fill. Use `color.border.subtle`/`default` (decorative), or a 1.5 px gradient-border segment. Needs a design decision.
3. **MI-67 commit-ready sweep** specifies a sheen of `rgba(255,255,255,.45)`, which is invisible on a white fill. The v3 mockup uses `--gb-sheen: rgba(63,99,240,.12)` (light) / `rgba(143,169,255,.14)` (dark). **Use the mockup values.**
4. **MI-01** mentions a "gradient CTA: specular `::before` opacity 0.4 → 0.7". v3 uses a faint inner tint (`--gb-tint rgba(63,99,240,.05)` / dark `rgba(143,169,255,.07)`) plus a glow on hover. Follow v3.

### 0.5 Internal inconsistencies in Part 2

| Where | Inconsistency | Suggested resolution |
|---|---|---|
| §4.0.1 says toast stacking is "per §4.24"; §4.2 says the error summary banner is "§4.25" | Toast is §4.21 and Banner is §4.22 (§4.24 is Progress and §4.25 is Avatar) | Cross-reference typos. |
| Table header height | §4.35.1 anatomy: 36 px C / 44 px Cf. §4.35.2a: "height 38 px" | Ask. The §4.35.2a rules are newer (v3), so they are probably authoritative. |
| Priority cell | §4.35.2: "40 px mini bar (4 px, `brand.solid`)". §4.35.2a: "64 px × 6 px track, fill `interactive.accent.bg`" | Use §4.35.2a (v3). |
| Selection checkbox | §4.35.2a: "5 px radius, 1.5 px border". §4.8: `radius.sm` (4 px), 1 px border. **5 px is not a radius token.** | Raise it as a token request or use `radius.sm`. |
| Selection column width | §4.35.8: 32 px. Queue column template: 44 px | Use the template (44 px) for queues. |
| Queue row type sizes | 13.5 px, 11.5 px and 12.5 px are not in the type scale (§2.3.3) | They are not tokens. Request tokens or map them to `type.body.compact` / `type.mono.sm` / `type.caption`. |
| Table headers | §4.35.2 says currency goes in the header «Ασφάλιστρο (€)». §4.35.2a says units go into cells and headers never need «(€)» | §4.35.2a (v3) wins. |
| Token naming | The doc says `motion.delay.tooltip`, `motion.delay.skeleton`, `motion.delay.spinner`, `motion.min-display.skeleton`, `motion.stagger.list`. The CSS emits `--motion-duration-delay-tooltip`, `--motion-duration-min-display-skeleton`, `--motion-duration-stagger-list` | Use the CSS names (they are generated) or fix the generator. |
| Token naming | The doc says `material.solid.chrome`. The CSS emits `--material-chrome-solid` | Same. |
| Missing tokens in the CSS | `motion.ease.linear`, `motion.ease.emphasized` (incl. `linear()`), `motion.duration.exit.factor`, `border.width.*`, `border.focus.ring`, breakpoints `bp.*`, field widths `field.*`, springs (JSON only, `$type: "spring"` is not a standard DTCG type), material specular, noise and elevation, the `interactive.danger.bg-pressed` state | Add them to the generator, or define them in TS (springs, breakpoints). |
| Extra tokens in the CSS that are not in the doc tables | `--color-border-control-hover` (#5F6878 / #8F98A7), `--color-state-row-selected-hover` (#E5ECFF / #1B2649), `--color-state-focus-halo` (rgba(63,99,240,.22) / rgba(143,169,255,.28)), `--motion-ease-overshoot-fallback` | These are good, because they replace raw values used in the component specs. Use them. |

### 0.6 Browser APIs with partial support (the spec has fallbacks; implement them)

`backdrop-filter` (fallback to solid), `prefers-reduced-transparency` (Chromium only; the in-app setting is the source of truth), View Transitions API (Motion fallback), CSS `linear()` easing (fallback to `ease.standard`), scroll-driven animations for MI-36 (fallback: swap at the threshold), `navigator.deviceMemory` and `getBattery()` (Chromium only; tier detection uses a probe animation elsewhere), `scheduler.yield()` (fallback `requestIdleCallback`/`setTimeout`), `navigator.userAgentData` (fallback `navigator.platform`), `content-visibility` (Safari 18+), container queries (fine on the target browsers). Target browsers (A-03): Chromium current and −2, Safari iPadOS/iOS 17+, Firefox ESR.

---

## 1. The 20 rules that matter most (from the brief at the top of AEGEAN-DESIGN-SYSTEM.md, near-verbatim)

1. **Use tokens only.** No raw colours, sizes, radii, z-index or durations in components. Names follow `category.role.variant.state` → `--category-role-variant-state`.
2. **Greek first.** Design with Greek strings, which run 15–35% longer. Labels sit above fields and wrap to 2 lines. Buttons never truncate. Table headers are single-line, so size the columns to fit.
3. **Greek capitals drop the tonos** («ΑΣΦΑΛΙΣΤΗΡΙΟ», «ΜΑΪΟΣ»). Use the `toGreekUpper()` utility, never CSS `text-transform` alone. Sentence case everywhere else.
4. **el-GR formats:** 07/10/2026 · 14:32 · 1.234,56 € (no-break space before €) · 15 % · a true minus «−» (U+2212). The format follows the user's *region format* setting, not the language.
5. **Primary buttons:** a surface-coloured fill with a **1.5 px violet → blue → teal gradient border** (`#8250F2 → #3F63F0 → #14988D`; dark `#9B74FC → #6283FA → #2BB5A8`), a semibold label in `text.primary` and a glow on hover. At most one *commit* CTA (with a resting glow) per view.
6. **Blue (`interactive.accent`)** is for selection controls (checkbox, radio, switch, tab indicator, selected day, current step), focus rings and links. It is not used for button fills.
7. **Glass only on chrome and overlays** (nav rail, top bar, status bar, modals, palette, popovers, toasts, floating action bar). **Never** put glass behind tables, forms or body text. Max 3 blurred layers at once, and 0 on low-end devices.
8. **Every status** = colour + icon + text label, through the single `<StatusPill>` map. No colour-only meaning. Solid red pills are only for `breached` and `conflict`.
9. **Density:** compact by default for staff (28 px controls, 32 px single-line rows, 60 px two-line queue rows). Comfortable for the broker portal and customers. Touch raises targets to 44 px. Minimum target 24×24.
10. **Motion tiers:** feedback ≤ 150 ms; transitions 200–400 ms; signature moments only for rare milestones (SM-01…SM-08) and never on bulk actions. Motion never blocks input. Animate `transform` and `opacity` only.
11. **Reduced motion and reduced transparency** each have an exact fallback for every effect. The in-app setting is the source of truth.
12. **Count-up numbers (MI-69)** run only on dashboard figures. **Never** on approval, payment, rating or financial-transaction values.
13. **AI is optional.** AI output appears as an `AI-suggested` card (Iris gradient edge) with Accept / Edit / Reject / Why?. Nothing AI-generated enters the record without a human action, and the layout works with AI switched off.
14. **Maker-checker:** actions that need approval say «Αποστολή για έγκριση» (Send for approval). The maker can never be the checker. Destructive confirmation strength scales with impact (Part 3 §5.13).
15. **Money:** right-aligned in tables, `tabular-nums`, precision from the currency rules. Extra decimals are rejected and never silently rounded. Payment approvals show the amount in words.
16. **Accessibility:** text contrast ≥ 4.5:1, UI ≥ 3:1. The focus ring is always visible (2 px, offset 2 px). Full keyboard support (J/K lists, Ctrl/⌘+K palette). Verified by `tools/contrast.py`.
17. **Layout:** content never scrolls under glass; working surfaces are opaque sheets. Split-pane workbenches. Desktop is primary (1440×900 reference, 1280×720 minimum); tablet and phone are for brokers.
18. **Charts:** max 6 categorical series from the CVD-validated palette; no pie charts; always offer a data-table toggle.
19. **Fonts:** Inter (UI) and JetBrains Mono (IDs, IBAN, codes), self-hosted (GDPR), with Greek and Greek Extended subsets.
20. **Don't invent** components, colours or states. If something is missing, follow the closest existing pattern and flag it as an assumption.

---

## 2. Assumptions (Part 1 §0.1)

| # | Assumption | Consequence |
|---|---|---|
| A-01 | The binding product sources are the System Contract (`00-system-contract.md`) and the Baseline Inventory (`UIL-*` screens, `IB-01…33` patterns). The 17 module PRDs did not exist when the guide was written. | Every component cites a contract section, UIL, IB or ROLE. Module PRDs must cite the guide's component names. |
| A-02 | Stack: React + TS + Vite SPA in `/web`, served by the ASP.NET Core `api` (Vite dev on 5173 proxying to 5000). Entra sign-in. Gotenberg PDFs. No low-code (CD-20). Tokens ship as CSS custom properties plus DTCG JSON; components sit on headless accessible primitives. | Stack issues are marked "⚠ Stack note". The login is Microsoft-hosted, so "login" in this system means the pre-redirect **welcome** screen and the post-sign-in **session start** (SM-08). |
| A-03 | Browsers: Chromium current and −2 (staff), Safari iPadOS/iOS 17+ (brokers), Firefox ESR (secondary). | Every effect has a fallback. |
| A-04 | Low-end hardware means 4 cores, ≤ 8 GB RAM, integrated GPU, 1366×768 or 1920×1080 at 100%. Bank-branch staff (ROLE-08) are the most likely users of it. | The Low tier disables all real-time blur. |
| A-05 | There are no brand guidelines. Fairfax-owned, one deployment stamp per legal entity. | "Aegean" is a placeholder in **one layer** (`brand.*`). Entities may override `brand.*` only. Semantic, status and data-viz tokens are fixed. |
| A-06 | Primary users are staff ROLE-03…46 plus intermediaries ROLE-05/06/07/08. Customers are secondary. | Compact is the default for staff. Comfortable for brokers and customers. |
| A-07 | "Liquid glass" maps to IB-18 (floating overlay layer; working tables stay flat) and IB-24 (quiet chrome). | Glass never on working surfaces. |
| A-08 | Presence (IB-19) and pinned comments (IB-20) are supported. There is **no** live multi-user co-editing of transactions; jobs lock. | Collaborative cursors only in the template/clause editor (UIL-U11) and product-version review (UIL-U7/U9). Everywhere else: avatars plus field-level locks. |
| A-09 | Non-production environments exist and can have a moved system clock (`SCR-PCACC2-14`). | A mandatory environment ribbon. |
| A-10 | AI is optional and has a global kill within 60 s (contract §3.8.2). | Every AI surface has a non-AI layout with no hole. The `ai.*` colours are used only while AI is on. |
| A-11 | Greek is binding and shown first. English is complete at go-live. Polytonic Greek is needed in the type stack (names, addresses, legal citations). | §2.3 type and casing rules. |

---

## 3. Aesthetic-vs-usability conflicts and resolutions (Part 1 §0.2)

| # | Conflict | Resolution |
|---|---|---|
| C-01 | Glass chrome with dense tables scrolling under it (ghosted figures, misread premiums) | **Content never scrolls under glass.** Working surfaces are opaque **sheets** inset from the chrome. The chrome only refracts the static ambient canvas. Table headers stick inside the sheet. |
| C-02 | Real refraction needs an SVG displacement map in `backdrop-filter:url()` (Chromium only, 3–8 ms per frame per layer) | Simulated with a static specular gradient and a 1 px rim light. Real displacement only on the pre-sign-in welcome card on High-tier devices (SM-08). |
| C-03 | Blurred imagery behind dashboard figures | Imagery only in the **canopy band** (top 240 px; 200 compact), shell gutters, the login page and empty-state frames, always under a measured scrim. KPI tiles and charts sit on opaque sheets. |
| C-04 | Gradients on primary CTAs (midpoint contrast, visual noise) | Gradient stops are verified. **One gradient (commit) CTA per view**, on the single commit action (Bind, Issue, Submit FNOL, Approve, Close period). Since v3 the "gradient" is the 1.5 px border of every primary button; the commit variant adds a resting glow. |
| C-05 | Rich microinteractions for actions repeated hundreds of times | A three-tier motion system. Feedback ≤ 150 ms. Signature moments fire **once per business object per user**, never on bulk, and can be switched off ("Celebrations", default on). |
| C-06 | Signature animation after Bind/Issue blocks the next action | Signature moments are non-modal overlays with `pointer-events:none` on decorative layers. The next action has focus at t = 0. Any key or click dismisses the decoration within 1 frame. |
| C-07 | Number tickers on changing values (a transient €12,4… could be approved) | Home and dashboard figures count up on arrival (MI-69, 1,300 ms) and tick on live change (MI-16). **Never** in approval, payment, rating or financial-transaction contexts. The final value is in `aria-label` from frame 0. Reduced motion shows it instantly. |
| C-08 | Compact density vs WCAG 2.5.8 (24×24) | Compact control 28 px, row 32 px, icon-button hit area ≥ 24×24. No row below 28 px. |
| C-09 | Dark glass over bright imagery | Dark tint opacities are higher (chrome 0.84 vs 0.80). Verified against 9 worst-case backdrops. |
| C-10 | Collaborative cursors suggest co-editing that does not exist | Cursors only where co-editing is real (A-08). Elsewhere: presence avatars plus a `locked` state. |
| C-11 | "Most advanced software" vs 8-hour fatigue | Effects are concentrated in the chrome and rare moments; the 95% of pixels that are data stay calm, neutral and flat. Imagery, transparency and celebrations are independently switchable. |

---

## 4. Principles (Part 1 §1): when two collide, the lower number wins

| # | Principle | Concrete rule |
|---|---|---|
| P1 | **Accuracy over impression.** | Quote summary «Συνολικό ασφάλιστρο 412,38 €» in tabular figures with the premium, IPT 15% and levy broken out, plus a **Why?** link (IB-08). Reserve approval figures are final from frame 0. No rounded animated hero numbers. |
| P2 | **The record is the hero; chrome recedes** (IB-24). | The claim gets about 78% of 1440 px; the rail collapses to 56 px. Translucent top bar, opaque claim sheet. |
| P3 | **Speed is a feature; motion never makes anyone wait.** | Ctrl+K → "ren 4471" → Enter opens a renewal in about 300 ms, with the 240 ms panel slide running while the data is already interactive. J/K selection has 0 ms visual latency; the detail renders from cache within 1 frame. |
| P4 | **People decide; the system proposes, visibly.** | AI suggestions are a violet-edged `AI-suggested` card with a before→after diff and Accept/Edit/Why?. After acceptance: «AI-generated · accepted by Μ. Παπαδοπούλου» in the audit trail. Never silent prefill. |
| P5 | **State is never ambiguous.** | Every state = colour + icon + text. `Breached` is the only solid status (alongside `conflict`). |
| P6 | **Greek first, and designed for Greek.** | Layouts are designed with Greek and checked in English. «Υποβολή αναγγελίας ζημίας» fits at the designed width. Overlines strip the tonos. |
| P7 | **Reversible by default, deliberate when not.** | Low-stakes edits are instant with undo (toast «Αποθηκεύτηκε · Αναίρεση», 6 s). Legal or financial effects get a preview, confirmation proportional to impact, and maker-checker («Αποστολή για έγκριση», naming the amount, payee, masked IBAN and approver group). |

---

## 5. Foundations and tokens

### 5.1 Token architecture

- Name: `<category>.<role>.<variant>.<state>` → CSS `--category-role-variant-state` (e.g. `--color-text-secondary`, `--color-status-success-fg`).
- Three layers: **Primitive** `palette.*` (components must **not** reference these) → **Semantic** `color.*`, `space.*`, `motion.*` … (theme- and density-aware) → **Component** (`button.primary.bg` …, aliased to semantic).
- Theme switching: `:root, [data-theme="light"]` is the light default; `[data-theme="dark"]` is dark; `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }` gives automatic dark (this block is byte-identical to the `[data-theme=dark]` block, which I verified). Density: `:root, [data-density="compact"]`, `[data-density="comfortable"]`, `@media (pointer: coarse)` raises the sizes.
- Only `brand.*` may be overridden per legal entity (A-05).

### 5.2 Token files: structure and counts

**`tokens/aegean.css`** (generated by `tools/build_tokens.py`, "do not edit by hand"), 695 lines:

| Block | Declarations |
|---|---|
| `:root, [data-theme="light"]` (lines 2–380) | 376, including `color-scheme` |
| `[data-theme="dark"]` (381–510) | 127 (theme-varying only) |
| `@media (prefers-color-scheme: dark) :root:not([data-theme="light"])` (511–642) | 127 (identical to the dark block) |
| `:root, [data-density="compact"]` / `[data-density="comfortable"]` / `@media (pointer: coarse)` | 15 each |

Light-block groups: `palette` 139 · `color` 95 · `type` 32 (16 shorthand + 16 tracking/opsz) · `material` 24 · `z` 23 · `motion` 22 · `font` 18 · `space` 15 · `gradient` 9 · `radius` 8 · `elevation` 7.

**`tokens/aegean.tokens.json`** (DTCG-style, `$value`/`$type`), 2,871 lines. Leaf token counts by top group:

| Group | Leaves | Sub-groups |
|---|---|---|
| `palette` | 139 | neutral 26, aegean 14, green 13, red 14, amber 12, sky 12, teal 12, plum 12, violet 12, ochre 12 |
| `color` | 95 | surface 7, text 11, border 6, interactive 18, state 6, status 40 (10 families × fg/bg/border/solid), chart 7 |
| `gradient` | 9 | cta, iris, tide, laurel, horizon-positive/adverse/neutral, ambient, specular |
| `elevation` | 7 | 0–5, pinned-column |
| `material` | 24 | chrome/overlay/popover/float × (blur, saturate, tint, rim, hairline, solid) |
| `space` | 15 | 0, px, 0_5, 1, 1_5, 2, 3, 4, 5, 6, 8, 10, 12, 16, 20 |
| `radius` | 8 | none, xs, sm, md, lg, xl, 2xl, full |
| `motion` | 26 | duration 16, ease 6, spring 4 (`$type:"spring"`, non-standard) |
| `z` | 23 | see §5.11 |
| `font` | 2 | family sans, mono |
| `type` | 16 | display-xl … numeric-kpi |
| `density` | 15 | size-control-sm/md/lg, size-row-table/-dense/list, size-tab, space-inset-control-x/cell-x/cell-y, space-stack-field/section, space-inset-card, size-topbar, size-rail |

Key CSS names an implementer will use: `--color-interactive-{primary|accent|secondary|ghost|danger|disabled}-bg[-hover|-pressed]`, `--color-interactive-primary-border-{start|mid|end}`, `--color-interactive-primary-glow`, `--color-status-{neutral|success|warning|danger|info|brand|teal|plum|ai|ochre}-{fg|bg|border|solid}`, `--color-border-{subtle|default|control|control-hover|strong|focus}`, `--color-surface-{canvas|sheet|sunken|raised|inverse|scrim-modal|scrim-image}`, `--color-state-{row-hover|row-selected|row-selected-hover|row-selected-indicator|drop-target|focus-halo}`, `--color-chart-cat-{1..6|other}`, `--material-{chrome|overlay|popover|float}-{blur|saturate|tint|rim|hairline|solid}`, `--type-*` (CSS `font` shorthands such as `600 20px/28px var(--font-sans)`).

### 5.3 Colour

#### Primitive scales (sRGB hex; do not interpolate new steps, request them)

- **Neutral "graphite"** (cool, 3° toward blue): 0 `#FFFFFF`, 25 `#FBFCFD`, 50 `#F6F7F9`, 60 `#F4F5F7`, 75 `#F3F5F8`, 100 `#EEF0F3`, 150 `#E4E7EC`, 200 `#D7DBE2`, 300 `#B9C0CB`, 350 `#98A1B0`, 400 `#8F98A7`, 450 `#7E8798`, 500 `#6B7485`, 550 `#5F6878`, 600 `#525B6B`, 650 `#4A5262`, 700 `#3D4452`, 750 `#323844`, 800 `#2A303B`, 825 `#232831`, 850 `#1F242D`, 875 `#1C2028`, 900 `#161A21`, 925 `#12151B`, 950 `#0E1116`, 1000 `#090B0F`.
- **Chromatic** (steps 50–950 plus a 975 dark status surface): aegean (brand; 500 `#3F63F0`, 600 `#2E4FD6`), green (success), amber (warning), red (danger), sky (info), teal, plum (review), violet (AI; 500 `#8250F2`), ochre (lapse). Irregular steps exist where contrast needed them (aegean 550/650, green 550, red 650/675). Dark selected row `#1A2547`.

#### Semantic: surfaces

| Token | Light | Dark | Use |
|---|---|---|---|
| `color.surface.canvas` | `#F6F7F9` | `#0E1116` | App background; ambient imagery sits here |
| `color.surface.sheet` | `#FFFFFF` | `#161A21` | **All working surfaces**, always opaque |
| `color.surface.sunken` | `#F3F5F8` | `#12151B` | Wells, read-only backgrounds, IB-29 zebra, code |
| `color.surface.raised` | `#FFFFFF` + elev.1 | `#1F242D` + elev.1 | Cards on canvas, sticky table header |
| `color.surface.inverse` | `#161A21` | `#EEF0F3` | Tooltips, kbd hint chip |
| `color.surface.scrim.modal` | `rgba(14,17,22,.40)` | `rgba(0,0,0,.60)` | Behind modals and the palette |
| `color.surface.scrim.image` | `rgba(246,247,249,.80)` | `rgba(14,17,22,.82)` | Over imagery |

#### Semantic: text

| Token | Light | Dark | Notes |
|---|---|---|---|
| `color.text.primary` | `#161A21` | `#EEF0F3` | 17.44 / 15.28 on sheet |
| `color.text.secondary` | `#4A5262` | `#B9C0CB` | Labels, column headers |
| `color.text.tertiary` | `#5F6878` | `#98A1B0` | Timestamps, helper. **Banned on glass and imagery.** |
| `color.text.placeholder` | `#5F6878` | `#98A1B0` | Passes 4.5:1 ⚖ (Greek format hints) |
| `color.text.disabled` | `#98A1B0` | `#5F6878` | Exempt. Always with `aria-disabled` plus a reason. |
| `color.text.link` / `link-hover` | `#2E4FD6` / `#1F3586` | `#8FA9FF` / `#BACCFF` | Underlined in body copy, 1 px, offset 2 px |
| `color.text.on-primary` | `#161A21` | `#EEF0F3` | Gradient-border button label, weight 600 |
| `color.text.on-accent` | `#FFFFFF` | `#FFFFFF` | Glyphs on accent fills |
| `color.text.inverse` | `#FFFFFF` | `#161A21` | Tooltip text |
| `color.text.adverse` | = `status.danger.fg` (`#AE1C1C` / `#FF9C9C`) | | Adverse money. Always with a sign and a direction word. |

#### Semantic: borders

| Token | Light | Dark | Use |
|---|---|---|---|
| `color.border.subtle` | `#E4E7EC` | `#2A303B` | Row and section dividers (decorative) |
| `color.border.default` | `#D7DBE2` | `#3D4452` | Card outlines, panel edges |
| `color.border.control` | `#7E8798` | `#6B7485` | Input, checkbox and radio boundaries, ≥ 3:1 on every surface |
| `color.border.control-hover` (CSS only) | `#5F6878` | `#8F98A7` | Input and checkbox hover |
| `color.border.strong` | `#3D4452` | `#B9C0CB` | Selected segment, active tab underline |
| `color.border.focus` | `#3F63F0` | `#8FA9FF` | Focus rings, 4.94 / 7.71 (worst case 4.44 on the light selected row) |

#### Semantic: interactive

| Token | Light | Dark | Notes |
|---|---|---|---|
| `color.interactive.primary.bg` | `#FFFFFF` | `#161A21` | = surface (gradient-border button) |
| `…primary.bg-hover` / `-pressed` | `#F7F9FF` / `#EEF3FF` | `#1A2030` / `#1D2540` | |
| `…primary.border-start/mid/end` | `#8250F2` / `#3F63F0` / `#14988D` | `#9B74FC` / `#6283FA` / `#2BB5A8` | 120°; each stop ≥ 3:1 vs surface |
| `…primary.glow` | `rgba(63,99,240,.55)` | `rgba(98,131,250,.60)` | |
| `color.interactive.accent.bg` / `-hover` / `-pressed` | `#2E4FD6` / `#2743BD` / `#1F3586` | `#3F63F0` / `#3557E0` / `#2E4FD6` | Selection controls (checkbox, radio, switch on, selected day, tab indicator, current step) |
| `color.interactive.secondary.bg` / `-hover` / `-pressed` | `#FFFFFF` / `#F4F5F7` / `#EEF0F3` | `#1F242D` / `#232831` / `#2A303B` | Border `border.control` |
| `color.interactive.ghost.bg-hover` / `-pressed` | `rgba(22,26,33,.05)` / `.09` | `rgba(255,255,255,.06)` / `.10` | Icon and toolbar buttons |
| `color.interactive.danger.bg` / `-hover` | `#C81E1E` / `#AE1C1C` | `#D12424` / `#B91F1F` | White text 5.74 / 5.26 |
| `color.interactive.disabled.bg` | `#EEF0F3` | `#232831` | |
| `color.state.row-hover` | `#F4F5F7` | `#1C2028` | |
| `color.state.row-selected` / `-indicator` | `#EEF3FF` / `#2E4FD6` | `#1A2547` / `#8FA9FF` | 2 px left bar |
| `color.state.drop-target` | `rgba(63,99,240,.08)` + 1 px dashed `#3F63F0` | `rgba(143,169,255,.10)` + dashed `#8FA9FF` | |
| Forced colours | `ButtonFace` fill, 1.5 px `ButtonText` border, `ButtonText` label | | |

#### Status families (`color.status.<family>.{fg,bg,border,solid}`)

All `fg` values pass 4.5:1 on their `bg` and on the sheet. All `solid` values pass 3:1 on the sheet and on their `bg`.

| Family | Light fg / bg / border / solid | Dark fg / bg / border / solid |
|---|---|---|
| neutral | `#3D4452` `#EEF0F3` `#D7DBE2` `#6B7485` | `#D7DBE2` `#232831` `#3D4452` `#8F98A7` |
| success | `#126341` `#ECFAF2` `#A6E4C3` `#1A8A57` | `#6FD09F` `#0F241B` `#124F36` `#3BB67B` |
| warning | `#8A4A0B` `#FFF8EB` `#FDD98A` `#B66607` | `#FBBF4D` `#2A1E0B` `#763F10` `#F5A524` |
| danger | `#AE1C1C` `#FFF1F1` `#FFC6C6` `#D12424` | `#FF9C9C` `#2E1214` `#8F1B1B` `#F96767` |
| info | `#075E8E` `#EBF8FF` `#A8E0FC` `#0475B0` | `#6CCBF8` `#0E2433` `#0C4F75` `#2FB0EE` |
| brand | `#243FAD` `#EEF3FF` `#BACCFF` `#2E4FD6` | `#8FA9FF` `#161F3D` `#1F3586` `#6283FA` |
| teal | `#0F615B` `#EBFAF8` `#9DE6DC` `#0E7A72` | `#5FD1C4` `#0C2422` `#114E4A` `#2BB5A8` |
| plum (review) | `#93236A` `#FDF2FA` `#F7C9EA` `#B32D82` | `#F0A0D8` `#2C1225` `#7A2059` `#E46BBE` |
| ai | `#5D2ABB` `#F5F2FF` `#D8CCFF` `#6F36DE` | `#BBA4FF` `#1E1638` `#4D2599` `#9B74FC` |
| ochre (lapse) | `#7A4A06` `#FDF6E7` `#F2D79A` `#A86A0C` | `#E8C27A` `#2A200E` `#6E4A12` `#D9A33F` |

**Solid ("critical") pill:** white text on `interactive.danger.bg`, **only** for `breached` and `conflict`. Every other pill has the same size, weight 500 and shape (IB-21 equal salience). Ochre (lapse) is separate from amber (warning): about 14° of hue plus a different icon (`hourglass` vs `shield-alert`).

#### Status map (single source of truth: `<StatusPill entity state/>`; Lucide icons)

Semantic states (§3.9.9): `default` (none); `read-only` (`lock-keyhole` on hover, no border, **no** sunken bg, copy button); `disabled` (reason tooltip mandatory); `required` (`*` in `danger.fg` plus SR «υποχρεωτικό»); `error` danger `circle-alert`; `warning` `triangle-alert`; `info` `info`; `success` `circle-check`; `pending-approval` plum `hourglass`→`user-check` «Αναμένει έγκριση · {group}»; `locked` neutral `lock` «Κλειδωμένο από {όνομα}» plus avatar plus "since"; `conflict` **danger solid** `git-compare` «Σύγκρουση εκδόσεων» plus a Review banner; `stale` warning `refresh-cw` «Υπάρχουν νεότερα δεδομένα» plus a Refresh banner (never auto-reloads dirty forms); `offline/degraded` neutral `wifi-off`/`cloud-alert` (in the status bar); `AI-suggested` ai `sparkles` «Πρόταση ΤΝ» (1 px `gradient.iris` edge, never a fill); `AI-generated` ai outline `sparkle`; `adverse` danger text only `trending-down`/`arrow-down-right` (on figures, never a pill); `overdue` danger `clock-alert`; `breached` **danger solid** `alarm-clock-off`.

Entity states (§3.2.4), as family/icon:
- **Job:** Draft neutral `circle-dashed`, Quoted info `file-text`, Bound success `badge-check`, Withdrawn neutral `undo-2`, Declined danger `ban`, NotTaken neutral `circle-slash`, Expired neutral `timer-off`, Scheduled teal `calendar-clock`, Rescinded neutral `rotate-ccw`. Flags: Referred plum `flag`, Preempted warning `git-merge`.
- **PolicyTerm:** Scheduled teal `calendar-clock`, InForce success `shield-check`, PendingCancellation warning `shield-alert`, Cancelled danger `shield-x`, Expired neutral `shield-off`, NonRenewed neutral `shield-minus`, Lapsed ochre `hourglass`.
- **UWIssue:** Open warning `circle-alert`, Approved success, ApprovedWithConditions teal `list-checks`, Rejected danger `circle-x`, Invalidated ochre `circle-off`, Closed neutral `circle-minus`.
- **Claim/Exposure:** Draft, Open info `folder-open`, Closed neutral `folder-check`, Reopened info `folder-sync`.
- **TransactionSet:** Draft, Submitted plum `send`, PendingApproval plum `user-check`, Approved, Rejected, Posted teal `book-check`.
- **Invoice:** Planned neutral `calendar`, Billed info `receipt`, Due info `calendar-clock`, PartiallyPaid teal `circle-dot-dashed`, Paid success, Overdue danger `clock-alert`, WrittenOff neutral `eraser`, Reversed neutral `undo-2`.
- **Payment in:** Received info `arrow-down-left`, Allocated success `link`, PartiallyAllocated teal `link-2`, Suspense warning `inbox`, Reversed, Refunded neutral `arrow-up-right`.
- **Disbursement:** Requested neutral `file-plus`, PendingApproval plum, Approved success, Released teal `send`, Issued teal `landmark`, Cleared success `badge-check`, Rejected danger, Stopped warning `octagon-pause`, Voided neutral, Returned danger `corner-up-left`.
- **Activity:** Open info `circle`, Completed success, Skipped/Cancelled neutral.
- **Outbound document:** Requested `file-clock`, Rendering info `loader` (spins), Rendered success `file-check`, Failed danger `file-x`, Superseded neutral `file-minus`.
- **Delivery:** Pending/Sent/Delivered/Failed/Bounced = neutral/info/success/danger/danger, icons `clock`/`send`/`mail-check`/`mail-x`/`mail-warning`.
- **FiscalDocument:** Pending, Submitted plum, Registered (MARK) success `stamp`, Rejected, Cancelled.
- **ClockInstance:** Running info `timer`, Paused neutral `pause`, Warned warning `alarm-clock`, Met success `alarm-clock-check`, **Breached danger solid** `alarm-clock-off`, Cancelled.
- **Complaint:** Received/Acknowledged info, UnderInvestigation plum `search`, Answered success `message-square-check`, Escalated danger `arrow-up-circle`, Closed.
- **Cession:** Calculated info `calculator`, Exception warning, Posted teal, Reversed.
- **ProductVersion:** Draft/Submitted/Approved/Locked/Retired = neutral/plum/success/**brand**/neutral.
- **AI recommendation:** Proposed ai, Accepted/Edited success, Rejected/Expired neutral.
- **ScreeningResult:** Clear success `shield-check`, PotentialHit warning `scan-search`, FalsePositive neutral `shield-question`, TrueMatch **danger solid** `shield-ban`. (Note: TrueMatch adds a third solid use beyond "breached + conflict". Flag it to design.)

Each state has a GR and an EN label in Part 1 §2.1.4. Labels marked † await ROLE-46 glossary confirmation. Brief shorthand: quoted=info, bound=success, lapsed=ochre, cancelled=danger, in-review=plum.

### 5.4 Gradients ("a gradient is signal: an unlisted use is a bug"; OKLCH interpolation with sRGB fallback stops)

| Token | Light / Dark | Allowed only on |
|---|---|---|
| `gradient.cta` | `linear-gradient(120deg,#8250F2 0%,#3F63F0 50%,#14988D 100%)` / `#9B74FC,#6283FA,#2BB5A8` | The 1.5 px border of every **primary** button (`border-box` behind a surface `padding-box`). The resting glow only on the one commit CTA per view. Never on fills, text, secondary buttons, toolbars or rows. |
| `gradient.iris` (AI) | `linear-gradient(135deg,#8250F2,#3F63F0)` / `#9B74FC,#6283FA` | 1 px edge of AI-suggested cards, the `sparkles` fill, the AI caret, the IB-06 running step. Hidden when AI is killed. |
| `gradient.tide` (progress) | `linear-gradient(90deg,#14988D,#3F63F0)` / `#2BB5A8,#6283FA` | Determinate progress only (indeterminate uses `brand.solid`) |
| `gradient.laurel` (milestone) | `linear-gradient(135deg,#147D4F,#0E7A72)` / `#1E9A62,#14988D` | Signature moments only |
| `gradient.horizon.{positive,adverse,neutral}` | Brand/red/neutral rgba at 0.18/0.16/0.14 → 0 (dark 0.28/0.24/0.20) | Area under KPI sparklines and hero trends. Not multi-series. |
| `gradient.ambient` | Two radial blooms (aegean-100 and teal-100 tints, dark aegean-900 and teal-900) on the canvas | Shell canvas when imagery is off, low tier, or reduced transparency |
| `gradient.specular` | `linear-gradient(180deg,rgba(255,255,255,.50) 0%,transparent 42%)` (dark .08) | Top highlight of glass materials only |

Gradients never encode magnitude: a favourable KPI does not get a "nicer" gradient.

### 5.5 Typography

**Families**
- **UI and body: Inter 4.1 variable** (`wght` 100–900, `opsz` 14–32). Reasons: Greek and Greek Extended, true `tnum`, an optical-size axis, ClearType hinting at 13 px.
- **Polytonic fallback: Noto Sans variable 2.013+** (x-height within ±3% of Inter).
- **Mono: JetBrains Mono 2.304** (Greek, distinct 0/O and 1/l/I) with fallback **Noto Sans Mono** for polytonic.
- All are SIL OFL, so there is no licence conflict.

```css
--font-sans: "Inter", "Noto Sans", system-ui, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
--font-mono: "JetBrains Mono", "Noto Sans Mono", ui-monospace, "Cascadia Mono", Consolas, monospace;
```

**Delivery**
- **Self-host WOFF2 only.** No font CDN (GDPR; LG München I 2022).
- 4 `unicode-range` subsets per family: `latin` (U+0000–00FF, U+0131, U+0152–0153, U+02BB–02BC, U+02C6, U+02DA, U+02DC, U+2000–206F, U+20AC, U+2122, U+2190–21FF, U+2212, U+2215), `latin-ext` (U+0100–024F, U+1E00–1EFF), `greek` (U+0370–03FF), `greek-ext` (U+1F00–1FFF).
- Preload only `inter-latin.woff2` and `inter-greek.woff2`. `font-display: swap` (body), `optional` (mono).
- **Mandatory CI check:** a fontTools script asserts that the shipped subsets cover U+0370–03FF and U+1F00–1FFF (assigned codepoints). The build fails if a codepoint is missing from both Inter and Noto Sans.
- Fallback metrics to avoid layout shift: `@font-face{font-family:"Inter Fallback";src:local("Arial");size-adjust:107%;ascent-override:90%;descent-override:22%;line-gap-override:0%}`. These are provisional; tune them with Capsize against the shipped build.

**Numerals:** tables, KPIs, amounts and dates use `tabular-nums lining-nums`. Running text uses `proportional-nums lining-nums`. Policy, claim and invoice numbers, VIN, IBAN and ΑΦΜ use mono with `tabular-nums slashed-zero`, display grouped with thin spaces, and copy ungrouped. Percentages in tables use `tabular-nums` with fixed decimals per column.

**Scale** (px size/line height, weight, tracking, opsz). The CSS shorthand is `--type-*`.

| Token | Spec | Use |
|---|---|---|
| `type.display.xl` | 40/48, 600, −0.022em, 32 | Hero metric, signature headline |
| `type.display` | 32/40, 600, −0.019em | Home greeting, empty-state headline, statement balance |
| `type.heading.1` | 24/32, 600, −0.014em | Page title (comfortable record header) |
| `type.heading.2` | 20/28, 600, −0.010em | Compact record title, modal title |
| `type.heading.3` | 16/24, 600, −0.006em | Section and card titles |
| `type.heading.4` | 14/20, 600, −0.003em | Sub-sections |
| `type.body.lg` | 15/24, 400 | Portal body (comfortable); touch inputs 16/24 |
| `type.body` | 14/22, 400 | Comfortable default body |
| `type.body.compact` | 13/20, 400 | Compact default; table cells |
| `type.label` | 13/16, 500 | Field labels, compact button text, pills |
| `type.label.lg` | 14/20, 500 | Comfortable buttons, tabs |
| `type.caption` | 12/16, 400, +0.005em | Helper, timestamps, footers |
| `type.overline` | 11/16, 600, +0.06em, uppercase | Nav group labels and eyebrows. Never decision-critical. |
| `type.mono` / `type.mono.sm` | 13/20 and 12/16, 450 | IDs; IB-29 technical columns |
| `type.numeric.kpi` | 28/36, 600, −0.016em | KPI value |

Rules: minimum 12 px (11 px only for overline). Nothing actionable below 13 px. Weights used: 400, 450 (mono), 500, 600. **Never 700** (Inter Greek at 700 closes the counters of θ, φ and ψ at 13 px). Body line height ≥ 1.4×. Line heights are absolute px on the 4 px grid. Prose max 72 ch.

**Greek casing:**
1. Sentence case everywhere.
2. All-caps only for overlines and the environment ribbon.
3. Tonos dropped in caps.
4. Add a dialytika when the dropped tonos marked a non-diphthong (Μάιος→ΜΑΪΟΣ, άυλος→ΑΫΛΟΣ, keep the existing one in πρωτεΐνη→ΠΡΩΤΕΪΝΗ).
5. Final sigma: Σ when uppercasing; ς at word end when lowercasing; always use `toLocale*Case('el-GR')`.
6. Polytonic caps drop all breathings and accents; ypogegrammeni → adscript Ι (ᾠδή→ΩΙΔΗ).
7. **`toGreekUpper()`**, not CSS `text-transform`. Algorithm: NFD → flag a vowel with U+0301/U+0342 followed by ι/υ → remove U+0300, 0301, 0313, 0314, 0342, and 0345→Ι → `toLocaleUpperCase('el-GR')` → insert U+0308 on the flagged ι/υ → NFC. Mandatory unit tests: the 6 cases plus ευρώ→ΕΥΡΩ, ρολόι→ΡΟΛΟΪ, αϋπνία→ΑΫΠΝΙΑ.
8. Set `lang="el"`/`"en"` on the root and on mixed spans.
9. `hyphens: manual` in chrome and tables; `auto` only in long-form text.

**Rendering:** `text-rendering: optimizeLegibility; font-optical-sizing: auto; font-feature-settings: "kern" 1, "calt" 1;` and antialiased smoothing (macOS only). Dark theme: `type.body.compact` at `wght` 420 (not lighter).

### 5.6 Spacing (4 px base; no raw values)

`space.0`=0, `px`=1, `0_5`=2, `1`=4, `1_5`=6, `2`=8, `3`=12, `4`=16, `5`=20, `6`=24, `8`=32, `10`=40, `12`=48, `16`=64, `20`=80.
Density aliases: `space.inset.control-x`, `space.inset.cell-x`, `space.inset.cell-y`, `space.stack.field`, `space.stack.section`, `space.inset.card` (values in §5.10).

### 5.7 Radius and borders

`radius.none` 0 (cells) · `xs` 2 (diff highlights, inline code) · `sm` 4 (checkbox, compact pill, tag, kbd, tooltip) · `md` 6 (buttons, inputs, selects, segmented) · `lg` 10 (cards, KPI, sheets, side panel, popovers, menus) · `xl` 14 (modals, drawers, toasts, floating action bar) · `2xl` 20 (palette, login card, signature card) · `full` 9999 (avatars, comfortable pills, switch, presence dots).

Nested rule: inner = outer − padding, minimum 2 px.

Borders: `border.width.hairline` 1 px; `border.width.thick` 2 px (focus, error, selected indicator, active tab). Focus ring: `outline: 2px solid var(--color-border-focus); outline-offset: 2px`; on glass, add `box-shadow: 0 0 0 4px var(--color-surface-sheet)`. Table row dividers only: `scaleY(.5)` at DPR ≥ 2.

### 5.8 Elevation and glass

**Shadows** (light; dark adds an inset rim):
- `elevation.0` none (sheets)
- `elevation.1` `0 1px 2px rgba(15,23,42,.06), 0 1px 1px rgba(15,23,42,.04)` (cards and KPI on canvas)
- `elevation.2` (+ `0 4px 12px .06`; card hover, scrolled sticky header, pinned edge)
- `elevation.3` (`0 2px 4px .06, 0 12px 32px .12`; popovers, menus, combobox lists, date picker)
- `elevation.4` (`0 4px 8px .08, 0 24px 64px .20`; modals, drawers, side sheets, toasts)
- `elevation.5` (`0 8px 16px .10, 0 32px 96px .28`; palette, signature card)
- `elevation.pinned-column` `4px 0 8px -4px rgba(15,23,42,.12)` (only when scrolled)

**Glass build order:** (1) `backdrop-filter: blur() saturate()`; (2) tint rgba; (3) specular `::before` with `pointer-events:none`; (4) 1 px inset rim light; (5) 1 px outer hairline; plus elevation and an optional 2% noise texture.

| Material | Used by | Blur / Sat | Tint L / D | Rim L / D | Hairline L / D | Specular | Elevation | Noise |
|---|---|---|---|---|---|---|---|---|
| `material.chrome` | Rail, top bar, status bar, shell-level wizard footer, bottom tab bar | 24 / 180% | `rgba(248,249,251,.80)` / `rgba(18,21,27,.84)` | .60 / .07 white | `rgba(15,23,42,.08)` / `rgba(0,0,0,.50)` | yes | none; top bar elev.2 when scrolled | 2% |
| `material.overlay` | Modals, palette, floating side sheet, signature card, drawers | 32 / 160% | `rgba(255,255,255,.86)` / `rgba(22,26,33,.86)` | .70 / .08 | .10 / .55 | yes | 4 / 5 | 2% |
| `material.popover` | Popovers, menus, listboxes, date picker, quick-look | 20 / 150% | `rgba(255,255,255,.90)` / `rgba(26,30,38,.90)` | .75 / .08 | .10 / .55 | 50% strength | 3 | none |
| `material.float` | Toasts, bulk-action bar, presence stack | 24 / 170% | `rgba(255,255,255,.90)` / `rgba(31,36,45,.90)` | .70 / .08 | .10 / .55 | yes | 4 | none |
| `material.*.solid` (fallback) | — | none | chrome `#F8F9FB`/`#12151B`; overlay `#FFFFFF`/`#161A21`; popover `#FFFFFF`/`#1A1E26`; float `#FFFFFF`/`#1F242D` | same | same | removed | same | none |

**Never glass:** tables, forms, record bodies, KPI tiles, charts, **tooltips** (solid inverse), banners, the document viewer. **No glass on glass:** a popover inside a modal uses the solid popover material.

**Fallbacks to solid:** the user's "Reduce transparency" setting (source of truth; its default comes from `prefers-reduced-transparency`); `@supports not (backdrop-filter…)`; the Low tier; forced colours (no tint, blur or shadow; `1px solid CanvasText`; system colours); print (chrome hidden).

**Contrast on glass (worst case over 9 backdrops):** chrome secondary text is 4.64 (L) and the imagery scrim is 4.59 (L). So the **tint α values are minimums** (a lint rule), and **`text.tertiary` is banned on glass and imagery**.

**Imagery (§2.6.5):**
- Allowed: the canvas (gutters, behind chrome), login, the canopy band (240/200 px), empty-state frames (320×180, `radius.lg`). Never behind tables, forms, records, modal content, charts or KPIs.
- Art direction: abstract Greek light and material; no people, landmarks or claim scenes. 8 images per theme, rotated **per day**.
- Build-time processing: 640 px wide, Gaussian σ24, saturation ×1.15 (L) or ×0.85 with brightness ×0.70 (D), AVIF q50 plus WebP q60, ≤ 40 KB each, served immutable.
- Render as `<img>` with `object-fit: cover`, `decoding="async"`, `fetchpriority="low"`, `aria-hidden`, `alt=""`. **No runtime `filter: blur()`.**
- Scrim: L `rgba(246,247,249,.80)` plus a 96 px bottom fade to .94; D `rgba(14,17,22,.82)` fading to .94. Gutter scrim .66 / .72.
- Imagery is not requested under reduced transparency, the Low tier or `saveData`; use `gradient.ambient` instead.
- Ambient drift MI-70.
- Setting «Εικόνες φόντου»: default on in comfortable, **off in compact**.

### 5.9 Performance budget and device tiers

- 60 fps; no frame > 33 ms during motion (Long Animation Frames RUM plus a Playwright trace on a low-end VM).
- **INP < 200 ms p75** (target < 100 ms for feedback-tier interactions), measured with `web-vitals`.
- Screen ready ≤ 1.5 s p95. Palette results ≤ 300 ms p95.
- **Max 3 simultaneous `backdrop-filter` layers** (0 on Low).
- Animate only `transform`/`opacity` (plus `clip-path` in the signature tier). Shadows animate by fading a pseudo-element's opacity. Enforced by a lint rule.
- ≤ 4 ms of main-thread JS per frame during transitions (`scheduler.yield`/`requestIdleCallback`).
- **GlassBudget service:** every glass element registers on mount. Priority: modal/palette > popover > float > chrome. Layers beyond N get `data-material="solid"` instantly. A modal or palette turns the rail and top bar solid under the scrim. A toast during an open popover is solid. Toasts dock inside the bulk-bar glass container.
- Chrome is `position: fixed` over a static canvas. Scrolling content must not repaint the chrome (check with paint flashing).
- `content-visibility: auto; contain-intrinsic-size: auto 480px` on off-screen record sections. Tables over 200 rows virtualise.

**Tiers** (detected on first run, cached 7 days, re-evaluated if 3 sessions show > 5% long frames):
- **High:** ≥ 8 cores, deviceMemory ≥ 8 or unknown, not a software renderer, 120-frame probe p95 ≤ 16.7 ms. Full effects, including SM-08 refraction.
- **Standard:** everything else. Glass N = 3, imagery, signature moments capped at 12 nodes with no particles.
- **Low:** ≤ 4 cores, ≤ 4 GB, a software renderer, probe p95 > 22 ms, or battery < 0.2 and not charging. Glass 0, ambient gradient, transitions shortened to 150 ms opacity-only, signature moments use their reduced version.
- User override «Οπτικά εφέ»: Αυτόματα / Πλήρη / Ελαφριά.
- Thresholds are provisional.

### 5.10 Motion tokens

**Durations:** `instant` 0 · feedback `xs` 70 / `sm` 100 / `md` 120 / `lg` 150 · transition `sm` 200 / `md` 240 / `lg` 320 / `xl` 400 · `exit.factor` ×0.75 · signature `beat` 600 / `max` 1,600 · delays: tooltip 400 (0 when warm for 1,500 ms), skeleton 150, spinner 400 · skeleton min display 300 · list stagger 16 (max 8 items, 128 ms cap).

**Easings:**
- `standard` `cubic-bezier(0.2,0,0,1)`
- `enter` `(0.05,0.7,0.1,1)`
- `exit` `(0.3,0,0.8,0.15)`
- `feedback` `(0.25,0.1,0.25,1)`
- `linear`
- `emphasized` = `linear(0, 0.009 2.4%, 0.037 5%, 0.15 10.4%, 0.32 16.3%, 0.5 22.2%, 0.68 30%, 0.81 39%, 0.9 49%, 0.955 60%, 0.985 74%, 1)` (fallback `standard`)
- `anticipate` `(0.36,0,0.66,-0.2)` (signature only)
- CSS also has `--motion-ease-overshoot-fallback` `(0.34,1.56,0.64,1)`.

**Springs** (Motion for React; stiffness/damping/mass 1):

| Spring | Values | Settle | Used for | CSS fallback |
|---|---|---|---|---|
| `snappy` | 520/42 | ≈220 ms, no overshoot | Drag settle, resize, divider snap | 220 ms standard |
| `smooth` | 280/32 | ≈340 ms | Shared elements, bulk bar | 320 ms enter |
| `gentle` | 180/24 | ≈480 ms | Palette reflow | 400 ms enter |
| `expressive` | 320/18 | ≈600 ms, 12% overshoot | **Signature only** | 600 ms overshoot curve |

Settle times are analytical estimates; confirm them with a test harness.

**Global rules:**
1. Interruptible from the current value; no animation queues.
2. Input is never blocked: no `pointer-events: none` on content; focus moves at t = 0.
3. **Reduced motion** (OS setting or the app preference «Μείωση κίνησης», which is the source of truth): spatial movement becomes an opacity fade ≤ 120 ms or instant; signature moments show the static end state plus a 120 ms fade (the announcement is unchanged); tickers swap instantly; spinners still rotate at 1.2 s; progress still fills.
4. Hover is never required: everything hover reveals is also available on focus.
5. No motion on unchanged data when polling.

### 5.11 Z-index (components never invent values; one `#layer-root` portal)

`z.base` 0 · `raised` 1 · `sticky.table-header` 20 · `sticky.pinned-column` 25 · `sticky.table-corner` 30 · `sticky.record-header` 40 · `drag` 90 · `shell.rail` 100 · `shell.topbar` 110 · `shell.statusbar` 110 · `panel.context` 200 · `drawer` 300 · `dropdown` 400 · `float.action-bar` 450 · `scrim` 500 · `modal` 510 (+10 per stacked modal, max 2) · `modal.dropdown` 520 · `palette` 600/601 · `toast` 700 · `signature` 750 · `tooltip` 800 · `env-ribbon` 900 · `skip-link` 1000.

### 5.12 Density (`[data-density]` on `<html>`; touch via `(pointer: coarse)` raises minimums in either density)

| Token | Compact | Comfortable | Touch |
|---|---|---|---|
| `size.control.sm` | 24 | 28 | 40 |
| `size.control.md` | **28** | 36 | 44 |
| `size.control.lg` | 32 | 40 | 48 |
| `size.row.table` | **32** | 40 | 48 (card list below `bp.md`) |
| `size.row.table-dense` (IB-29, opt-in) | 28 | 32 | n/a (CSS 32) |
| `size.row.list` | 28 | 36 | 44 |
| `size.tab` | 36 | 44 | 48 |
| `size.topbar` / `size.rail` | 48 / 56 | 56 / 64 | 56 / 64 |
| `space.inset.control-x` | 8 | 12 | 16 |
| `space.inset.cell-x` / `cell-y` | 12 / 6 | 16 / 9 | 16 / 12 |
| `space.stack.field` / `section` | 12 / 24 | 20 / 32 | 20 / 24 |
| `space.inset.card` | 16 | 24 | 16 |
| Body | `type.body.compact` | `type.body` | `type.body.lg` (inputs 16/24 to stop iOS zoom) |
| Icons | 16 | 16 (20 in nav) | 20 |
| Label→field gap | 4 | 6 | 6 |

**Defaults by surface:** staff workbenches, queues, records, admin and wizards are **compact**. Staff Home and dashboards are comfortable. Broker portal: comfortable on desktop, touch on tablet and phone. Bank branch (ROLE-08): comfortable. Customer portal: comfortable, not switchable.

Per-table override from the table toolbar (IB-16). A density switch applies in one frame with no animation and preserves the scroll anchor.

---

## 6. Layout

### 6.1 Grid

- 4 px baseline. 12-column fluid grid.
- Gutter, sheet margin and section gap: compact 16/16/24; comfortable 24/24/32; touch 16/16/24.
- Forms use a 12-column subgrid with **semantic widths** (columns at ≥ 1240 / 905–1239 / < 905):
  - `field.xs` 2/3/6 (postcode, %, currency code)
  - `field.sm` 3/4/12 (dates, amounts, ΑΦΜ, plate)
  - `field.md` 4/6/12 (names, phone, selects)
  - `field.lg` 6/12/12 (email, address, IBAN)
  - `field.full` 12
- **Labels above fields everywhere** ⚖. The only exception is the read-only key–value list (label column `minmax(140px, 40%)`, wraps to 2 lines).

### 6.2 Breakpoints (media queries for the shell only; `@container` for components)

`bp.xs` 0–599 (phones; brokers and customers) · `bp.sm` 600–904 (tablet portrait; field assessors) · `bp.md` 905–1239 (tablet landscape, split windows) · `bp.lg` 1240–1439 (1280/1366 laptops; staff minimum **1280×720**) · `bp.xl` 1440–1919 (**design reference 1440×900**) · `bp.2xl` ≥ 1920.

Reference frames: 1440×900, 1280×720, 1024×768, 834×1112, 390×844.

Must work at 200% zoom at 1280 px (so `bp.sm` behaviour must be complete). Reflow at 320 px for everything except data tables, which must offer a card list. Must survive WCAG 1.4.12 text-spacing overrides (no fixed heights on text; rows use `min-height`).

### 6.3 App shell (staff)

```
env ribbon 24 (non-prod only, solid)
┌rail 56┬ top bar 48 (chrome): [Entity ▾][Breadcrumb/title][⌘K search 280–480][🔔][?][👤] ┐
│       ├ work views 240 ┬ MAIN SHEET (opaque, radius.lg, 8 px inset) ┬ context panel 360 ┤
│       │ (opaque canvas)│ record header 72→48 sticky / tabs 36 / body│ (opaque sheet)     │
└───────┴ status bar 24 (chrome): entity · env · connectivity · AI state · 14:32 EET · jobs ┘
```

| Region | Compact / Comfortable | Material | Behaviour |
|---|---|---|---|
| Env ribbon | 24 | Solid | Non-prod only |
| Top bar | 48 / 56 | chrome | Fixed. Entity switcher (only with > 1 entity), breadcrumb or title (appears when the record header scrolls away), palette trigger (280–480 px, centred), bell with count, help, user menu with presence |
| Rail collapsed | 56 / 64 | chrome | 20 px icons in 40×40 hit areas; tooltips to the right; role-configured order |
| Rail expanded | 240 / 256 | chrome | Toggle with `[`. Overlays at ≤ `bp.lg`, pushes at ≥ `bp.xl`. Remembered per user. |
| Work-views sidebar (IB-03) | 240 (200–320 resizable) / 256 | **Opaque canvas** | Workbenches only. Saved views and queues with live `tabular-nums` counts. Toggle with `Shift+[`. |
| Main sheet | fluid, min 560 / 600 | `surface.sheet`, `radius.lg`, inset 8 / 12 | **The only content scroll container** |
| Context panel | 360 (320–560, collapses to a 40 px tab strip) / 400 (360–600) | Opaque sheet | Tabs: Assistant, Activity, Notes, Documents, Presence & comments. Pushes at ≥ 1440. Overlay side sheet (`material.overlay`, no scrim) at 1240–1439. Drawer below 1240. |
| Status bar | 24 / 28 | chrome | `type.caption`: entity, env, connectivity, AI state («ΤΝ απενεργοποιημένη» when killed), Europe/Athens date and time with EET/EEST, background job count |

**Split pane (IB-05):**
- List pane 420 / 460 px, resizable 320–640.
- Divider: `role="separator"` with `aria-valuenow`. ←/→ move 16 px, Shift 64 px, Home/End go to min/max. 1 px `border.subtle` with an 8 px hit area; hover or focus shows a 2 px `border.focus` line.
- Detail pane min 560 px; below that the layout stacks (opening an item pushes the detail over the list with a shared-element transition; `Esc` or `Alt+←` goes back).
- J/K drives the detail from cache within 1 frame.

**Record view:**
- Header 72 / 96 px: type overline, `type.heading.2` title, mono ID, status pills, 3–5 key facts, presence avatars, max 2 actions plus ⋯.
- On scroll past 72 px it collapses to 48 px: the title moves to the top bar via a 200 ms shared element; status and actions stay visible.
- Tabs 36 / 44 px, with overflow into «Περισσότερα ▾» (never scroll).
- Body: one column up to 1200 px, or 8 + 4 columns. Collapsible sections remembered per user.

**Wizard:**
- Stepper: vertical on the left at ≥ 1240 (220 px wide, 40 px steps); horizontal at the top below that (48 px, current ± 1 plus a "3 / 7" counter).
- Body max 960 px (forms) or full width (risk tables).
- Sticky summary 320 px at ≥ 1440 (running premium with **no ticker**, UW issue count, documents); below 1440 it becomes a 56 px bottom bar.
- **Footer bar** 56 / 64 px, sticky, **opaque**: «Αποθήκευση πρόχειρου» plus last-saved time on the left; Πίσω / Επόμενο / the commit CTA on the right.

**Dashboard / Home:**
- Canopy 240 / 200 with imagery and scrim: greeting (`type.display` / `type.heading.1`, «Τρίτη 7 Οκτωβρίου») plus 1 hero metric.
- Global filter bar 48 px, sticky, opaque (IB-26).
- Card grid: KPI tiles span 3 / 4 / 6 / 12 columns at ≥ 1440 / 1240 / 905 / < 905. Work-left cards span 6 or 12.

**Environment ribbon:**
- UAT/PREPROD: solid `#8A4A0B` with white text (6.85:1), «ΠΕΡΙΒΑΛΛΟΝ ΔΟΚΙΜΩΝ · UAT · Τα δεδομένα δεν είναι πραγματικά».
- DEV/TEST/SANDBOX: `#6F36DE` (6.44:1).
- A shifted clock adds «· Ημερομηνία συστήματος: 15/01/2027» in bold, and the status bar clock turns warning.
- The favicon and title prefix change («[UAT] …»).
- Never glass, never dismissible, not affected by reduce-transparency.

**Landmarks and skip link:**
- Order: skip link «Μετάβαση στο περιεχόμενο» → `header role=banner` → `nav "Κύρια πλοήγηση"` → `nav "Προβολές εργασίας"` → `main` → `aside "Πλαίσιο"` → `footer role=contentinfo`.
- **F6 / Shift+F6** cycle between regions.

### 6.4 Responsive shell

| Breakpoint | Nav | Work views | Context panel | Top bar | Status bar |
|---|---|---|---|---|---|
| ≥ 1920 | Rail collapsed; expanded pushes | Visible, pushes | Visible, pushes, 400 | Full | Full |
| 1440–1919 | Same | Visible | Visible, 360 | Full | Full |
| 1240–1439 | Expanded overlays | 200 px | Overlay side sheet, closed by default, opened with **`Ctrl+.`** | Full | Full |
| 905–1239 | Overlays | «Προβολές ▾» dropdown | Full-height drawer | Search becomes an icon | Condensed |
| 600–904 | **Bottom tab bar** 56 px + safe area, ≤ 5 destinations plus «Περισσότερα» | Dropdown | Bottom sheet (snaps at 50% and 92%) | 56 px | Hidden (offline shows as a top banner) |
| < 600 | Bottom tab bar | Dropdown | Bottom sheet | 56 px | Hidden |

**Content by breakpoint:**
- Tables: full at ≥ 1240; at 905–1239 with the first column pinned and horizontal scroll; priority columns 1–2 plus expand at 600–904; **card list** below 600 (long-press 500 ms or an «Επιλογή» mode to select).
- Split pane stacks when the detail would be under 560 px.
- Stepper: vertical → horizontal → counter plus progress bar.
- Forms: single column below 905.
- Record header key facts wrap, then collapse into «Λεπτομέρειες».
- Modals: centred → bottom sheet (92%) → full screen.
- KPI tiles: 4 → 2–3 → 2 → 1 per row (or a scroll-snap carousel on Home).

**Brokers on tablet and phone:** quote, referral responses, policy search and summary, documents, payments and FNOL must all be fully usable. Commission statements and agency admin are desktop-first, with a read-only summary and the hint «Άνοιγμα σε υπολογιστή» (the hint never hides data).

Both orientations must work. A hardware keyboard enables shortcuts. Under `(hover: none)`, hover-only affordances become always visible (for example row ⋯).

**Print:** chrome hidden, pills printed with text and icon, link URLs appended, footer «Εκτύπωση οθόνης — όχι επίσημο έγγραφο · {user} · {timestamp}».

### 6.5 User appearance settings (persisted to the profile, PLT)

| Setting | Options (default first) |
|---|---|
| Language | Ελληνικά, English |
| Region format | el-GR (independent of language) |
| Theme | Αυτόματο, Φωτεινό, Σκοτεινό (`[data-theme]`) |
| Density | Per surface |
| Reduce motion | Αυτόματο, Ναι, Όχι |
| Reduce transparency | Αυτόματο, Ναι, Όχι |
| Background images | On in comfortable, off in compact |
| Visual effects tier | Αυτόματο, Πλήρη, Ελαφριά |
| Celebrations (Εορτασμοί) | Ναι |
| Single-key shortcuts | Ναι (WCAG 2.1.4: must be switchable off) |

---

## 7. Components (Part 2 §4): every one, with variants, states, keyboard, ARIA and the React Aria fit

### 7.0 Conventions

- **8-state vocabulary**, required for every interactive component ("n/a" must give a reason):
  - **default**
  - **hover**: `(hover: hover)` only; one bg step; MI-01
  - **focus-visible**: 2 px `border.focus` ring, offset 2; MI-03; never removed
  - **active**: `*.bg-pressed`; scale 0.97 on buttons only; MI-02
  - **disabled**: `text.disabled` + `interactive.disabled.bg` + `not-allowed`; **stays focusable with `aria-disabled`** when there is a reason, shown in a tooltip and `aria-describedby`
  - **loading**: a same-size spinner replaces the content, width locked, `aria-busy`, label kept for screen readers
  - **error**: 2 px `status.danger.solid` + `circle-alert` + message + `aria-invalid` + `aria-describedby`
  - **read-only**: value as text, no chrome, copy button on hover or focus, `aria-readonly` if a control is kept
- **Sizes:** `sm` / `md` / `lg` map to `size.control.*`; `(pointer: coarse)` raises them to the touch minimum.
- **Library rule:** one primitive library only (React Aria). Missing primitives are built on React Aria hooks (`useMove`, `useKeyboard`, …).
- **Commit CTA keyboard:** `Ctrl/⌘+Enter` from anywhere in the wizard step.

### 7.1 Button (§4.1) — RAC `Button`, `ToggleButton`; split button = `Button` + `MenuTrigger`/`Menu`; group = `Group`

**Anatomy:** container → [16 px leading icon] → label → [trailing icon or kbd] → [spinner, which replaces the content].

| Variant | Spec | Limit |
|---|---|---|
| `primary` | Surface fill + 1.5 px gradient border (`interactive.primary.border-*`) + `0 1px 2px rgba(15,23,42,.06)`; label `text.on-primary` 600 | 1 per region |
| `primary-gradient` (commit) | Same, plus a **resting glow** `0 1px 2px rgba(15,23,42,.06), 0 4px 14px -6px rgba(63,99,240,.45)` (D .50). For Δέσμευση, Έκδοση, Υποβολή αναγγελίας, Έγκριση πληρωμής, Κλείσιμο περιόδου | **1 per view** |
| `secondary` | `interactive.secondary.bg`, `text.primary`, 1 px `border.control` | — |
| `ghost` | Transparent | Toolbars, rows |
| `danger` | `interactive.danger.bg`, white | **Only inside a confirmation dialog**, 1 per dialog |
| `danger-ghost` | `status.danger.fg` text | Destructive entry points (opens the confirmation) |
| `link` | `text.link`, underlined | Inline |
| `ai` | `status.ai.bg`, `status.ai.fg`, 1 px `gradient.iris` | Accept AI suggestion, AI entry points |

**Sizes** (height Compact / Comfortable / Touch, padding-x, text, icon; all `radius.md`):
- `sm`: 24/28/40, padding 8, `type.label`, 14 px icon, min width 24 (icon-only)
- `md`: 28/36/44, padding 10/12/16, `type.label` / `label.lg`, 16 px icon, min width 64
- `lg`: 32/40/48, padding 12/16/20, `type.label.lg`, 16 px icon, min width 88

Icon-only buttons are square and always have `aria-label` plus a tooltip with the label and shortcut.

**States:**
- **hover:** fill `#F7F9FF` / D `#1A2030` + glow `0 6px 20px -8px rgba(63,99,240,.55)` (D `rgba(98,131,250,.60)`). Secondary `#F4F5F7` / `#232831`; ghost `ghost.bg-hover`.
- **focus:** ring; the commit button adds `0 0 0 4px sheet` under the ring.
- **active:** `#EEF3FF` / `#1D2540`, scale 0.97.
- **disabled:** primary uses `interactive.disabled.bg` + `text.disabled` + a reason tooltip. Commit **flattens**: no gradient, no glow, 1.5 px `border.subtle`, plus a tooltip giving the exact blocker («Δεν μπορεί να γίνει δέσμευση: 2 ανοιχτά ζητήματα ανάληψης») with a link to the first blocker. Never hide the CTA.
- **loading:** 16 px spinner, gradient border stays, width locked, `aria-busy`, clicks ignored; the commit sweep pauses.
- **error:** n/a.
- **read-only:** not rendered without permission; shown disabled only if a state change could enable it.

**Behaviours:**
- MI-67 sweep when the commit button becomes enabled.
- Shortcut kbd chip inside `lg` buttons; for other sizes it appears after holding Ctrl/⌘ for 600 ms (MI-17).
- **Maker-checker labels:** «Αποστολή για έγκριση» with a mandatory `user-check` icon.
- **Double-submit guard:** loading within 1 frame, repeat clicks ignored, and an **idempotency key** generated on the first click (contract §3.5.3).
- **Split button:** two focus stops; `Alt+↓` opens the menu (divider colour: see §0.4).
- **Button group:** joined secondaries for zoom and pagination; never for exclusive choice.

**Keyboard and ARIA:** Enter/Space; native `<button>`; toggles use `aria-pressed`; menu buttons use `aria-haspopup="menu"` + `aria-expanded`. **Motion:** MI-01, 02, 03, 17, 67.

**Copy:** verb + object, sentence case («Υποβολή αναγγελίας ζημίας»). Never «ΟΚ» or caps.

### 7.2 Text input and FormField (§4.2) — RAC `TextField` + `Label` + `Input`/`TextArea` + `Text slot="description"` + `FieldError`; search = `SearchField`

**FormField anatomy:** label (`type.label`, `text.secondary`) + `*` → optional `circle-help` popover → control → helper **or** error (`type.caption`) → optional character count.

**Control anatomy:** leading adornment (icon or prefix) → input → trailing adornment (unit, clear, status icon, AI badge).

**Variants:** text, email, tel (+30 prefix, groups «69x xxx xxxx»), url, search (`Esc` clears, a second `Esc` blurs), password (n/a: Entra handles sign-in), textarea (auto-grows from 3 to 12 rows, vertical resize only), identifier (§7.7).

**Sizes:** text `body.compact` / `body` / 16 px; padding-x 8 / 12 / 12; `radius.md`.

**States:**
- **default:** sheet bg, 1 px `border.control`.
- **hover:** `border.control-hover` (100 ms).
- **focus:** 1 px `border.focus` + `0 0 0 3px` `color.state.focus-halo`, shown on **any focus**, not only `:focus-visible`.
- **disabled:** disabled bg + `border.subtle` + reason.
- **loading:** async lookup (e.g. ΑΦΜ); 14 px trailing spinner after 400 ms; `aria-busy`.
- **error:** 2 px danger border (1 px border + 1 px inset shadow, so there is no shift); danger halo `rgba(209,36,36,.22)`; trailing `circle-alert`; message in `danger.fg`; MI-14.
- **warning (soft):** 1 px `warning.solid` border + message; does not block.
- **read-only:** no border or bg; padding kept so columns align; copy button (MI-10); masked P2/P3 values («•••• 4471» with an eye icon to reveal, and the reveal is audited).
- **AI-suggested:** 1 px `gradient.iris` border + trailing `sparkles` badge; value in `status.ai.fg` italic until accepted; popover shows source and confidence. Tab to the badge, then Enter accepts and Del rejects.

**Validation timing:** on blur for format errors; on change once an error is showing; on submit, show all errors, focus the first (MI-14 shake) and show the error-summary banner.

**ARIA:** `<label for>`, `aria-required`, SR text «υποχρεωτικό», `aria-describedby` (error first). Character count is `aria-live="polite"`, at most once per second and only when fewer than 20 characters remain.

**Keyboard:** `Ctrl/⌘+Enter` in a textarea submits.

**Do:** put the format in the helper text («ηη/μμ/εεεε»), wrap labels to 2 lines (never ellipsis), and show the source of prefilled values («Από gov.gr Wallet»).

**Motion:** MI-01, 03, 09, 10, 14.

### 7.3 Select (§4.3) — RAC `Select` + `Button` + `SelectValue` + `Popover` + `ListBox`/`ListBoxItem`/`ListBoxSection`/`Header`

**Choosing the control:** ≤ 7 options with no search → Select. 2–4 always-visible options → radio or segmented. > 7, or any reference-data list → Combobox.

**Anatomy:** trigger (value + 16 px `chevron-down`) → listbox in `material.popover`, `radius.lg`, `elevation.3` → options (check icon on the selected one, optional description, group headers).

**States:**
- placeholder «Επιλέξτε…»
- open: chevron rotates 180° (150 ms) + MI-28
- loading: spinner after 400 ms + 3 skeleton rows
- error, disabled, read-only (plain text) as for inputs

**Listbox:** max height 320 / 384 px; option height `size.row.list`. A **sliding highlight pill** (`ghost.bg-hover`, `radius.sm`, 4 px inset; MI-19). Selected option: check + weight 500. Typeahead is accent- and case-insensitive.

**Keyboard:** Space/Enter/↓ open; ↑↓; Home/End; typeahead; Enter selects; Esc closes; **Tab selects the highlighted option** (RA default).

**ARIA:** button + listbox + option, `aria-selected`.

**Data rules:** order by frequency or Greek collation `el`, never by code; show «03 · Οδική βοήθεια» for regulated code lists.

**Motion:** MI-19, 28.

### 7.4 Combobox (§4.4) — RAC `ComboBox` + `Input` + `Popover` + `ListBox`; async via `useAsyncList`; multi = `TagGroup` + `ComboBox` (custom); custom `filter`

**Anatomy:** optional leading `search` → clear + chevron → grouped results. Each option: primary text with **match highlighting** (600 weight + `status.brand.bg` underlay), a secondary `type.caption` `text.tertiary` line, and optional right-aligned meta (pill or mono ID). Optional «Δημιουργία νέου προσώπου…» footer.

**Variants:**
- `single`
- `multi`: chips in the field, max 3 lines then «+5»; chip `radius.sm`, 20 / 24 px tall, `status.neutral.bg`, 16 px ×
- `async`: debounce 150 ms, min 2 characters (1 for numeric IDs)
- `creatable`

**Matching:** NFD-strip U+0300–036F, `toLocaleLowerCase('el')`, ς≡σ; **Greeklish via ELOT 743 both ways** («papad»↔«Παπαδόπουλος»); numeric prefixes match IDs («4471» → «ΑΣΦ-2026-004471»).

**States:**
- loading: 2 px indeterminate `brand.solid` bar after 150 ms; old results dimmed to 60%
- empty: «Δεν βρέθηκαν αποτελέσματα για "{query}"» + create + «Αναζήτηση σε όλο το σύστημα (Ctrl+K)»
- error: «Η αναζήτηση απέτυχε. Δοκιμάστε ξανά.» + Retry; the query is kept
- Backspace on an empty input removes the last chip

**Keyboard:** typing opens; ↑↓; Enter; Esc closes, a second Esc clears; Alt+↓ opens. In multi mode, ← from the start of the input walks the chips and Delete removes one.

**ARIA:** `role=combobox`, `aria-expanded`, `aria-controls`, `aria-activedescendant`, `aria-autocomplete=list`. The count is announced politely («12 αποτελέσματα»).

**Rules:** secondary lines must disambiguate namesakes (birth year, masked last 3 digits of the ΑΦΜ, town); never show the full ΑΦΜ without P2. **Keep the typed text on blur** and warn «Δεν επιλέχθηκε τιμή».

**Motion:** MI-19, 25, 28, 45.

### 7.5 Date picker, range and date-time (§4.5) — RAC `DatePicker`/`DateRangePicker` + `DateField`/`DateInput`/`DateSegment` + `Calendar`/`RangeCalendar`/`CalendarGrid`/`CalendarCell`, inside `I18nProvider locale="el-GR"`; date-time = `granularity="minute"`, `hourCycle={24}`

**Anatomy:** segmented field `ηη/μμ/εεεε` → `calendar` icon button → popover calendar: header «Οκτώβριος 2026» (nominative) with ‹ › and a month/year quick picker on title click → weekday row **Δε Τρ Τε Πε Πα Σα Κυ (Monday first)** → 6×7 grid → footer quick picks.

**Locale:**
- Field format dd/mm/yyyy.
- Long display «Τρίτη, 7 Οκτωβρίου 2026" (genitive month, via `Intl.DateTimeFormat('el-GR',{month:'long',day:'numeric'})`).
- 24-hour time, Europe/Athens; show EET/EEST only where cross-zone confusion is possible.
- Holidays from the PLT calendar (CD-17): 4 px `status.info.solid` dot plus a tooltip. Weekends in `text.tertiary`.
- Business-day footer «+10 εργάσιμες: 21/10/2026».

**Accelerators** (parsed on blur or Enter, with a live preview line «→ Πέμπτη, 6 Νοεμβρίου 2026»): `7/10`, `7.10.26`, `07102026`; `σ`/`σήμερα`/`t`/`today`; `α`/`αύριο`; `+30`/`-7`; `+1ε`/`+1y`, `+6μ`/`+6m`; `τμ`/`eom`. **This needs custom work** (see §0.3).

**Quick picks:**
- Effective date: Σήμερα, Αύριο, 1η επόμενου μήνα, Λήξη τρέχουσας περιόδου.
- Ranges: Τελευταίες 7 ημέρες, Τρέχων μήνας, Προηγούμενο τρίμηνο, Από αρχή έτους.

**Day states:**
- today: 1 px `border.focus` ring, no fill
- selected: `interactive.accent.bg`, white, `radius.md`
- hover: `ghost.bg-hover`
- in range: `state.row-selected`
- unavailable: `text.disabled` with strikethrough + a reason tooltip («Εκτός επιτρεπόμενου διαστήματος αναδρομικότητας: έως 30 ημέρες»)
- field states as for inputs; loading n/a; read-only = long format

**Keyboard:**
- Field: ↑↓ change the segment, typing auto-advances, Alt+↓ opens.
- Grid: arrows move by day, PgUp/PgDn by month, Shift+PgUp/PgDn by year, Home/End to week bounds, Enter selects, Esc closes.

**ARIA:** `role=grid`; cells carry full labels («Τρίτη 7 Οκτωβρίου 2026, αργία»).

**Do:** use segments so birth-date entry never needs the calendar.

**Motion:** MI-28, 44, 14.

### 7.6 Currency and percentage (§4.6) — currency: custom on `useNumberField` or `TextField` (see §0.3); percentage: RAC `NumberField` with `formatOptions={{style:'percent'}}`

**Currency:**
- Value right-aligned with `tabular-nums`, «1.234,56 €» (NBSP before €); optional currency selector chip (multi-currency); amount in words on payment approvals («Δώδεκα χιλιάδες τετρακόσια ογδόντα ευρώ»).
- Format follows the **region format**, not the language (en-GB: «€1,234.56»).
- Live grouping with the caret preserved.
- Both `,` and the numpad `.` type the decimal separator in el-GR.
- Paste rules, in order: (1) `,` + 1–2 trailing digits = decimal; (2) `.` + exactly 3 digits = grouping; (3) a single `.` + 1–2 trailing digits = decimal, with an info hint «Ερμηνεύτηκε ως 1.234,56 €»; (4) anything else is an error «Μη έγκυρο ποσό».
- Shorthand: `12k`/`12χ` → 12.000,00; `1.5m`/`1,5ε` → 1.500.000,00.
- Precision comes from the MKT currency rule; **extra decimals are rejected with a message**.
- Negatives only where allowed, with U+2212 and `text.adverse` where adverse.
- Over the user's authority limit (PLT `authority.check`) → a *warning* «Πάνω από το όριο εξουσιοδότησής σας (5.000,00 €) · θα σταλεί για έγκριση».
- Width `field.sm`; 999.999.999,99 € must fit (≈128 px).
- **No ↑/↓ stepper on money.**

**Percentage:**
- el-GR «15 %» (U+202F), en «15%».
- 2 decimals default; 4 for RAT; ‰ variant.
- Range 0–100; deviations −100 to +∞.
- **Deviation variant:** sign, a delta vs technical premium («−7,50 % = −31,20 €»), and a 4 px **authority meter** (success within the limit, warning at 80–100%, danger beyond) with the text «Όριο σας ±10 %».
- Stepper ↑↓ by 0,5 (deviation) or 1, ×10 with Shift; no visible spinner in compact (shown on hover in comfortable).

**Shared states:**
- as for text input
- loading: the 14 px spinner shows in the **dependent** field, not this one
- read-only: right-aligned in tables, left in key–value lists
- `Esc` reverts to the last committed value

**ARIA:** the visible suffix is `aria-hidden`; the accessible value includes the unit («1.234,56 ευρώ»). The authority meter maps to RAC `Meter`.

**Motion:** MI-14; **no ticker** on inputs.

**Rule:** money right-aligned in tables and left-aligned in forms; show the currency on every amount when more than one currency is on screen.

### 7.7 Identifier inputs (§4.7) — RAC `TextField` + an in-house mask/formatter (no masking library)

| Identifier | Mask | Validation | Extras |
|---|---|---|---|
| ΑΦΜ | `000 000 000` | 9 digits, mod-11 | «Έλεγχος ΑΦΜ» async lookup; ΔΟΥ autofilled as "lookup-suggested" with its source |
| IBAN | `GR00 0000 …` groups of 4, mono | Length + mod-97 | Bank name from reference data in the helper text; VoP badge for disbursements |
| Greek plate | `ΑΑΑ-0000` | Only the 14 shared letters Α Β Ε Ζ Η Ι Κ Μ Ν Ο Ρ Τ Υ Χ | **Latin lookalikes auto-convert to Greek** as you type, with a 1 s hint «Μετατράπηκε σε ελληνικούς χαρακτήρες» |
| VIN | 17 characters, mono, uppercase | No I/O/Q; check digit gives a warning only for EU VINs | |
| Policy, claim, invoice number | Mono, scheme grouping from pack data | Format | Pasting a full number opens a quick-look (MI-55) |

All identifiers: `--font-mono`, `tabular-nums slashed-zero`, `autocomplete="off"`, `spellcheck="false"`; copy yields the ungrouped value.

### 7.8 Checkbox (§4.8) — RAC `Checkbox`, `CheckboxGroup`

**Anatomy:** 16×16 box (20 on touch), `radius.sm`, 2 px stroke check or indeterminate bar, label. The hit area covers box + label + 4 px and is at least 24 px tall.

**States:**
- unchecked: 1 px `border.control` on sheet
- checked or indeterminate: `interactive.accent.bg` with a white glyph
- hover: border `border.control-hover`; checked → **`accent.bg-hover`** (see §0.4)
- focus: ring around the box
- active: box scale 0.92 (70 ms)
- disabled: `border.subtle` + disabled bg; checked fill `#B9C0CB` / D `#3D4452` with glyph `#FFF` / D `#98A1B0`
- loading: n/a (instant-save checkboxes show MI-09 after the label)
- error: 2 px danger border + a group message
- read-only: `check` icon or «—» + label

**Keyboard:** Space; Tab between boxes in a group.

**ARIA:** native checkbox, `aria-checked="mixed"`, `fieldset`/`legend`.

**Rule:** never for an action with immediate legal effect.

**Motion:** MI-04.

### 7.9 Radio group and choice cards (§4.9) — RAC `RadioGroup`, `Radio`

**Anatomy:** 16 px circle (20 on touch) with a 6 px dot (8 on touch). Vertical by default; horizontal only for ≤ 3 short options.

**States:**
- selected: 5 px `interactive.accent.bg` border (the white centre forms the dot); MI-05
- hover: border hover
- focus: ring around the circle
- active: scale 0.92
- disabled: as checkbox
- loading: n/a
- error: group message + danger border
- read-only: the selected label as text

**Keyboard:** Tab lands on the selected option; arrows move **and select**.

**Choice cards** (offerings, payment plans): title, `tabular-nums` price, 3–5 bullets. Selected = 2 px `accent.bg` border + `state.row-selected` bg + a top-right check badge. Hover `elevation.2`. Same keyboard as radio.

**Motion:** MI-05.

### 7.10 Switch (§4.10) — RAC `Switch`

**Use:** immediate-effect settings only (preferences, permitted AI toggles, «Εορτασμοί»). Never inside submit-later forms.

**Anatomy:** track 32×18 (44×24 touch), `radius.full`; white thumb 14 px (20 touch) with `0 1px 2px rgba(15,23,42,.24)`; label on the left; optional «Ενεργό/Ανενεργό».

**States:**
- off: track `#B9C0CB` / D `#3D4452` **plus a 1 px inset `border.control`** to reach 3:1
- on: `accent.bg`
- hover: darkens one step (on → **`accent.bg-hover`**)
- focus: ring around the track
- active: thumb widens 14 → 18 px toward the travel direction
- disabled: 40% opacity + reason
- loading (server-persisted): 10 px spinner in the thumb; reverts with an error toast on failure
- error: n/a (reverts)
- read-only: text + `circle-check` / `circle-minus`

**Keyboard:** Space (and Enter in RA). **ARIA:** `role=switch` + `aria-checked`.

**The AI kill switch is not a switch:** it is a danger button with confirmation (tenant-wide effect).

**Motion:** MI-06.

### 7.11 Tabs (§4.11) — RAC `Tabs`, `TabList`, `Tab`, `TabPanel`; `keyboardActivation="manual"` for expensive panels; overflow menu is custom (`MenuTrigger`); URL sync via React Router

**Anatomy:** tab list with a 1 px `border.subtle` bottom; tabs with an optional count badge and an optional error dot; **sliding 2 px indicator** (`accent.bg`, top corners `radius.full`); panel.

**Variants:**
- `line`: record views (Σύνοψη, Καλύψεις, Συναλλαγές, Χρεώσεις, Ζημίες, Έγγραφα, Ιστορικό)
- `contained`: in cards and panels; the selected tab is a sheet with `elevation.1` on a sunken track

**States:**
- default: `text.secondary`, `label.lg` (Cf) / `label` (C)
- hover: `text.primary` + `ghost.bg-hover` with `radius.md`
- focus: ring inset −2 px
- selected: `text.primary` 600; width reserved via a hidden bold copy so labels do not shift
- disabled: + reason
- loading: panel skeletons (MI-33)
- error: 6 px `danger.solid` dot + count in the accessible name («Καλύψεις, 2 σφάλματα»)
- read-only: n/a

**Overflow:** «Περισσότερα ▾», never scroll. The selected tab is always visible (it swaps with the last visible tab).

**Keyboard:** arrows move **and activate** (automatic), except panels that take > 300 ms (manual, Enter). Home/End. **Ctrl+PgUp/PgDn** from inside the panel (custom).

**ARIA:** tablist/tab/tabpanel; deep link `?tab=coverages`.

**Motion:** MI-12, 37.

### 7.12 Segmented control (§4.12) — RAC `RadioGroup` styled (the spec says radiogroup semantics); `ToggleButtonGroup selectionMode="single" disallowEmptySelection` is the alternative

**Use:** 2–5 exclusive views or modes (Λίστα/Πίνακας/Χάρτης, Μηνιαία/Τριμηνιαία, density toggle).

**Anatomy:** track (sunken, 1 px `border.subtle`, `radius.md`, 2 px padding) → segments → **sliding thumb** (sheet, `elevation.1`, `radius.sm`).

**States:**
- label `text.secondary`, hover `text.primary`
- focus: ring on the segment
- press: thumb scale 0.98
- selected: label `text.primary` 500
- a single disabled segment + reason
- loading and error: n/a
- read-only: label as text

**Keyboard and ARIA:** radiogroup with arrows. **Motion:** MI-13.

### 7.13 Badges and status pills (§4.13) — not interactive: a plain `<span>` component `<StatusPill entity state/>`; announcements via React Aria `announce()` (`@react-aria/live-announcer`)

**Anatomy:** icon (12 px C / 14 Cf) → label (`type.label` 12/16 C, 13/16 Cf, weight 500) → optional «· sub-label» → optional countdown («6 ημ.»).

**Variants:**
- `status`: bg/fg/1 px border of the family
- `status-solid`: danger bg, white, `elevation.1`; **breached and conflict only**
- `status-outline`: transparent, 1 px family `solid`, `fg` text (AI-generated; IB-29 logs)
- `dot`: 8 px family `solid` dot + `text.secondary` label (sidebars, narrow columns)
- `count`: min 18×18, `radius.full`, caption 600, `tabular-nums`; neutral or attention (danger bg, white)

**Sizes:**
- `sm`: 18 / 20 px tall, padding 6
- `md`: 20 / 24 px tall, padding 8
- radius `sm` (compact) / `full` (comfortable)

**Behaviour:**
- A clickable pill becomes a **filter chip**.
- Changing status: the icon swaps to a 12 px spinner.
- Status change animates (MI-15) and is announced politely («Κατάσταση: Σε ισχύ»).

**Rules:**
- Text is never omitted: narrow columns use the `dot` variant with the label in a tooltip and the accessible name.
- One status pill per entity per row, plus at most one flag pill.
- Countdown pills: info until the CMP warning threshold, then warning, then solid when breached; the text updates every minute **without animation**.
- Render statuses only through the map, never with ad-hoc colours.

### 7.14 Tags and filter chips (§4.14) — RAC `TagGroup`, `TagList`, `Tag` (`onRemove`); clicking a chip reopens its filter `Popover`

**Anatomy:** label + optional icon or avatar + optional ×; height 24 / 28; `radius.full`.

**States:**
- default: brand family bg/fg/border; label «Κατάσταση: Σε ισχύ, Σε εκκρεμή ακύρωση», values truncate after 32 characters (full text in a tooltip)
- hover: border `brand.solid`
- focus: ring
- active: 0.97
- disabled: n/a; locked ABAC filters show a `lock` icon and no ×
- loading: n/a
- error: invalid saved filter → danger family + «Το πεδίο δεν υπάρχει πλέον»
- read-only: neutral family, no ×

**Keyboard:** Enter edits; Delete/Backspace removes. **Motion:** MI-25.

### 7.15 Tooltip (§4.15) — RAC `TooltipTrigger`, `Tooltip`, `OverlayArrow`

**Use:** icon labels, disabled reasons, abbreviations, truncated text. Never for task-critical information.

**Anatomy:** `surface.inverse`, **solid, never glass**; `text.inverse`; `type.caption`; padding 6×8; `radius.sm`; max width 280; 6 px arrow; optional kbd chip (bg `rgba(255,255,255,.14)` / D `rgba(0,0,0,.08)`).

**Timing:** hover 400 ms (warm 0 ms for 1,500 ms); keyboard focus 0 ms. Hoverable per WCAG 1.4.13 (the pointer can move onto the tooltip). `Esc` dismisses without moving focus. RA supports `delay`/`closeDelay`; check the hoverable-content behaviour.

**ARIA:** `role=tooltip`; the trigger gets `aria-describedby`, or `aria-labelledby` for icon buttons.

**Touch:** long-press 500 ms (custom), otherwise labels appear in a bottom-sheet menu.

**States:** n/a. **Motion:** MI-11.

### 7.16 Popover, hover card, quick-look, explain-why (§4.16) — `popover` = RAC `DialogTrigger` + `Popover` + `Dialog`; hover card = custom (`useHover` + `Popover isNonModal`, no focus steal)

**Anatomy:** `material.popover`, `radius.lg`, `elevation.3`, padding 12 / 16, optional 8 px arrow, optional header (title + ×), body, optional footer. Width 240–400 px (filters up to 480).

**Variants:**
- `popover` (click or Enter): filters, field help, column settings, explain-why.
- `hover-card`/**quick-look** (hover 500 ms on a record link, or **Space** on a focused link): mini record with title, mono ID, pill, 4 facts, last 2 activities; «Άνοιγμα» (Enter) and «Άνοιγμα σε νέα καρτέλα» (Ctrl+Enter). Prefetch on hover intent (pointer speed < 0.1 px/ms; provisional).
- `explain-why` (IB-08) («Γιατί;» link or `?` on a focused value): inputs, factor bars (MI-53), confidence (AI) or rule/table version (deterministic), numbered sources, «Προβολή πλήρους φύλλου υπολογισμού».

**States:** open/closed; loading skeleton after 150 ms; error «Δεν ήταν δυνατή η φόρτωση» + Retry.

**Keyboard:** click popovers move focus to the first focusable element; Esc returns focus to the trigger; **Tab past the end closes** (non-modal). Hover cards do not take focus.

**ARIA:** trigger `aria-haspopup="dialog"` + `aria-expanded`; the popover is a non-modal `role=dialog` with `aria-labelledby`.

**Motion:** MI-28, 55.

### 7.17 Menu: dropdown, context, overflow (§4.17) — RAC `MenuTrigger`, `Menu`, `MenuItem`, `SubmenuTrigger`, `MenuSection`, `Separator`, `Keyboard`; context menu is custom (`onContextMenu` + `Shift+F10`/Menu key → `Popover` with `triggerRef`)

**Anatomy:** `material.popover`; groups divided by 1 px `border.subtle` with 4 px margin; items: [16 px icon] label [description] [shortcut chip] [submenu chevron]; destructive group at the bottom.

**Item states:**
- default: `text.primary`, height `size.row.list`
- hover/highlight: sliding pill (MI-19)
- focus: the pill **is** the indicator, plus a 2 px inset left bar in `border.focus` under forced colours
- active: `ghost.bg-pressed`
- disabled: + reason tooltip
- loading: the menu closes immediately and progress shows in a toast or on the trigger
- destructive: `danger.fg`
- checked: 16 px `check` in the leading slot

**Context menu** on rows: same actions as the row's ⋯ button.

**Keyboard:** ↑↓, Home/End, typeahead, → opens a submenu, ← closes it, Enter, Esc. Submenus open after 120 ms hover intent with a **safe triangle** (built into RA).

**Motion:** MI-19, 28.

### 7.18 Modal dialog (§4.18) — RAC `ModalOverlay` + `Modal` + `Dialog` (`role="alertdialog"` for destructive) + `Heading slot="title"`

**Use:** blocking decisions, destructive or legal confirmations, short forms (≤ 6 fields), maker-checker decisions. Not long forms.

**Anatomy:** scrim `surface.scrim.modal` → `material.overlay`, `radius.xl`, `elevation.4` → header [20 px icon in a 36 px tinted circle] + `type.heading.2` title + 28 px ghost × → body `type.body`, max 72 ch → footer right-aligned, primary last. For destructive actions the danger button is last and «Άκυρο» is **first and focused**.

**Sizes:** sm 400, md 560, lg 720, xl min(1040 px, 92vw). Max height `calc(100vh - 96px)`; the body scrolls; header and footer borders appear only when scrolled.

**States:**
- open: MI-29
- busy: primary loading, others disabled, Esc and scrim click disabled, caption «Η ενέργεια εκτελείται…»
- error: danger banner at the top of the body; input preserved
- disabled/read-only: n/a

**Destructive recipe:** title = action + object («Ακύρωση ασφαλιστηρίου ΑΣΦ-2026-004471;»); body = key–value consequences; high-impact actions require typing a token (the last 4 digits) to enable the danger button.

**Keyboard:** focus trap; initial focus on the first field or the safe action; Esc closes unless busy; focus returns to the trigger; Ctrl/⌘+Enter runs the primary if it is not destructive.

**ARIA:** `aria-labelledby`/`aria-describedby`. While open, the shell chrome goes solid (GlassBudget). Stacked modals max 2.

**Motion:** MI-29.

### 7.19 Drawer (§4.19) — modal: RAC `ModalOverlay` + `Modal` + `Dialog`; non-modal (notifications): custom `role="complementary"` panel (no RA primitive); swipe-to-close via `useMove`

**Use:** tablet navigation, advanced filters, the notification centre.

**Anatomy:** edge panel below the top bar; width 360 (filters), 400 (notifications), 320 (nav); `material.overlay`; no scrim when non-modal; 48 px header; scroll body; sticky footer («Εκκαθάριση» / «Εφαρμογή (124)» with a live count).

**States:** open MI-30; loading skeletons; error banner; disabled/read-only n/a.

**Keyboard:** modal drawers trap focus; non-modal drawers are reachable with F6; Esc closes.

**ARIA:** modal `role=dialog aria-modal=true`; non-modal `role=complementary` + label.

**Touch:** swipe to close at > 0.5 px/ms or > 40% distance, `spring.smooth`.

### 7.20 Side sheet (§4.20) — custom non-modal dialog: `FocusScope` (no trap) + `useDialog` from react-aria; not RAC `Modal`

**Use:** create or edit sub-records without leaving the record (vehicle, payee, exposure, note, AI-drafted letter).

**Anatomy:**
- Inside the main sheet region (it does not cover the nav).
- Width 480 (md), 640 (lg), or 50% (`split`, document + form).
- **Opaque sheet** with a left-edge `elevation.4`.
- The underlying record dims to `rgba(14,17,22,0.12)` but stays readable and scrollable.
- Header: title + context chip («για ΑΣΦ-2026-004471») + close. Sticky footer: Άκυρο · primary.

**States:**
- open: MI-30
- dirty: 6 px `warning.solid` dot on the title (MI-64); close asks «Απόρριψη αλλαγών;»
- busy: primary loading, `aria-busy`
- error: summary banner at the top, focus moves to it
- read-only: view mode, no footer, «Επεξεργασία» button if permitted
- disabled: n/a

**Stacking:** **max one**; opening another replaces it (with a dirty check).

**Keyboard:** Esc (with dirty check), Ctrl/⌘+S saves, Ctrl/⌘+Enter saves and closes; focus to the first field on open and back to the trigger on close.

**ARIA:** `role=dialog aria-modal=false`, labelled.

### 7.21 Toast (§4.21) — own `Toaster` (spec), or RA `UNSTABLE_ToastRegion`/`useToastRegion`

**Use:** confirm user actions, with undo. Not for errors needing action, and not for events the user did not trigger (except offline/reconnected).

**Anatomy:** `material.float`, `radius.xl`, `elevation.4`, 360 px (280–420), padding 12×14; 16 px family icon → title `label.lg` → optional 2-line caption body → optional action (Αναίρεση/Προβολή) → × → **2 px timeout hairline** (family `solid` at 60%, shrinking linearly).

**Position:** bottom-right, 16 px from the sheet, above the status bar; bottom-centre at ≤ `bp.sm` (above the tab bar).

**Duration:** success 4 s; with an action 6 s; info 5 s; warning 8 s; error (transient with retry) persists. **Timers pause** on hover, on focus within the region and while the window is blurred (WCAG 2.2.1).

**Stack:** 3 visible as a depth stack (scale .95/.90, translateY −8/−16, opacity .9/.7); hover or focus expands it; the 4th and later queue.

**Batch:** one summary toast («Εκδόθηκαν 48 ασφαλιστήρια · 2 απέτυχαν · Προβολή»).

**Progress toast:** a `gradient.tide` bar for background jobs, which becomes a success or error toast.

**States:** dismiss = slide right 24 px + fade 200 ms.

**Keyboard:** **F8** focuses the newest toast region; Esc dismisses the focused toast.

**ARIA:** `role=region aria-label="Ειδοποιήσεις"`; success/info `aria-live=polite`; warning/error `role=alert`. **Undo must also be reachable from the record for 30 s.**

Shares one glass layer with the bulk bar. **Motion:** MI-31.

### 7.22 Banner (§4.22) — plain markup (`role="status"` / `role="alert"`); actions are RAC `Link`/`Button`

**Anatomy:** 3 px left accent bar (family `solid`) → 16 px icon → title `label.lg` + body `body.compact` → link actions → optional ×. Bg family `bg`, 1 px family border; `radius.lg` inline, `radius.none` page-level (under the record header).

**Variants:**
- info
- success
- warning (`stale` «Υπάρχουν νεότερα δεδομένα · Ανανέωση», PendingCancellation, authority)
- danger (form error summary, `conflict`, sanctions hit blocking payment)
- ai (disclaimer, «Η βοήθεια ΤΝ δεν είναι διαθέσιμη· συνεχίστε χειροκίνητα»)
- system (neutral, page-level above the header: maintenance, kill switch)

**Error summary:** «Διορθώστε 3 πεδία για να συνεχίσετε» + links that focus each field; it **receives focus on submit failure** (WCAG 3.3.1/3.3.3).

**Motion:** appear via `grid-template-rows` 0fr→1fr over 200 ms (MI-27); dismiss 150 ms. Danger and conflict banners are not dismissible.

**ARIA:** status (info, success, warning) or alert (danger in response to an action).

### 7.23 Stepper (§4.23) — no RA primitive: `<nav>` + list of RAC `Link`s with `aria-current="step"`; Alt+→/← are custom global shortcuts

**Anatomy (vertical):** 24 px node (number, check, `circle-alert` + count, or `lock`) → `label.lg` label → caption sub-label («3 οχήματα · 2 οδηγοί» / «2 σφάλματα») → 2 px connector.

**States:**
- upcoming: 1 px control ring, `text.tertiary` number, `text.secondary` label
- current: `accent.bg` fill, white number, 4 px halo `rgba(63,99,240,.18)`, label 600
- complete: `success.solid` fill, white check; connector fills with `gradient.tide` (MI-38)
- error: `danger.bg` node + `circle-alert` + count; sub-label in `danger.fg`
- warning: `warning.bg` + `triangle-alert` «1 ζήτημα ανάληψης»
- locked: 14 px lock in `text.disabled` + reason
- hover: node scale 1.06 + underline
- focus: ring around node + label
- loading: spinner in the node
- read-only (a Bound job): all complete and navigable

**Navigation:** completed and error steps are clickable; upcoming steps are clickable only if all previous steps are valid (non-linear for experts).

**ARIA:** the accessible name includes the state («Βήμα 3, Οχήματα, ολοκληρωμένο»).

### 7.24 Progress, spinner, skeleton (§4.24) — RAC `ProgressBar` (determinate and indeterminate), `Meter` for authority meters; spinner and skeleton are custom

- **Linear determinate:** track 4 / 6 / 8 px (`lg` is IB-12), `radius.full`, track `status.neutral.bg`, fill `gradient.tide`. Value «62 %» + «31 από 50». At 100% the fill turns `success.solid` and an end-cap check draws (MI-43).
- **Linear indeterminate:** 2 px, a 30% `brand.solid` segment sliding on a 1.2 s loop. Reduced motion: a static segment pulsing opacity 0.4↔1 over 1.6 s.
- **Circular / step ring:** 16 / 24 / 40 / 64 px with strokes 2 / 2.5 / 3.5 / 5; SVG `gradient.tide` arc; the value shows in the centre at 40 and 64 px.
- **Spinner:** 12 / 14 / 16 / 20 / 24 px; a 2 px 270° arc in `currentColor`; 800 ms per revolution (1,200 ms under reduced motion); only after 400 ms.
- **Skeleton:** blocks match the final layout (lines at 60–90% width, height = line height − 8, `radius.sm`); colour `#EEF0F3` / D `#1F242D`; shimmer MI-33; shows after 150 ms and stays at least 300 ms.
- **ARIA:** `role=progressbar` with `aria-valuenow`/`min`/`max`/`valuetext` («31 από 50 εργασίες»); indeterminate omits `valuenow`. Skeleton regions get `aria-busy=true` + a hidden «Φόρτωση…».

### 7.25 Avatar and presence (§4.25) — plain `<img>`/initials; presence stack = RAC `Button` + `DialogTrigger`/`Popover`

**Anatomy:** circle → image or 2 initials (`type.label` 600, white) on a deterministic background from 8 hues (aegean.600, teal.600, plum.600, violet.600, green.600, sky.700, red.700, amber.700) → optional presence ring (2 px, offset 2) → optional 8 px status dot with a 2 px sheet-coloured cutout.

**Sizes:** xs 20 (cells, mentions), sm 24 (presence stack, comments), md 32 (user menu, activity), lg 48 (party header; organisations use `radius.md` squares), xl 64 (profile, SM-08).

**Presence rings:** viewing `success.solid`; editing `brand.solid` + MI-46 (**never Iris**); idle > 5 min `neutral.solid` at 50%; offline none.

**Stack:** max 4, overlapping 6 px with 2 px sheet borders, then «+3». Hover or focus shows a list («Μαρία Παπαδοπούλου · επεξεργάζεται: Καλύψεις · από 14:02»). Join and leave use MI-20.

**Field lock:** xs avatar + «Επεξεργάζεται η Μ. Παπαδοπούλου» in `text.secondary`.

**ARIA:** `alt` = name, or `alt=""` when the name is visible next to it; the stack button has `aria-label="3 ακόμη άτομα βλέπουν αυτή την εγγραφή"`.

### 7.26 Card (§4.26) — plain; interactive = stretched RAC `Link`; selectable = `Radio` (choice card)

**Anatomy:** on a sheet: 1 px `border.default` (no shadow); on canvas: `surface.raised` + `elevation.1`. `radius.lg`, padding `space.inset.card`, header `type.heading.3` + meta + actions, body, optional footer with a top border.

**Variants:**
- `static`
- `interactive`: hover `elevation.2` + `border.control` (150 ms); active 0.995; focus ring on the card; stretched link, no nested interactives except explicit secondary buttons
- `selectable`: as choice card
- `work-left` (IB-11): 8 px family dot top-left (4 px 18% halo, 10 px glow), title indented 16 px, count, due pill, «Συνέχεια»; completes with MI-40
- `ai-summary`: see §7.37

**States:** loading skeleton; error banner + retry; empty «Δεν υπάρχουν δεδομένα» + action.

### 7.27 KPI tile and hero metric (§4.27) — plain; definition popover = `DialogTrigger` + `Popover`; report link = `Link`

**Tile anatomy:** sheet, `radius.lg`, 1 px `border.default` → label (`type.label`, `text.secondary`) + `circle-help` popover (formula, source mart, as-of) → value `type.numeric.kpi` `tabular-nums` + unit → delta chip («▲ 4,2 % vs προηγ. μήνα»; success or danger by **per-KPI favourability**) → 32 px sparkline with `gradient.horizon.*` → «Ενημέρωση 14:32».

**States:**
- hover (if linked): `elevation.2` + «Προβολή αναφοράς →» slides in
- focus: ring
- active: 0.995
- loading: 96×28 skeleton + a line
- error: «—» + `circle-alert` + «Δεν είναι διαθέσιμο» + retry
- stale (past the freshness SLA: > 10 s for read models, > 24 h for marts): as-of caption in `warning.fg` + clock
- live update: MI-16
- first render: MI-69 / MI-56

**Hero metric** (one per dashboard): `type.display.xl`, a comparison line, a 64 px trend with horizon fill, on the canopy card. Example «Ασφάλιστρα νέας παραγωγής Οκτ. · 1.284.390 € · 104 % του πλάνου».

**Formatting:** abbreviated in tiles («1,28 εκ. €») with the full value in the tooltip and accessible name.

### 7.28 Statement view (§4.28) — plain list; rows as interactive rows (RAC `GridList` or table row links)

**Anatomy:** headline balance `type.display` with a **direction label** («Οφειλή προς εμάς» / «Οφειλή από εμάς» / «Εξοφλημένο») → period selector → two-column line items (description `text.primary`; amount right-aligned `tabular-nums`; date and reference in `text.tertiary` below) → subtotals with a 1 px top rule → adverse movements in `text.adverse` with «−» + `arrow-down-right` → optional running balance.

**States:** loading skeleton; empty «Καμία κίνηση στην περίοδο»; error banner; row hover and focus as in tables.

**Rule:** colour follows the **adverse semantics of the statement type**, never the sign alone.

### 7.29 Key–value list (§4.29) — `<dl>`; inline editors reuse `TextField`/`NumberField`/`Select`

**Anatomy:** label (`type.label`, `text.secondary`, `minmax(140px, 40%)`, 2 lines max) → value (`body.compact`; mono for IDs; right-aligned money only in money-only lists); row gap 8 / 12; optional inline edit and explain-why.

**Inline edit (IB-14, PRD-marked fields only):** hover shows a 14 px `pencil` + `ghost.bg-hover`; click or Enter swaps in the input at the same metrics (MI-23); Enter commits (MI-09); Esc cancels; on failure the value reverts with a danger message + «Επανάληψη».

**Fields needing a transaction:** `lock` + «Απαιτείται πρόσθετη πράξη» + an «Έναρξη αλλαγής» link. Never inline-edit fields that need a transaction, approval or maker-checker.

### 7.30 File upload (§4.30) — RAC `DropZone` + `FileTrigger`; per-file `ProgressBar`

**Anatomy:** drop zone (dashed 1.5 px `border.control`, `radius.lg`, sunken, 120 / 160 px tall, 24 px `upload-cloud`, «Σύρετε αρχεία εδώ ή **επιλέξτε**», caption «PDF, JPG, PNG, HEIC, DOCX, MSG · έως 25 MB το καθένα») → file rows 48 px (type icon or 40×40 thumbnail, middle-truncated name, size + pipeline status, progress ring, remove/retry/preview).

**Pipeline:**
- queued: neutral «Σε αναμονή»
- uploading: tide ring «Μεταφόρτωση 62 %»
- scanning: indeterminate + `shield` «Έλεγχος ασφαλείας…»
- classifying/extracting: `sparkles` «Ταξινόμηση…» → chip «Δήλωση ατυχήματος · 96 %» (AI-suggested until confirmed)
- done: check MI-42 «Έτοιμο»
- rejected: danger with the exact reason («Μη επιτρεπτός τύπος αρχείου (.exe)»)
- failed: danger «Η μεταφόρτωση διακόπηκε · Επανάληψη»

**Zone states:** drag-over MI-18 (`border.focus` marching ants + `state.drop-target`); focus ring (Enter/Space opens the dialog); disabled at max files («Έχετε φτάσει το όριο των 20 αρχείων»); batch error banner; read-only (list only).

**Input:** Ctrl+V paste supported; `capture="environment"` on mobile.

**ARIA:** the zone is a button; per-file progressbar; grouped polite announcements («3 αρχεία έτοιμα, 1 απορρίφθηκε»).

### 7.31 Document viewer (§4.31) — pdf.js (`pdfjs-dist`) + RAC `Toolbar`, `Button`, `ToggleButton`, `Menu`

**Anatomy:**
- 40 px toolbar: title + version pill + language pill («ΕΛ · δεσμευτικό» / «EN · ενημερωτικό») | «3 / 12» | zoom −/%/+, fit width, fit page | rotate | search | download | print | new window | ⋯
- Canvas: sunken; white pages with `elevation.1` and a 16 px gap.
- Optional 96 px thumbnail rail.
- Optional right panel: extraction, comments, metadata (hash, archive id, retention, legal hold).

**Extraction overlay:** 1.5 px boxes, `ai.solid` while suggested and `success.solid` when verified; hover links the box and the field with a 120 ms fill `rgba(111,54,222,.10)`; low confidence = dashed + warning.

**States:**
- loading: aspect-correct placeholders + spinner; progressive render
- error: «Δεν ήταν δυνατή η προβολή · Λήψη αρχείου»
- unsupported type (.msg): metadata + download + text preview
- read-only by default (annotations are separate objects)
- legal hold: lock banner «Υπό δικαστική δέσμευση · δεν διαγράφεται»
- superseded: banner «Αντικαταστάθηκε από την έκδοση 3 · Άνοιγμα»

**Keyboard:** PgUp/PgDn, Home/End, Ctrl/⌘ +/− (captured inside the viewer), Ctrl/⌘+F, R to rotate.

**ARIA:** keep the pdf.js text layer; OCR text serves as the long description for images.

### 7.32 Timeline and audit trail (§4.32) — custom `role="feed"`; filters via segmented (`RadioGroup`), `ComboBox` and `DateRangePicker`; audit log = the data table in IB-29 mode

**Timeline anatomy:** 1 px rail 12 px from the left → 8 px family dots (20 px icon nodes for key events) → card: actor (xs avatar + name / «Σύστημα» `cpu` / «Πράκτορας ΤΝ για Μ. Π.» `sparkles`) · action · object link · relative time (absolute in the tooltip and on focus) → optional field diff («Ημερομηνία λήξης: ~~31/12/2026~~ → 30/06/2027»: old in `text.tertiary` struck through, 12 px `arrow-right`) → optional trace link.

**Grouping and filters:** sticky day headers (Σήμερα, Χθες, «Δευτέρα 5 Οκτωβρίου»). Edits by the same actor within 5 minutes collapse («έκανε 6 αλλαγές»). Filters: type (Όλα/Συναλλαγές/Έγγραφα/Επικοινωνία/Αλλαγές πεδίων/ΤΝ), actor, date range.

**Audit viewer (UIL-K5):** dense 28 px rows, sunken zebra instead of borders, `type.mono.sm` technical columns, expandable JSON-aware side-by-side diff (added `success.bg`, removed `danger.bg`, plus +/− glyphs). P2/P3 masked; reveals are audited.

**States:** loading skeleton nodes; empty «Δεν υπάρχουν συμβάντα με αυτά τα φίλτρα»; error + retry; live insert MI-48; **append-only** (no edit affordances).

**ARIA:** `role=feed` with articles carrying `aria-posinset`/`aria-setsize`, `aria-busy` while loading older items, and a «Φόρτωση παλαιότερων» button as an alternative to infinite scroll.

### 7.33 Comments and pinned comments (§4.33) — pin = RAC `Button`; thread = `DialogTrigger` + non-modal `Popover`; composer `TextArea` + custom @mention combobox

**Anatomy:** a 16 px speech-bubble pin in `brand.solid` at the element's top-right (with a count when > 1) → thread popover 360 px: header (field name, resolve) → comments (sm avatar, name, time, body with @mentions as brand chips, edit/delete within the WRK window) → composer (auto-grow, @ mention, Ctrl/⌘+Enter sends).

**Rail:** in the context panel; selecting a thread scrolls to the element and pulses its pin (MI-49).

**States:**
- unresolved: solid brand pin
- resolved: outline neutral pin, hidden by default («Εμφάνιση επιλυμένων»)
- unread: 6 px `danger.bg` dot
- sending: optimistic append at 60% opacity (MI-41); failure «Δεν στάλθηκε · Επανάληψη»
- read-only (audit role): composer hidden
- disabled: n/a

**ARIA:** pin label «Σχόλια στο πεδίο Ποσοστό απαλλαγής, 2»; the thread is a non-modal dialog; mentions notify via the notification centre.

**Note:** these are threads on fields, not WRK notes (notes are records).

### 7.34 Keyboard shortcut chip (`kbd`) and shortcut overlay (§4.34) — RAC `Keyboard`; overlay = `Modal` + `SearchField`

**Chip:** height 18, min width 18, padding 0 4, `radius.sm`, `type.mono.sm` at 11/16, sunken bg, 1 px `border.default` + a 2 px bottom border. Shows ⌘ on macOS/iPadOS and Ctrl elsewhere (`navigator.userAgentData.platform`, falling back to `navigator.platform`).

**Overlay:** `?` (outside text fields) opens a searchable cheat sheet grouped by context.

### 7.35 Data table (§4.35) — **TanStack Table v8 + TanStack Virtual v3** (spec); semantics `role="grid"` or `"table"`; cell editors from RAC; header menus RAC `Menu`; column settings `Popover` + `CheckboxGroup`/`GridList` with drag and drop (+ move buttons)

**Anatomy:**
- Toolbar 48 / 56 px: saved-view switcher (with «• τροποποιημένη»), table search, «+ Φίλτρο», chips, «Εκκαθάριση», density, columns ⚙, export.
- Sticky header.
- Virtualised body.
- 36 px footer («1–50 από 4.812 · 3 επιλεγμένα» + a labelled money total).
- Floating bulk bar.

**Cells:**

| Cell | Alignment | Format |
|---|---|---|
| Text | left | Ellipsis + tooltip; 2-line clamp only for description columns in comfortable |
| Identifier | left | Mono link + quick-look + copy on hover |
| Money | **right** | el-GR; adverse uses `text.adverse` + «−» |
| Percentage | right | Fixed decimals |
| Date | left | dd/mm/yyyy (relative only in activity columns) |
| Status | left | Pill or dot |
| Priority | left | Bar + number + «Γιατί;» |
| Countdown | left | Pill |
| Person | left | xs avatar + name |
| Boolean | centre | `check` or «—» |
| Actions | right, pinned | ⋯ 24 px + 1–2 quick actions on hover |

**Empty values:** «—» in `text.tertiary` with the accessible name «κενό».

**Row layouts (v3):**
- **Single-line:** 32 / 40 / 48.
- **Two-line queue row:** **60 / 64 / 72**. Primary line 13.5 px weight 500; secondary line 12 px `text.tertiary` with the mono ID (11.5 px, `text.secondary`) + a 3 px dot + one fact.

**Header rules:** 38 px, `type.caption` 500 `text.tertiary` (sorted column `text.primary` + a 12 px arrow), **single line, never wraps**, columns sized for the Greek label («Προτεραιότητα» needs 104 px); units go in cells (not headers); abbreviations only from a controlled list, with a tooltip.

**Dividers:** 1 px `border.subtle`, inset after the selection column; none after the last row.

**Selection checkbox:** 16 px, 1.5 px `border.control`, 55% opacity until the row is hovered, selected or focused (full opacity on keyboard focus and under `hover: none`).

**Priority bar:** 64×6, `accent.bg` fill, 12.5 px value. One colour only, because priority is not a status.

**Queue template at ≥ 1240:** `44px minmax(0,1fr) 132px 76px 112px`; min width 540; queue pane `min(620px, 47%)`; the detail pane stacks below 820 px (container query).

**Row states:**
- default: `#FFF` / `#161A21`, divider `#E4E7EC` / `#2A303B`
- hover: `#F4F5F7` / `#1C2028`; quick actions fade in (MI-07)
- focus: inset 2 px ring `#3F63F0` / `#8FA9FF` (grid mode rings the cell)
- selected: `#EEF3FF` + a 2 px `#2E4FD6` left bar (D `#1A2547` / `#8FA9FF`; MI-08)
- selected + hover: `#E5ECFF` / `#1B2649`
- **cursor row** (J/K): ring style at 50% even when focus is in the detail pane
- disabled (locked): `text.tertiary` + `lock` + reason
- loading: 2 px tide shimmer at the bottom (MI-41)
- error: 3 px `danger.solid` left bar + an inline message row with «Επανάληψη»
- read-only: no edit affordances
- new: `brand.bg` flash over 1,200 ms (MI-48) + a «Νέο» dot for 60 s
- stale: 1 px `warning.solid` top border + banner «12 εγγραφές άλλαξαν · Ανανέωση» (**no auto-reorder under the cursor**)

**Sorting:**
- Click cycles asc → desc → clear. Shift+click adds up to 3 sorts, shown as ▲1 ▼2 ▲3.
- `arrow-up-down` appears on hover for sortable columns.
- MI-21 when ≤ 50 visible rows move.
- Server sort beyond the window, with a 2 px bar.
- Collation `Intl.Collator('el',{sensitivity:'base',numeric:true})`.
- `aria-sort` on the primary sort only.

**Filtering:**
- text: contains / equals / starts with / empty
- enum: searchable checklist with counts + «Μόνο αυτό»
- number/money: min–max + presets + a 32 px histogram brush
- date: range + presets
- person: combobox with «Εγώ» and «Η ομάδα μου»
- Global search is client-side ≤ 5,000 rows, server-side above that, accent-insensitive.
- The footer count ticks (MI-16 allowed).
- **Exception-first (IB-28):** queues default to «Χρειάζεται προσοχή» with a «4.721 κανονικές εγγραφές κρυμμένες · Εμφάνιση όλων» row.

**Pinning:** left = selection + ≤ 2 identifying columns (≤ 40% of width); right = actions, always. Opaque bg, pinned shadow only when scrolled (MI-22).

**Resizing:** 8 px handle, 2 px guide line, min 64 / max 640, double-click autosizes (first 200 rows, max 480), **Ctrl+Alt+←/→** by 16 px. Widths persist in the view.

**Reordering:** drag (MI-68) **and** «Μετακίνηση πάνω/κάτω» buttons (WCAG 2.5.7).

**Visibility:** 320 px popover with search, grouped checkboxes and «Επαναφορά προεπιλογών».

**Inline edit** (PRD-marked columns only): Enter/F2/click opens an in-place editor at identical metrics with a 2 px `border.focus` box. Enter commits and moves down; Tab commits and moves right; Esc cancels. Optimistic + MI-09 + audit. Errors keep the editor open. Batch paste only in config tables, with a diff preview modal.

**Selection and bulk:**
- Header tri-state checkbox; a full page selected offers «Επιλογή και των 4.812 που ταιριάζουν».
- Shift+click range; Ctrl/⌘+A; X; Shift+J/K.
- **Floating bulk bar** (MI-24): `material.float`, `radius.xl`, 48 px, centred 24 px above the sheet bottom, max width 720: count (ticker) | ≤ 4 ghost actions | ⋯ | ×.
- Maker-checker wording applies. One summary toast + per-row errors.
- More than 500 rows runs as a background job with a progress toast.

**Virtualisation:**
- Fixed row height per density; overscan 8; scroll anchor in history state.
- Pages of 100 with a skeleton band, or classic pagination (25/50/100) where stable page references matter.
- `aria-rowcount` (or −1) and `aria-rowindex` on every row («Γραμμή 1.204 από 4.812»).
- 100k rows at 60 fps on the Standard tier; row render ≤ 0.5 ms (memoised cells, no per-cell closures).

**Saved views (IB-03):**
- Switcher groups: Προσωπικές / Ομάδας / Συστήματος, with live counts.
- A view stores filters, sort, order, widths, pinning, visibility, density and grouping.
- Dirty → «Αποθήκευση» / «Αποθήκευση ως νέα…».
- Default per user, falling back to the role's view.
- Team views require the team-lead role.
- URL `?view=…&f=…`; **P2/P3 values are never in the URL** (use a server-side token).

**Keyboard:**

| Key | Action |
|---|---|
| J/K, ↓/↑ | Move the cursor row |
| Enter / O | Open |
| Space | Quick-look |
| X | Toggle selection |
| Shift+J/K | Extend selection |
| Ctrl/⌘+A | Select all loaded |
| E | Assign / bulk menu |
| / | Focus search |
| F | Add filter |
| G then V | View switcher |
| ←/→ (grid mode) | Move between cells; Enter/F2 edits |
| Home/End, Ctrl+Home/End | Row and table bounds |
| PgUp/PgDn | Move by a viewport |
| Shift+F10 / Menu | Context menu |

Single-key shortcuts are off inside text inputs and when the user setting is off.

**Table-level states:**
- first load: header + 8 skeleton rows
- refetch: 2 px bar under the header
- empty: empty-state block (Part 3) «Δεν υπάρχουν παραπομπές»
- filtered empty: «Κανένα αποτέλεσμα με αυτά τα φίλτρα» + «Εκκαθάριση φίλτρων» + the active filters
- error: banner + retry; old rows stay, marked stale
- permission-limited: columns omitted + a caption «Ορισμένες στήλες δεν εμφανίζονται λόγω δικαιωμάτων»
- offline: cached rows + `wifi-off` banner + actions disabled with reasons

**Responsive (container query):** ≥ 905 full; 600–904 priority columns + expand; < 600 card list.

**Motion:** MI-07, 08, 16, 21, 22, 23, 24, 25, 33, 40, 41, 48, 55, 68.

**Do:** fixed row heights; labelled filtered/full totals; reorder only on explicit sort.

### 7.36 Command palette (§4.36) — RAC `ModalOverlay`/`Modal`/`Dialog` + `Autocomplete` + `SearchField` + `Menu` (or `ListBox`) with `MenuSection`/`Header`; WRK search back-end (CD-18)

**Anatomy:**
- Scrim → `material.overlay`, `radius.2xl`, `elevation.5`, 640 px wide, max 480 px tall, top at 14vh.
- 56 px input row: 20 px `search`, input `type.body.lg` 16/24, scope chip «Σε: Ασφαλιστήρια», `Esc` kbd.
- Groups: Ενέργειες / Μετάβαση σε / Εγγραφές (Ασφαλιστήρια, Πελάτες, Ζημίες, Παραπομπές) / Πρόσφατα.
- 44 px items: icon, highlighted title, secondary line (mono ID · pill · fact), shortcut or «↵ Άνοιγμα».
- 32 px footer «↑↓ πλοήγηση · ↵ άνοιγμα · Ctrl+↵ νέα καρτέλα · Tab φίλτρο τύπου · ? σύνταξη».

**Syntax:** `ασφ:`/`pol:`, `ζημ:`/`clm:`, `πελ:`/`cus:`, `>` actions only, `#` IDs; natural commands («αλλαγή 4471», «ανάθεση σε Μαρία»); fuzzy, accent-insensitive, Greeklish.

**States:**
- empty query: 8 recent items + context suggestions
- results within ≤ 300 ms; stale results stay dimmed, never flash empty
- no results: «Καμία αντιστοιχία · Αναζήτηση σε πλήρες κείμενο (↵)»
- error: «Η αναζήτηση δεν είναι διαθέσιμη»; local actions still work
- AI on: a «Ρωτήστε» group (IB-30), labelled AI

**Keyboard:** **Ctrl/⌘+K toggles from anywhere, including inside inputs**; ↑↓; Enter; Ctrl+Enter opens in a new tab; Tab cycles the type filter; Backspace on an empty input removes the scope; Esc.

**ARIA:** modal dialog containing a combobox + listbox with groups, `aria-activedescendant`, polite count.

**Motion:** MI-32, 19.

### 7.37 AI surfaces (§4.37)

**Shared rules:** `ai` colours, `sparkles`, `gradient.iris` edge, a visible label («Πρόταση ΤΝ» / «Δημιουργήθηκε με ΤΝ»), an explain-why entry, and a **no-hole fallback**. On kill switch: unmount within 1 frame; accepted or edited text is kept as a normal draft.

- **Suggestion card (IB-09/22)** (plain card; buttons are RAC `Button` with the `ai` variant):
  - Anatomy: a 1 px Iris border via a masked pseudo-element (crisp radius); bg `status.ai.bg` at 40% over the sheet; header `sparkles` + «Σύνοψη ΤΝ» + confidence chip («Υψηλή βεβαιότητα 0,91»; low confidence uses the warning family) + model in a tooltip; body with superscript `[n]` citations (brand links, hover highlights the source); footer Αποδοχή (`ai` button), Επεξεργασία, Απόρριψη, Γιατί;, Πηγές (4).
  - States: generating (MI-50 border + MI-51 stream or skeleton + «Διακοπή»); proposed; accepted (200 ms collapse to the receipt «Αποδεκτή από … · 14:32 · Αναίρεση»; the value becomes normal with an `AI-generated` outline pill); edited («Τροποποιήθηκε»); rejected (150 ms fade to a receipt + reason menu); expired (stale banner «Τα δεδομένα άλλαξαν · Νέα πρόταση»); low confidence (not shown, only the link «Δείτε τη χαμηλής βεβαιότητας πρόταση»); error/timeout > 5 s (ai banner «Η πρόταση δεν είναι διαθέσιμη · συνεχίστε χειροκίνητα», no retry loop); disabled (fallback layout).
- **Approve-the-diff (IB-07)** (RAC `CheckboxGroup` per row): label → old value (tertiary, struck, 50% `danger.bg` underlay) → arrow → new value (primary 500, `success.bg` underlay) → a checkbox per row → «Αποδοχή επιλεγμένων (3)», Επεξεργασία, Γιατί;. Text uses a word diff (underline/strike + `+`/`−` glyphs). Accepting goes through normal validation and authority; values beyond authority become «Αποστολή για έγκριση». MI-52.
- **Explain-why (IB-08):** per §7.16. Factor bars: positive contributors `brand.solid`, negative **`ochre.solid`** (not danger), signed labels; sources open the viewer at the cited page.
- **Streaming draft (IB-10)** (RAC `TextArea`/editor): 2 px Iris caret (MI-51); «Διακοπή», then «Χρήση ως πρόχειρο», «Επαναδημιουργία», tone segmented (Επίσημο/Φιλικό), ΕΛ/EN toggle; banner «Πρόχειρο ΤΝ · ελέγξτε πριν την αποστολή» until edited and confirmed; customer documents get the AI statement automatically.
- **Agent plan panel (IB-06/31)** (in the context panel; a plain ordered list or `GridList`): task title; steps with icons (pending `circle`, running spinner in `ai.solid`, done `check` success, waiting-for-human `user-check` plum, failed `circle-x`) + label + elapsed time + expandable links; human gates render as plum cards with approve-the-diff and the decider's name; «Διακοπή εργασίας» always visible. MI-54.
- **Self-enriching field (IB-27):** see §7.2. Bulk version: banner «Βρέθηκαν 4 προτεινόμενες συμπληρώσεις… · Έλεγχος» → approve-the-diff side sheet.

### 7.38 Relationship graph shell (§4.38) — visx (non-approved, §0.1); keyboard is custom (`useKeyboard`); toggle = segmented

Used for RI programmes, party relationships, fraud links and corporate groups. **Always paired with a table view toggle** («Γράφημα / Λίστα»). Arrow keys follow edges; the selected node opens a quick-look; zoom 25–400% with Ctrl/⌘+scroll and buttons; expand animates with `spring.gentle` (off under reduced motion).

### 7.39 Notification bell and centre (§4.39) — RAC `Button` + non-modal drawer (§7.19)

A 20 px `bell` icon button with a count badge (attention for action-required items, neutral otherwise; cap «99+»). Opens a 400 px non-modal drawer. MI-26 swings the bell **only** for urgent items (breached clocks, assigned sanctions hits, conflicts on records being edited).

---

## 8. The gradient-border button (option 17), consolidated spec

Chosen from 20 explorations (`mockups/button-options.html`, "option 17: gradient border") and approved on 2026-10-08 as v3. It replaces v1 (smoked glass) and v2 (expressive).

**Technique** (CSS double background; no `border-image`, so the radius stays intact):

```css
.btn-primary, .btn-commit {
  border: 1.5px solid transparent;
  background:
    linear-gradient(var(--color-interactive-primary-bg), var(--color-interactive-primary-bg)) padding-box,
    var(--gradient-cta) border-box;               /* 120deg #8250F2 0% → #3F63F0 50% → #14988D 100% */
  color: var(--color-text-on-primary);            /* #161A21 / #EEF0F3 */
  font-weight: 600;
  border-radius: var(--radius-md);                /* 6px */
  box-shadow: 0 1px 2px rgba(15,23,42,.06);       /* dark: 0 1px 2px rgba(0,0,0,.4) */
  backdrop-filter: none;                          /* buttons are never glass */
}
.btn-commit { box-shadow: 0 1px 2px rgba(15,23,42,.06), 0 4px 14px -6px rgba(63,99,240,.45); }
/* dark commit: 0 1px 2px rgba(0,0,0,.4), 0 4px 16px -6px rgba(98,131,250,.5) */
.btn-primary:hover, .btn-commit:hover {
  background:
    linear-gradient(var(--gb-tint), var(--gb-tint)) padding-box,   /* rgba(63,99,240,.05) / D rgba(143,169,255,.07) */
    linear-gradient(var(--color-interactive-primary-bg), var(--color-interactive-primary-bg)) padding-box,
    var(--gradient-cta) border-box;
  box-shadow: 0 1px 2px rgba(15,23,42,.06), 0 6px 20px -8px rgba(63,99,240,.55);
  /* dark hover glow: 0 6px 22px -8px rgba(98,131,250,.6) */
}
.btn-primary:active, .btn-commit:active { transform: scale(.97); }  /* plus bg-pressed #EEF3FF / #1D2540 */
/* MI-67 sheen: ::after linear-gradient(105deg, transparent 30%, var(--gb-sheen) 50%, transparent 70%)
   gb-sheen = rgba(63,99,240,.12) / D rgba(143,169,255,.14); translateX -100%→100% once, 700 ms */
@media (forced-colors: active) {
  .btn-primary, .btn-commit { background: ButtonFace; border: 1.5px solid ButtonText; color: ButtonText; }
}
```

**Notes for the implementation:**
- The token doc gives hover fill `#F7F9FF` (`interactive.primary.bg-hover`) and pressed fill `#EEF3FF`. The v3 mockup reaches hover with a 5% tint layer instead. These are visually equivalent; prefer the tokens.
- The glow is a `box-shadow`. The performance rule bans animating `box-shadow`, so cross-fade a pseudo-element's opacity to animate the hover glow.
- **Disabled commit** removes both the gradient and the glow: `interactive.disabled.bg` + a 1.5 px `border.subtle` border + `text.disabled`, focusable with `aria-disabled`, and a tooltip with the blocker count.
- **Contrast:** each border stop is ≥ 3:1 against the surface (light 3.56–4.94, dark 5.11–6.88). The label is ≥ 15.7:1 (L) / 13.1:1 (D) on every fill state. Interpolated midpoints are not separately measured (open item).
- **Limits:** every primary button uses the border; only **one commit CTA per view** has the resting glow; never use the gradient on «Αποθήκευση πρόχειρου», secondary buttons, toolbars or rows.
- **Stale references** (split-button divider, MI-67 white sheen, checkbox/switch hover via `primary.bg-hover`): see §0.4.

---

## 9. Microinteraction catalogue (all 68, Part 2 §4.40)

All entries animate `transform`/`opacity` only (plus `clip-path` and SVG `stroke-dashoffset` where stated), are interruptible and never block input. "RM" = reduced motion. MI-59 and MI-61 are intentionally unused (the density switch is instant; inbox-zero is SM-07).

### 9.1 Feedback tier (≤ 150 ms): 26 entries

| ID | Name | One line (trigger → movement · timing · RM) |
|---|---|---|
| MI-01 | Hover fill | Pointer enters a button, row, card or menu item → bg/border tween to the hover token (CTA: hover tint + glow) · 100 ms `ease.feedback` · RM: instant colour |
| MI-02 | Press | pointerdown/Space → scale 1→0.97, springs back · 70 ms down, `spring.snappy` up · RM: pressed colour only |
| MI-03 | Focus ring snap | Keyboard focus → ring offset 4→2 px + opacity 0→1 · 100 ms `ease.standard` · RM: instant |
| MI-04 | Checkbox tick | Checked → fill scale 0.8→1 (70 ms) + check `stroke-dashoffset` 14→0 (120 ms from 30 ms) · 150 ms `ease.enter` · RM: instant |
| MI-05 | Radio dot | Selected → inner circle scale 0→1 · 120 ms `spring.snappy` (fallback `ease.enter`) · RM: instant |
| MI-06 | Switch throw | Toggle → thumb translateX 14 px with spring; squash 14→18 px while pressed; track colour tween · 150 ms · RM: instant |
| MI-07 | Row hover reveal | Pointer enters a row or card → bg tween; quick actions fade in with translateX 4→0 · 100 / 120 ms `ease.enter` · RM: actions appear, no translate |
| MI-08 | Row select | Row selected → left bar scaleY 0→1 from centre; bg to selected; header count ticks · 150 ms `ease.enter` · RM: instant |
| MI-09 | Inline save receipt | Inline edit committed → check draws (120 ms); «Αποθηκεύτηκε» fades in, holds 1,200 ms, fades 200 ms; underline flashes success over 600 ms · ack ≤ 120 ms · RM: instant check and caption, no flash |
| MI-10 | Copy confirm | Copy clicked → `copy`→`check` morph (scale 0.6→1, rotate −15°→0) + tooltip «Αντιγράφηκε», reverts after 1,500 ms · 150 ms · RM: icon swap |
| MI-11 | Tooltip in | Hover 400 ms or focus → opacity + 4 px translate from the anchor side · 120 ms in / 80 ms out · RM: opacity only |
| MI-12 | Tab indicator slide | Tab selected → 2 px indicator translates and scales to the new tab · 150 ms `spring.snappy` · RM: jump |
| MI-13 | Segmented thumb | Segment selected → thumb slides · 150 ms `spring.snappy` · RM: jump |
| MI-14 | Field error | Validation fails → message row expands (grid-rows 0fr→1fr) + fade; icon scale 0.8→1; on submit, the first invalid field shakes ±2 px ×3 and is focused · 150 ms; shake 180 ms · RM: no expand or shake; focus still moves |
| MI-15 | Status morph (IB-23) | Visible status changes → old pill up 6 px + fade (100 ms), new pill in from +6 px (150 ms), colour cross-fade · 150 ms · RM: 100 ms cross-fade |
| MI-17 | Shortcut reveal | Hold Ctrl/⌘ for 600 ms → kbd chips fade in next to actions · 120 ms in / 80 ms out · RM: instant |
| MI-18 | Drop target | Drag over a zone → `border.focus` dashed with marching ants (1 s loop) + drop-target fill · 100 ms · RM: static dashed |
| MI-19 | Highlight pill | Highlight moves in a menu, listbox or palette → one background element slides between items · 100 ms `ease.standard` · RM: jump |
| MI-21 | Sort flip | Header sort → arrow rotates 180° (150 ms); rows FLIP if ≤ 50 visible move (240 ms) · RM: instant reorder |
| MI-22 | Pinned edge | Horizontal scroll leaves 0 → pinned-column shadow opacity 0→1 · 100 ms · RM: instant |
| MI-23 | Cell edit open | Enter/F2/click on an editable cell → focus box offset 3→0; editor at identical metrics · 100 ms · RM: instant |
| MI-25 | Chip in/out | Filter added or removed → in: scale 0.9→1 + opacity (`spring.snappy`); out: scale 0.9 + fade · 150 / 100 ms · RM: opacity |
| MI-26 | Urgent bell | Urgent notification → bell rotates ±12° ×2 + badge rolls · 400 ms once (a deliberate exception) · RM: badge only |
| MI-62 | J/K cursor | J/K/arrows → **no animation**; ring moves instantly; detail from cache in the same frame · 0 ms · RM: same |
| MI-64 | Unsaved dot | First edit in a form or side sheet → 6 px warning dot scale 0→1 · 150 ms `spring.snappy` · RM: instant |
| MI-66 | Toggle disclosure | Section or accordion toggled → chevron rotates 90° · 150 ms · RM: instant |

### 9.2 Transition and ambient tier (200–400 ms plus stated exceptions): 42 entries

| ID | Name | One line |
|---|---|---|
| MI-16 | Number ticker | A live dashboard value, count or footer total changes → changed digits roll (up or down) + favourability colour flash returning over 600 ms · 400 ms · **never** on approval or financial inputs · RM: instant swap |
| MI-20 | Presence join/leave | Collaborator joins → avatar scale 0.6→1 (`spring.smooth`); leaves → scale 0.6 + fade 150 ms · ≈300 ms · RM: fade |
| MI-24 | Bulk bar rise | First row selected → bar translateY 16→0 + opacity (`spring.smooth`), count ticks; out 200 ms `ease.exit` · RM: opacity 120 ms |
| MI-27 | Banner/section expand | Banner appears or section expands → grid-rows 0fr→1fr + opacity · 200 ms; collapse 150 ms · RM: instant |
| MI-28 | Popover open | Popover, menu, listbox, date picker or hover card → scale 0.96→1 from the trigger side + opacity + translateY 4→0 · 200 ms `ease.enter`; close 150 ms · RM: opacity 100 ms |
| MI-29 | Modal open | Scrim 0→1 (200 ms); dialog scale 0.98→1 + translateY 8→0 + opacity (320 ms); glass fades with it · close 240 ms · RM: opacity 120 ms |
| MI-30 | Panel slide | Drawer, side sheet or context panel → translateX 100%→0 (`spring.smooth`), inner sections staggered 16 ms (max 8) · ≈320 ms; close 240 ms · RM: opacity 120 ms |
| MI-31 | Toast stack | Toast in → translateY 16→0 + scale 0.96→1; older toasts step back (scale .95/.90, y −8/−16, opacity .9/.7); hover expands; hairline shrinks · ≈300 ms · RM: opacity, plain list |
| MI-32 | Palette open | Ctrl/⌘+K → scrim 200 ms; palette scale 0.97→1 + opacity; input focused at t = 0; results stagger 16 ms (first 8); reflow `spring.gentle` · 240 ms; close 150 ms · RM: opacity 100 ms, no stagger |
| MI-33 | Skeleton to content | Data after the 150 ms delay → shimmer (30% highlight sweep every 1,200 ms); each block cross-fades in (200 ms) + translateY 4→0, staggered 16 ms · RM: static tone, 100 ms fade |
| MI-34 | Detail swap | Split-pane selection by click → header shared-element morph + body cross-fade · 200 ms · **off during J/K key-repeat** · RM: instant |
| MI-35 | List to record | Open a record → row title and pill FLIP to the header; sheet reveals via `clip-path: inset()` from the row rect; View Transitions with a Motion fallback · 400 ms `ease.emphasized` · RM: 150 ms fade |
| MI-36 | Header collapse | Scroll past 72 px → title and ID move to the breadcrumb, facts fade; a scroll-timeline over 72 px · scroll-linked · RM: swap at the threshold |
| MI-37 | Tab panel change | Tab selected → old panel fades (100 ms); new panel opacity + translateX 8 px in the tab direction · 200 ms `ease.enter` · RM: 100 ms fade |
| MI-38 | Wizard step | Next/back → body slides 24 px + fade; connector `gradient.tide` scaleX 0→1; node check draws · 320 ms `ease.emphasized` (check 150) · RM: 120 ms fade, instant connector |
| MI-39 | Filter apply | Filter change → affected widgets dim to 60% + a 2 px bar; on data they restore and values tick · 200 ms · RM: opacity swap |
| MI-40 | Work moves on (IB-23) | Item leaves a list → row translateX −24 + fade, height collapses (240 ms); destination count pulses 1→1.15→1 (200 ms) and ticks; IB-12 progress advances · RM: 120 ms fade, counts swap |
| MI-41 | Optimistic pending | Optimistic mutation → value shows immediately + a 2 px tide shimmer until confirmed; on failure it cross-fades back + inline error · shimmer 1,200 ms loop, revert 200 ms · RM: static line, instant revert |
| MI-42 | Upload complete | File through the pipeline → ring completes, morphs to a check (150 ms), row flashes `success.bg` over 600 ms · 300 ms · RM: check, no flash |
| MI-43 | Progress done | Determinate bar reaches 100% → fill turns success + end-cap check draws · 300 ms · RM: instant |
| MI-44 | Month slide | Calendar month change → grid slides 24 px + cross-fade · 200 ms · RM: cross-fade |
| MI-45 | Async results | Combobox or search fetch → 2 px bar after 150 ms; old results dim to 60% · 200 ms · RM: static bar |
| MI-46 | Editing pulse | Collaborator editing → presence ring pulse scale 1→1.15, opacity .6→0 · 1,600 ms loop · RM: static ring |
| MI-47 | Co-cursor | Collaborator cursor moves (template editor and product-version review only) → interpolated position; name label fades after 3 s idle · 80 ms linear · RM: no interpolation |
| MI-48 | Live insert | New timeline event or row → height from 0 (200 ms) + `brand.bg` flash fading over 1,200 ms · RM: static «Νέο» dot for 60 s |
| MI-49 | Pin pulse | New comment or thread selected → pin scale 0→1 (`spring.smooth`) + one ripple (scale 1→2.2, opacity .5→0) · 600 ms · RM: static highlight ring for 2 s |
| MI-50 | AI thinking edge | AI generating → conic Iris border rotates 360° · 2,400 ms loop, stops at 10 s with fallback · RM: static gradient + «Δημιουργείται…» |
| MI-51 | AI stream | Tokens arrive → each chunk fades 0→1; Iris caret blinks · 120 ms per chunk, caret 1,000 ms · RM: no fade, static caret |
| MI-52 | Diff accept | Αποδοχή → strike line draws over the old value (150 ms); new value FLIPs into the field; an `AI-generated` outline settles · 320 ms `ease.emphasized` · RM: instant swap + marker |
| MI-53 | Factor bars | Explain-why opens → bars scaleX 0→value, staggered 24 ms (values in the DOM from frame 0) · 240 ms `ease.enter` · RM: static |
| MI-54 | Agent step | Plan step changes → icon morph pending→spinner→check; finished steps compress to one line · 200 ms · RM: instant |
| MI-55 | Quick-look | Hover 500 ms or Space on a record link → MI-28 popover with skeleton if not prefetched · 200 ms · RM: opacity |
| MI-56 | Hero trend draw | Hero or KPI first render → sparkline draws L→R (`stroke-dashoffset`); comparison chip slides in 200 ms later; value counts with MI-69 · 600 ms `ease.emphasized` · RM: static |
| MI-57 | Sparkline scrub | Pointer or keyboard over a sparkline → crosshair follows; point 3→5 px; value tooltip · 100 ms · RM: instant |
| MI-58 | Theme switch | Theme toggled → View Transition circle reveal from the toggle (`clip-path: circle(0→150%)`) on High, cross-fade elsewhere · 400 ms / 200 ms · RM: instant |
| MI-60 | Seen fade | Notification visible for 1 s → unread dot scales to 0 + fades · 200 ms · RM: instant |
| MI-63 | Connectivity | Connection lost or restored → status-bar segment morphs (MI-15); offline banner slides down; on restore it turns success «Συνδεθήκατε ξανά» for 2 s then retracts · 200 ms · RM: instant |
| MI-65 | Save nudge | Navigation with unsaved changes → save bar lifts 4 px and returns (`spring.snappy`), unsaved dialog opens (MI-29) · 200 ms · RM: dialog only |
| MI-67 | Commit ready sweep | Commit CTA becomes enabled (last blocker cleared) → specular sheen sweeps translateX −100%→100% **once** · 700 ms `ease.standard` (a once-per-state-change exception) · RM: no sweep (**use the v3 sheen colour**, §0.4) |
| MI-68 | Drag lift | Row, column, card or file dragged → scale 1.02 + `elevation.3` (via pseudo-element opacity); drop with `spring.snappy`; others make room · 150 ms lift, ≈220 ms settle · RM: no scale, instant reflow |
| MI-69 | Count-up on arrival | Home or a dashboard opens → hero, KPI, work-card counts and statement headlines count from 0 with el-GR grouping and decimals kept; `aria-label` holds the final value from frame 0 · 1,300 ms, ease-out 1−2^(−10t) · **dashboards only** · RM: final value instantly |
| MI-70 | Ambient drift | Always, on the shell canvas and canopy → blurred colour layers translate ±6% and scale 0.95–1.10, alternating · 24–36 s per layer `ease-in-out` · stops on Low tier · RM: static |

**Signature tier (listed for completeness; storyboards in Part 4 §10):**
- SM-01 Quote bound
- SM-02 Policy issued with MARK
- SM-03 Claim payment released / settled
- SM-04 Period closed
- SM-05 FNOL claim-number reveal
- SM-06 Referral decided with authority
- SM-07 Queue cleared (inbox zero)
- SM-08 Session start (welcome, first load each day after sign-in)

Rules: once per business object per user, never on bulk, non-modal with `pointer-events: none` decoration, `z.signature` 750, max 1,600 ms decorative, switched off by «Εορτασμοί».

---

## 10. Implementation checklist derived from Parts 1–2 (things that need code, not just CSS)

1. Token pipeline: consume `aegean.css` as-is. Add TS exports for springs, breakpoints and field widths (they are absent from the CSS), and fix the doc/CSS naming drift (§0.5).
2. `toGreekUpper()` plus its mandatory unit tests. Use it in Overline and EnvRibbon.
3. Greek search normaliser (NFD strip, ς≡σ, ELOT 743 Greeklish), shared by Combobox, Palette, Table search and Select typeahead.
4. el-GR money parser and formatter (paste heuristics, shorthand, reject extra precision, amount in words); percentage and per-mille; region format separate from language.
5. Date accelerator parser (σ/α/+n/±nμ/ε/τμ, compact digits) and el-GR long-date formatting (genitive month).
6. ID masks and validators: ΑΦΜ mod-11, IBAN mod-97 with bank lookup, Greek plate Latin→Greek conversion, VIN.
7. `GlassBudget` service (N = 3, priority, `data-material="solid"`), device-tier probe and preference store (theme, density, reduce motion, reduce transparency, images, effects tier, celebrations, single-key shortcuts).
8. The aria-disabled-with-reason pattern applied to every RAC control.
9. Global keyboard layer: Ctrl/⌘+K, F6 regions, F8 toasts, `[`, `Shift+[`, `Ctrl+.`, `?`, J/K/X/O/E/F/`/`/G V, Alt+←/→ in wizards, Ctrl/⌘+Enter commit, hold-Ctrl kbd reveal. Single-key shortcuts disabled in inputs and switchable off.
10. CI gates: font-coverage check (fontTools), `contrast.py`, lint rules (raw values, animated properties, tint α minimums, `text.tertiary` on glass), and a Playwright perf trace on a low-end VM.
11. Every MI implemented as CSS (feedback tier) or Motion (springs, FLIP), each with an explicit RM branch driven by the in-app preference.
