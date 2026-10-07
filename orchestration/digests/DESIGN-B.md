# DESIGN-B digest: Aegean design system, Parts 3–4, plus the approved v3 mockup

**Sources read in full:**
- `core-insurance-prds/design-guide/design-system-part-3.md` (600 lines): §5 Patterns, §6 Data visualisation, §7 Iconography and illustration
- `core-insurance-prds/design-guide/design-system-part-4.md` (924 lines): §8 Content and localisation, §9 Accessibility checklist, §10 Signature moments, §11 Developer handoff
- `core-insurance-prds/design-guide/mockups/aegean-screens-v3.html` (1,136 lines): the **approved** visual reference (v1/v2 in `_deprecated/` were not used)
- `tools/contrast-report.txt` (242 lines, 0 failures) and `tools/dataviz-report.txt` (23 lines, 0 failures), skimmed

**Cross-checked (targeted greps only):** Part 1 and Part 2 for the facts Parts 3–4 depend on (button spec, C-07 and MI-69, typography, shell dimensions, breakpoints, library choices); `tokens/aegean.css`; `design-guide/README.md`; `mockups/_deprecated/README.md`; `core-insurance-infra/ARCHITECTURE-DECISIONS.md`.

**Binding stack (ARCHITECTURE-DECISIONS.md):** React 19 + TS + Vite served by ASP.NET Core; React Router, TanStack Query, React Hook Form + Zod, **react-i18next**, React Aria; Vitest and Playwright; Entra ID with **`Microsoft.Identity.Web` and MSAL React**.

Section numbers like "§5.1" refer to the design guide. "MI-nn" are microinteractions (Part 2 catalogue). "IB-nn" are inspiration-board patterns. "C-nn" are the aesthetic-vs-usability conflict rulings in Part 1. "RM" means reduced motion.

---

## 0. TL;DR for implementers

1. **Build to v3 and Parts 1–2.** Parts 3–4 contain some **stale pre-v3 text**: the old solid-blue primary button and the blue `gradient.cta` (in §11.3, §11.4 and SM-01), and "no ticker on first render" (in §6.2 and SM-08). Parts 1–2, `tokens/aegean.css` and the mockup were updated on 2026-10-08. Where they disagree with Parts 3–4, the newer sources win. The full list is in §13.
2. **Primary button = surface fill + 1.5 px violet → blue → teal gradient border** (option 17), semibold label in `text.primary`. The commit CTA (`primary-gradient`) is the same shape with a resting glow, and there is **one per view**.
3. **i18n conflict.** The guide specifies **FormatJS `react-intl`** (§8.6, plus the §11.7 code uses `useIntl`). The stack mandates **react-i18next**. Keep ICU MessageFormat semantics by adding `i18next-icu` + `intl-messageformat`, or use i18next-native plurals. Either way it needs a decision.
4. **New npm dependencies the guide assumes** that are not in the ADR list: `motion` (Motion for React), `@visx/*` (charts), `lucide-react` (icons), `@tanstack/react-table` and `@tanstack/react-virtual`, `pdfjs-dist`, `libphonenumber-js` (or equivalent), Storybook (implied by `*.stories.tsx`), Stylelint plus custom ESLint rules, and `@axe-core/playwright`. Fonts: self-hosted **Inter**, **Noto Sans**, **JetBrains Mono** and **Noto Sans Mono** (all SIL OFL). The token build is a **Python** script that must run in CI.
5. **Greek rules live in code, not CSS.** Use `toGreekUpper()` (no tonos in capitals, dialytika added, final sigma), `formatMoney()` (U+2212 minus, NBSP before €), `Intl.Collator('el', {sensitivity:'base', numeric:true})`, accent-insensitive and Greeklish-tolerant search, and **vocative** greetings only after the user confirms them. The v3 mockup itself breaks the uppercase rule: it calls JS `toUpperCase()` on «Μετάβαση σε» and «Αυτοκίνητο ΙΧ», which keeps the tonos.

---

# Part A: Section 5, Patterns

Every pattern in the source lists: Traces to, Layout, Flow, States, Motion, Keyboard and ⚖ function-first calls. Greek copy is the source text.

## A.1 §5.1 Quote-to-bind wizard (motor first, then home, then commercial)

**Traces to:** POL submission job Draft → Quoted → Bound (contract §3.2.4); RAT rating ≤ 200 ms and quote ≤ 2 s (§3.9.7); UW issues by blocking point; BIL down-payment bind gate; PTY sanctions `REQ-PTY-006`; DOC pre-bind documents and IPID; CHN broker quoting (UIL-A1, UIL-P1); IB-05, 07, 08, 21, 32.

**When to use:** new business submission (staff and broker). Also the **compact change wizard** for endorsements (§5.3).

**Layout (Part 1 §3.4.5 wizard template):**
- Record header: «Νέα αίτηση · Αυτοκίνητο ΙΧ» · job id `ΕΡΓ-2026-018233` · `[Πρόχειρο]` pill · presence.
- Three columns: **Stepper 220 px** | **Step body max 960 px** | **Live quote panel 320 px**.
- Footer: «Αποθήκευση πρόχειρου · Αποθηκεύτηκε 14:31» on the left; `[Πίσω]` and `[Επόμενο: Καλύψεις  Ctrl+↵]` on the right.
- Part 1 (grep) says the sticky summary is on the right at 320 px when the viewport is ≥ 1440 px. Below 1440 px it collapses into a **56 px bottom summary bar**. The running premium never uses a ticker (C-07).

**Steps:**

| # | Step (GR / EN) | Key UI | Streamlining |
|---|---|---|---|
| 1 | Πελάτης και προϊόν / Customer and product | Party combobox (§4.4) with Greeklish match; «Νέο πρόσωπο» opens an inline side sheet; product and offering; effective date (§4.5) with a retroactive limit | gov.gr Wallet prefill (CHN), with fields shown as lookup-suggested and their source; ΑΦΜ check |
| 2 | Προκαταρκτικές ερωτήσεις / Pre-qualification | PFC question set. Yes/no questions are **segmented controls, not radios**. Knockout answers show an inline danger banner immediately | Only questions whose display conditions are true are shown |
| 3 | Όχημα και οδηγοί / Vehicle and drivers | Plate input (§4.7) → lookup fills make, model, year and cc as lookup-suggested; drivers in an inline-editable table (§4.35.7) with `+ Οδηγός` | Plate-first entry removes about 8 fields |
| 4 | Καλύψεις / Coverages | **Offering choice cards** (§4.9) «Βασικό / Άνετο / Πλήρες», each with a live price, then a coverage matrix table with coverage terms as selects. Dependent coverages auto-select with a 1-line info caption | Live price on every card (≤ 200 ms) |
| 5 | Ανάληψη κινδύνου / Underwriting | UW issues grouped by blocking point (pre-quote, pre-bind, pre-issue). Each shows the rule, the triggering values, «Γιατί;» and either «Αίτημα έγκρισης» (referral, plum) or «Έγκριση» when the user has authority (authority meter §4.6.2) | Non-blocking issues collapse into one line |
| 6 | Προσφορά / Quote | Up to 3 **quote versions** side by side (`xl` compare view), with differences highlighted on a `status.brand.bg` underlay; «Αντίγραφο έκδοσης», «Ορισμός ως κύρια» | Versions instead of copied submissions |
| 7 | Πληρωμή και έγγραφα / Payment and documents | Payment plan choice cards (instalment schedule preview as a mini statement, IB-13); payment method; IPID and demands-and-needs preview (document viewer side sheet); consent checkboxes with exact text (PTY) | Documents are generated as previews in the background during step 6 |
| 8 | Δέσμευση / Bind | **Bind gate checklist** (IB-32 binary): sanctions clear, UW blocking issues resolved, down payment received or allowed by the plan, mandatory documents generated, demands-and-needs signed. Each gate is pass/fail with a fix link. Commit CTA «Δέσμευση ασφαλιστηρίου» | Gates are checked continuously, so there are no surprises at step 8 |

**Live quote panel (§5.1.3, C-07):**
- Recalculation starts **300 ms after the last change** (debounce). While it runs, «Επανυπολογισμός…» appears in `text.tertiary` beside the previous value, which is dimmed to 60%. The panel is **never blank**.
- When the new value arrives, the old value moves into a small struck-through line («~~398,98 €~~») with a delta chip «+13,40 €» (brand family; the word «αύξηση» is in the accessible name). The new value cross-fades in over **150 ms**. **No digit rolling.**
- «Γιατί;» opens an explain-why popover with the worksheet summary (factor bars, MI-53) and a link to the full RAT worksheet.
- The bind gates block at the bottom of the panel shows any pre-bind blocker with a direct link.

**Referral inside the wizard (§5.1.4):** the issue card offers «Αποστολή σε αναλήπτη». This opens a side sheet with a pre-filled note (an AI draft is optional, IB-10) and the target queue. On send, the header gets the `Referred` pill (plum) and the Bind step is locked with the reason «Αναμένεται απόφαση ανάληψης». When the decision arrives (notification and live update), the issue card morphs (MI-15) to Approved or ApprovedWithConditions. If that was the last blocker, the commit CTA enables with **MI-67**.

**States (§5.1.5):**

| State | Behaviour |
|---|---|
| Autosave | Every field commit saves the draft (debounced **800 ms**). The footer shows «Αποθηκεύτηκε 14:31» (`text.tertiary`), or «Αποθήκευση…» while saving |
| Locked by another user | `locked` pill with the holder; all steps read-only; «Ζητήστε πρόσβαση» notifies the holder |
| Preempted (POL rebase) | Warning banner «Η εργασία χρειάζεται επανευθυγράμμιση · Προβολή αλλαγών» with a diff side sheet |
| Rating unavailable | Panel shows a danger inline «Δεν ήταν δυνατός ο υπολογισμός · Επανάληψη». Next stays enabled for data entry; Bind is disabled |
| Policy hold (UW) | Page-level warning banner, e.g. «Αναστολή εργασιών για Τ.Κ. 19xxx (πλημμύρες) έως 15/10»; Bind locked |
| AI off | Prefill suggestions disappear; manual fields are identical (no hole in the layout) |

**Bind and issue (§5.1.6):** «Δέσμευση ασφαλιστηρίου» opens a confirmation modal (`md`) with a key–value summary: policyholder, vehicle, period, total premium and plan, first instalment. If not already recorded, it adds the checkbox «Ο πελάτης έλαβε το IPID και τους όρους». The button is «Δέσμευση». On success → **SM-01**. The issuance pipeline then runs (documents, then fiscal registration via CMP) with a progress toast. On MARK registration → **SM-02**.

**Keyboard:** `Alt+→/←` next/back. `Ctrl/⌘+Enter` runs the step's primary action; on step 8 it **opens the confirmation and never binds directly**. `Ctrl/⌘+S` saves the draft. `G` then `1–8` jumps to a step.

## A.2 §5.2 Policy record view (policy file)

**Traces to:** POL policy, term, jobs, transactions and segments (bitemporal, reverse-and-reapply); UIL-U3 policy 360; DOC Documents tab (CD-09); CLM and BIL summaries; IB-05, 13, 14, 19.

**Layout:** record template (Part 1 §3.4.4).
- **Header:** overline «ΑΣΦΑΛΙΣΤΗΡΙΟ · ΑΥΤΟΚΙΝΗΤΟ ΙΧ»; the title is the policyholder name; ID `ΑΣΦ-2026-004471` (mono, copyable); term status pill; flags.
- **Key facts:** «Περίοδος 01/03/2026–28/02/2027 · Ετήσιο ασφάλιστρο 412,38 € · Παραγωγός 10233 Νικολάου Ασφαλιστική · Πρόγραμμα 4 δόσεις».
- Presence stack. Actions: «Αλλαγή» (primary) and «⋯» (Ακύρωση, Επανέκδοση, Ανανέωση τώρα, Μεταφορά …).
- **Tabs:** Σύνοψη · Καλύψεις · Κίνδυνοι · Συναλλαγές · Χρεώσεις & πληρωμές · Ζημίες · Έγγραφα · Δραστηριότητες · Ιστορικό.

**Policy spine (§5.2.2): the signature visual of the record.** A 72 px effective-time strip across the full sheet width at the top of Σύνοψη.

| Element | Spec |
|---|---|
| Axis | Term start → term end. Previous and next terms at 40% opacity (scrollable). Month ticks use Greek short months («Μαρ», «Απρ»). Today marker: 1 px `border.focus` vertical line labelled «Σήμερα» |
| Segments | Bars 24 px tall, `radius.sm`, `status.brand.bg` with a 1 px `status.brand.border`. Boundaries fall at transaction effective dates |
| Transactions | 10 px diamonds on the axis, coloured by job type: submission brand, change teal, cancellation danger, reinstatement success, renewal info. Hover or focus opens a quick-look with job, record time and premium delta |
| Out-of-sequence | Reversed transactions are hollow diamonds, with a dashed connector to their reapplied version |
| Status bands | PendingCancellation and Cancelled periods as **hatched** overlays (45°, 4 px pattern, `status.warning.solid` / `danger.solid` at 30%): a pattern, not colour alone |
| As-of slider | A handle on a second thin record-time axis. Dragging it renders the spine **and the whole record as known at that time** (contract §3.5.5). A `stale`-style banner reads «Προβολή όπως ήταν στις 12/05/2026 10:14 · Επιστροφή στο τώρα». Keyboard: with the handle focused, ←/→ steps through transaction record times |
| Motion | Segments grow in (scaleX from the left, 400 ms `ease.emphasized`, 24 ms stagger) on first render (MI-56 family). As-of changes cross-fade the record (200 ms) |
| RM | Static; as-of swaps instantly |
| Accessible alternative | A «Προβολή ως πίνακας» toggle shows the segments and transactions as a table |

**Summary tab body (§5.2.3), 8 + 4 columns:**
- **Left (8):** key–value groups: Policy, Risk summary, Coverages summary (limits and deductibles), Producer, Billing summary (mini statement, IB-13), Claims summary («2 ζημίες · 1 ανοιχτή · Πληρωθέντα 1.420,00 €»).
- **Right (4):** AI summary card (IB-09, when AI is on, with citations), upcoming dates (renewal window, next instalment, statutory clocks), open activities.

**Inline edit rules (§5.2.4):** only non-contractual attributes are edited in place (IB-14): correspondence preferences, internal notes, tags. Every contractual field shows `lock` with «Απαιτείται πρόσθετη πράξη» and a link that starts a policy change pre-scoped to that field.

## A.3 §5.3 Endorsements and mid-term adjustments (policy change)

**Traces to:** POL policy change jobs, premium preview and dry-run, out-of-sequence handling, effective-date permissions; RAT proration; BIL re-spreading; UIL-A2.

**Flow:**
1. **Start from intent.** «Αλλαγή» opens a palette-like picker «Τι αλλάζει;» (Αλλαγή οχήματος, Προσθήκη οδηγού, Αλλαγή διεύθυνσης κινδύνου, Αλλαγή καλύψεων, Αλλαγή πληρωτή, Άλλο). The choice opens a **compact change wizard** with only the affected steps (typically 2–3 instead of 8).
2. **Effective date first**, with the permission window (§4.5 unavailable days with reasons). If a later transaction exists, show the **out-of-sequence warning** immediately: «Η αλλαγή είναι αναδρομική πριν από 1 μεταγενέστερη συναλλαγή · θα γίνει αντιλογισμός και επαναφορά», listing the affected transaction.
3. **Edit.** Changed fields get a **change marker**: a 2 px left bar in `status.brand.solid` plus a caption with the old value, «Πριν: ΙΚΧ-1234».
4. **Premium preview (dry-run):** a diff table per coverage with columns «Πριν / Μετά / Διαφορά / Αναλογία περιόδου». The headline is the prorated charge, e.g. «Επιπλέον χρέωση 37,15 € για 146 ημέρες». Adverse and favourable semantics follow §3.9.9. Refunds use the success family and «Επιστροφή» wording.
5. **Billing impact preview:** how the change re-spreads over the remaining instalments (mini statement).
6. **Bind change** with the commit CTA «Δέσμευση αλλαγής» → confirmation → MI-15 status updates, and the spine adds a diamond (MI-48 style). **SM-01 does not fire** for changes (C-05). Instead, one toast with «Προβολή εγγράφου πρόσθετης πράξης».

**States:** as §5.1.5, plus «Σύγκρουση εκδόσεων» (a solid `conflict` pill) when another change was bound on the same term during editing. The resolution side sheet is a three-column merge (**Βάση / Δική σας / Δεσμευμένη**) with a per-field choice.

## A.4 §5.4 Claims FNOL (staff intake) and customer/broker FNOL

**Traces to:** CLM FNOL from every channel, including the joint accident report (Friendly Settlement); policy verification against POL snapshots; UIL-C1; CHN UIL-C13; WRK inbound documents; CMP statutory clocks; AI FNOL from photos, voice and free text (contract §3.8.6).

**Layout:** a **single scrolling page with anchored sections**, not a stepper, because FNOL is often taken live on the phone (⚖).
- Header: «Νέα αναγγελία ζημίας» · ΖΗΜ-πρόχειρο · «Καλών: Κ. Νικολάου» · a ⏱ call timer.
- Three columns: **section rail** | **sections 1–7** | **assistant/evidence panel**. The right panel holds AI extraction from free text and photos (approve-the-diff), a coverage check and similar claims (optional).

| Section | Key UI |
|---|---|
| 1 Ασφαλιστήριο | Combobox search by plate, policy number, ΑΦΜ or name. The result card shows **coverage as at the loss date** (POL snapshot), not today: «Σε ισχύ στις 03/10/2026 · Καλύψεις: ΑΜ, Οδική, Θραύση κρυστάλλων». If nothing is found: «Συνέχεια χωρίς ασφαλιστήριο» |
| 2 Συμβάν | Loss date and time with **no default value** (empty and required). Location: address combobox plus a map pin (drag to adjust, reverse geocode, Greek address formats). Loss cause select. Free-text description that AI extracts into fields (approve-the-diff, IB-07). A CatEvent selector appears and is pre-selected when an event covers the date and area (UIL-C11) |
| 3 Εμπλεκόμενοι | A card per party with role chips (οδηγός, ιδιοκτήτης, μάρτυρας, τρίτος). Third-party plate plus insurer lookup |
| 4 Δήλωση ατυχήματος | **17 circumstance checkboxes per vehicle** in two columns (Όχημα Α / Όχημα Β). A **sketch canvas** with road templates and vehicles dragged as arrows, or a photo of the paper report with AI extraction. Signatures yes/no. Live eligibility as an IB-32 binary: «Επιλέξιμη για Φιλικό Διακανονισμό ✓» |
| 5 Ζημιές & τραυματισμοί | Injury data is **P3**. The header shows `shield` + «Ευαίσθητα δεδομένα υγείας · πρόσβαση με ειδικό δικαίωμα»; fields are masked for users without permission |
| 6 Έγγραφα & φωτογραφίες | File upload (§4.30) with camera capture on tablets. AI classifies files as suggestions («Φωτογραφία ζημιάς · πίσω προφυλακτήρας») |
| 7 Έλεγχος & υποβολή | Summary plus a completeness meter (IB-12) with required fields per loss type. Shows the statutory clock that will start: «Η προθεσμία προσφοράς 3 μηνών ξεκινά με την υποβολή». Commit CTA «Υποβολή αναγγελίας» |

**Section rail:** filled dot = complete, half = partial, hollow = not started, danger dot = has errors. Clicking scrolls smoothly over 300 ms (instant under RM). An IntersectionObserver highlights the section in view.

**Submit (§5.4.2):** → **SM-05**. Auto-created WRK activities are listed beneath, with a «Αποστολή επιβεβαίωσης στον πελάτη» toggle (default per preference) that shows the channel. **Customer/broker FNOL** (CHN) uses the same sections as a **5-step mobile wizard** in touch density, with photo-first capture and the SM-05 mobile variant.

**States (§5.4.3):**
- Draft autosave, as in §5.1.5.
- Duplicate detection: warning banner «Πιθανή διπλή αναγγελία: ΖΗΜ-2026-031002, ίδιο όχημα, ίδια ημερομηνία · Προβολή · Συνέχεια ως νέα».
- Policy not in force at the loss date: danger banner, but **submission is still allowed** (coverage is a human decision, contract §3.8.3).
- Sanctions hit on a party: a payment-block flag is noted and FNOL continues.

## A.5 §5.5 Claim workbench (handler workspace)

**Traces to:** UIL-C2 to C8, UIL-C10; IB-04, 05, 09, 13, 25; contract §3.9.2 (payments and reserves above authority need a checker).

**Layout:** split pane (Part 1 §3.4.3).
- **Left:** a priority-ranked claims queue (IB-04) with sidebar work views: Οι ζημίες μου 34, Προθεσμίες εβδομάδας 6, Νέα έγγραφα 11, Εγκρίσεις 3.
- **Right:** the claim record.
  - **Header:** claim number, status, claimant, policy link (quick-look), loss date, cause, cat event chip, and the **statutory clock pill** («Προσφορά σε 23 ημ.»; warning at ≤ 10 days; solid danger when breached). Presence. Actions: Πληρωμή, Απόθεμα, Ανάθεση, ⋯.
  - **Claim strip (signature visual):** a horizontal journey bar FNOL → Επαφή → Εκτίμηση → Προσφορά → Πληρωμή → Κλείσιμο. The current stage is in brand; completed stages show `check`; the statutory deadline is a flag at its proportional position.
  - **Tabs:** Σύνοψη · Εκθέσεις · Οικονομικά · Ανακτήσεις · Έγγραφα · Ημερολόγιο · Εμπλεκόμενοι · Ιστορικό.
  - **Σύνοψη:** AI summary card with citations and «Πρόταση αποθέματος» (approve-the-diff into the reserve; maker-checker above authority; comparable claims as explain-why evidence), an exposures grid (coverage × claimant, status, reserve, paid, incurred) and next actions.
  - **Οικονομικά:** a statement view (IB-13). Headline «Πραγματοποιηθείσες 8.420,00 €» = Πληρωθέντα 3.200,00 + Απόθεμα 5.220,00, then lines per exposure × cost type. Recoveries show as favourable movements; pending approvals in plum.

**Task-adaptive layout for catastrophe events (§5.5.2, IB-25):** when the claim has a cat code:
- the header gains a cat banner («Πλημμύρες Θεσσαλίας 10/2026 · 1.284 ζημίες»);
- the summary promotes the property-damage checklist;
- the vendor assignment card (UIL-C9, map and capacity) moves to the top;
- the diary shows cat-specific SLAs.

The reshaping uses only approved components and announces itself: «Η προβολή προσαρμόστηκε για καταστροφικό γεγονός · Κανονική προβολή». The transition uses MI-35-style shared elements (400 ms) the first time per session, then is instant.

**Payments (§5.5.3):** «Πληρωμή» opens a side sheet (`lg`) with:
- payee: a combobox over claim parties showing **VoP status** and **sanctions status**;
- exposure and cost type;
- amount: currency input with **amount in words** and an authority meter;
- method;
- preview: «Μετά την πληρωμή: απόθεμα 2.020,00 €» (and the auto-reserve-reduction rule).

On submit: within authority → «Έγκριση και αποδέσμευση» → **SM-03** on release. Above authority → «Αποστολή για έγκριση» → TransactionSet `PendingApproval` (plum) → the checker's approval inbox (§5.14).

## A.6 §5.6 Billing and payments

**Traces to:** UIL-B1 to B8; IB-13, IB-28.

- **Billing account (§5.6.1), statement-first.** Headline «Οφειλή προς εμάς 309,29 €» + «Επόμενη δόση 103,10 € στις 01/11/2026». Then the **instalment schedule** as a horizontal timeline of invoice pills on a date axis: Planned neutral, Billed info, Due info, Paid success, Overdue danger. It uses the same visual grammar as the policy spine. Then line items. Actions: «Καταχώριση είσπραξης», «Αλλαγή προγράμματος», «Αλλαγή τρόπου πληρωμής», «Επιστροφή».
- **Suspense and unapplied cash (§5.6.2), exception-first.**
  - Queue columns: payer text, reference, amount, date, and up to 3 **suggested match chips** with confidence («ΤΙΜ-2026-118223 · 103,10 € · 98 %»).
  - Hovering a chip previews the allocation. Keys `1`/`2`/`3` accept the n-th suggestion. `A` opens manual allocation (side sheet with invoice search and an allocation waterfall preview).
  - Accepting runs **MI-40** (the row leaves) and advances «Αντιστοιχίστηκαν 182 από 240 σήμερα». Allocations above a threshold go to approval.
- **Refund approval (§5.6.3).** Columns: reason, amount, payee, **IBAN with VoP result** («Επαλήθευση δικαιούχου: Ταίριασμα ✓ / Μερικό ταίριασμα ⚠ / Μη ταίριασμα ✕»), sanctions, maker. The checker uses the maker-checker pattern. **A changed payee bank account forces maker-checker regardless of amount** (contract §3.9.2). Released disbursements show the status chain as a compact horizontal stepper (Requested → … → Cleared).
- **Delinquency and dunning (§5.6.4).** A vertical **dunning ladder**: Υπενθύμιση → Ειδοποίηση μη καταβολής (statutory) → Αίτημα ακύρωσης → Ακύρωση. The current stage is highlighted, with a statutory countdown at the notice stage and the next automatic action («Αυτόματη αποστολή ειδοποίησης στις 12/10»). «Αναστολή όχλησης» (hold) requires a reason and is audited.

## A.7 §5.7 Underwriting referral and approval queues (workbench)

**Traces to:** UIL-U1, U3, U4, U5; PLT authority framework (CD-05); WRK queues and SLA (CD-10); IB-04, 05, 08, 19, 20.

**Workbench layout (§5.7.1):**
- **Sidebar work views:** Οι παραπομπές μου 12 · Ομάδα 31 · Λήγουν σήμερα 4 · Με όρους σε εκκρεμότητα 7 · Ολοκληρωμένες.
- **Queue table (IB-04):** columns Παραπομπή, Πελάτης, Προϊόν, Παραγωγός, Ασφάλιστρο, **Προτεραιότητα** (score bar + number; «Γιατί;» explains SLA remaining, premium size, broker tier and renewal), **SLA** countdown pill, Ζητήματα (count by severity), Ανάθεση. The v3 mockup condenses this into a two-line row; see §11.
- **Detail (split pane):** risk brief (AI summary card, optional); the **issues panel**, where each issue shows the rule and the triggering value against the threshold, e.g. «Αξία οχήματος 48.000 € > όριο 40.000 €»; risk detail tabs (Σύνοψη, Κίνδυνος, Ιστορικό ζημιών, Κυρώσεις, Έγγραφα, Προσφορά); and the **decision bar**.

**Decision bar (§5.7.2): a sticky, opaque 64 px footer** in the detail pane.
- **Authority meter:** «Η εξουσιοδότησή σας: Αυτοκίνητο · ασφαλιζόμενη αξία έως 60.000 € ▓▓▓▓▓▓▓░░ 48.000 € ✓». It shows each dimension from `authority.check` (amount, sum insured, deviation %, territory).
  - Within authority: a `success` meter and «Έγκριση» enabled.
  - Beyond authority: the meter overflows in `danger` past a limit tick, and the primary becomes «Παραπομπή σε ανώτερο (Ομάδα Β)», naming the next authority (contract §3.9.3 allow / refer-to / deny with reason).
- **Buttons:** `[Απόρριψη…]` `[Έγκριση με όρους…]` … `[Έγκριση  Ctrl+↵]` (commit CTA).
- «Έγκριση με όρους…» opens a side sheet with condition templates and a validity rule.
- «Απόρριψη…» opens a decline side sheet: structured reasons (peril × asset class), a Greek refusal letter preview (DOC template, Greek binding), and «Αποστολή», which requires confirmation.
- **On approve:** MI-40 (the row leaves to Ολοκληρωμένες and the count bumps). For the **first referral decided each day**, **SM-06** runs; after that, a toast only (C-05).
- **Keyboard:** `A` approve (opens a confirm if conditions exist), `C` conditions, `D` decline, `R` refer, `J/K` next/previous. After a decision, focus moves to the next row automatically (configurable).

**Presence and pinned comments (§5.7.3):** pinned comments (IB-20) attach to the triggering field, and presence (IB-19) shows the broker viewing in the portal: «Ο παραγωγός βλέπει αυτή την παραπομπή».

## A.8 §5.8 Broker / agent portal home

**Traces to:** CHN broker portal, UIL-A1 to A6; IB-11, 12, 17, 26; Part 1 §3.7.3.

**Desktop 1440, comfortable density (§5.8.1):**
1. **Canopy band, 240 px** (ambient imagery + scrim): «Καλημέρα, Κώστα» (`type.display`), the date, and the **hero metric** «Παραγωγή Οκτωβρίου 48.230 € · 112 % του στόχου» with a 64 px trend line. On the right, a **Quick quote** card: an **opaque sheet, never glass, because it is a form**. It has a product segmented control (Αυτοκίνητο / Κατοικία / Επιχείρηση) and the first field (plate or address), so a quote starts in one keystroke.
2. **Work-left row (IB-11):** cards «Προσφορές που λήγουν (5)», «Απαντήσεις παραπομπών (2)» (plum), «Ανανεώσεις 30 ημερών (18)», «Ληξιπρόθεσμες πληρωμές (3)» (danger). Each lists its top 3 items with direct actions (Ανανέωση, Αποστολή υπενθύμισης). Clearing items advances a progress ring «Ολοκληρώσατε 7 από 12».
3. **Book-of-business KPI tiles (IB-17):** Ενεργά ασφαλιστήρια, Ποσοστό ανανέωσης, Προμήθειες μήνα, Δείκτης ζημιών (if permitted). Each has a sparkline.
4. **Recent activity** timeline (§4.32) and a **Commissions** mini statement «Προμήθειες προς καταβολή 3.412,80 € · πληρωμή 15/10».

**Tablet and mobile (§5.8.2):**
- Bottom tab bar: Αρχική · Προσφορά · Πελάτες · Εργασίες · Περισσότερα.
- The canopy shrinks to 160 px.
- Quick quote becomes a FAB «+ Προσφορά» (56 px, `gradient.cta`, bottom-right above the tab bar, `material.float` shadow).
- Work-left cards become a horizontal scroll-snap carousel; KPI tiles go 2 per row.

**Agency admin (§5.8.3):** desktop-first. Users, producer codes, commission statements and account current are standard data tables; on `bp.xs` they are read-only summaries.

## A.9 §5.9 Global search and command palette

**Traces to:** IB-01, IB-02; WRK global search, recents, palette back-end (CD-18); contract §3.9.7 (palette ≤ 300 ms, global search ≤ 1 s).

| Level | Entry | Scope | Result |
|---|---|---|---|
| 1. Command palette | `Ctrl/⌘+K`, or the top-bar field | Actions, destinations, records (top 5 per type), recents | Jump directly (§4.36) |
| 2. Full search page | `Enter` on «Αναζήτηση σε πλήρες κείμενο», or `Ctrl/⌘+Shift+F` | All types, full text including notes and document text (permission-filtered) | Results page: type facets on the left (240 px, with counts), results with snippets and match highlight, filter chips, split-pane preview on the right |
| 3. Entity search screens | Module navigation (Αναζήτηση ασφαλιστηρίων …) | Structured criteria owned by PTY/POL/CLM | Data table with saved views |

**Palette behaviours (§5.9.2):**
- **Context-aware first screen:** on a policy, the top group is «Ενέργειες για ΑΣΦ-…4471» (Αλλαγή, Ακύρωση, Νέα ζημία, Αποστολή εγγράφου, Ανάθεση).
- **Verbs + objects:** «ακύρωση 4471» resolves to the cancellation wizard; «ανάθεση σε μαρ» completes user names.
- **Frecency:** frequency × recency, 14-day half-life (provisional), per user.
- **Preview pane** at ≥ 1240 px: the palette widens to 960 px with a 320 px preview of the highlighted record. The last preview stays until the new one is ready (no flicker).
- **Math:** «412,38*4» shows «1.649,52» inline with copy.
- **AI row (optional):** «Ρωτήστε τον βοηθό» (IB-30), clearly labelled.
- **Speed:** results render progressively per source. Local actions in < 16 ms; records within 300 ms.
- From Part 2 (grep): query prefixes `ασφ:`/`pol:`, `ζημ:`/`clm:`, `πελ:`/`cus:`, `>` for actions, `#` for IDs. Matching is fuzzy, accent-insensitive and Greeklish-tolerant.

## A.10 §5.10 Notifications centre

**Traces to:** WRK notifications and SLA escalations; CMP clocks; PLT incidents; mentions (IB-20).
- **Bell + drawer** (§4.19, §4.39). Drawer tabs: «Για ενέργεια», «Ενημερώσεις», «Αναφορές». Filter by module. (Part 2: a non-modal 400 px drawer.)
- **Item anatomy:** a 40 px icon tile (family colour background + glyph) → title (bold until seen) → 2-line body → time → inline actions («Έγκριση», «Άνοιγμα», «Αναβολή 1 ώρα») → unread dot (MI-60).
- **Grouping:** items of the same type within 15 minutes are grouped («5 νέα έγγραφα σε 3 ζημίες»).
- **Priority:** breached clocks and sanctions hits pin to the top with a solid danger pill, and the bell runs MI-26 once.
- **Delivery:** in-app always; email digest optional; **no desktop OS notifications for P2/P3 content** (titles only).
- **Do-not-disturb:** «Σίγαση έως 16:00» silences non-urgent badges. Urgent items (breached, sanctions) still show.

## A.11 §5.11 Empty, loading, error and permission-denied states (canonical table)

| State | Pattern | Example (GR) |
|---|---|---|
| First-use empty | Illustration (240×180) + headline + one sentence + primary action + help link | «Δεν έχετε ακόμη αποθηκευμένες προβολές» · «Αποθηκεύστε φίλτρα και στήλες για να επιστρέφετε με ένα κλικ.» · [Δημιουργία προβολής] |
| Done-empty (work cleared) | Small illustration + celebratory line. **SM-07 fires here, once per day** | «Η ουρά σας είναι άδεια. Καλή δουλειά.» |
| Filtered-empty | **No illustration.** The active filters are listed + «Εκκαθάριση φίλτρων» | «Κανένα αποτέλεσμα για: Κατάσταση = Ακυρώθηκε, Παραγωγός = 10233» |
| Loading, fast (< 150 ms) | Show nothing (no flash) | — |
| Loading, normal | Skeletons matching the layout (MI-33) | — |
| Loading, long (> 2 s) | Progress with stage text and an estimate if known; the user can leave and be notified | «Δημιουργία αναφοράς… 2 από 4 στάδια» |
| Error, recoverable | Inline banner in the affected region only; the rest of the page works; Retry; correlation id inside a «Λεπτομέρειες» disclosure | «Δεν ήταν δυνατή η φόρτωση των πληρωμών. [Επανάληψη] · Κωδικός: 4f2a…91c0» |
| Error, page-level (500 or render failure) | Full-sheet state with illustration, «Επιστροφή στην αρχική», «Αναφορά προβλήματος» (pre-filled with the trace id). **Unsaved input is preserved** in local draft storage | «Κάτι πήγε στραβά από τη δική μας πλευρά» |
| Not found / merged | Merged parties auto-redirect to the survivor with the banner «Το πρόσωπο συγχωνεύθηκε στο ΠΡΣ-…» (contract §3.2.3) | — |
| Permission denied (whole screen) | Lock illustration + what is restricted + who can grant it + «Αίτημα πρόσβασης» (creates a request to the role owner) | «Δεν έχετε πρόσβαση στις αναφορές Solvency II. Απευθυνθείτε στον υπεύθυνο ρόλων της Οικονομικής Διεύθυνσης.» |
| Permission-limited (partial) | Restricted fields are **omitted or masked, never blank**, with a caption | «•••• 4471 · Απαιτείται δικαίωμα προβολής ευαίσθητων δεδομένων» |
| Offline / degraded | Status bar + top banner (MI-63); read-only cached data; actions disabled with the reason «Εκτός σύνδεσης» | — |
| AI off / kill switch | AI regions are replaced by non-AI layouts. One system banner, the first time per session: «Οι λειτουργίες ΤΝ είναι προσωρινά απενεργοποιημένες. Όλες οι εργασίες συνεχίζονται κανονικά.» | — |

## A.12 §5.12 Unsaved changes

- **Prevention first:** wizards and side sheets autosave drafts to the server (800 ms debounce), with a local **IndexedDB** fallback. **No P3 data in local drafts** (GDPR): P3 fields are excluded and re-requested.
- **Indicator:** MI-64 dot on the tab or title; footer «Μη αποθηκευμένες αλλαγές».
- **Leaving with unsaved changes** (route change, side-sheet close): a `sm` modal titled «Υπάρχουν μη αποθηκευμένες αλλαγές». The body lists the changed fields (up to 5, then «και 3 ακόμη»). Actions: «Συνέχεια επεξεργασίας» (**default focus**) · «Απόρριψη αλλαγών» (danger-ghost) · «Αποθήκευση και έξοδος» (primary). Browser tab close uses the native `beforeunload` prompt.
- **Session expiry:** 2 minutes before expiry, a non-blocking banner «Η σύνδεση λήγει σε 2:00 · Παράταση». On expiry, re-authenticate silently where possible and **retain in-memory form state**. §11.10 says how: call `/.auth/refresh` (Container Apps built-in auth), and on a 401 open a re-auth popup and replay the request with the same idempotency key. This conflicts with the ADR's MSAL React; see §13.

## A.13 §5.13 Destructive-action confirmation (proportional friction)

| Level | Examples | Confirmation |
|---|---|---|
| 0 · Reversible | Remove a filter; delete a draft note within the edit window | None; **undo toast 6 s** (MI-31) |
| 1 · Recoverable | Withdraw a quote, unassign, cancel a draft job | `sm` modal: action + object in the title, one consequence line, **«Άκυρο» focused** |
| 2 · Contractual or financial | Cancel a policy, void a payment, write off, reverse a receipt | `md` modal with a consequences key–value summary (effective date, refund or charge, documents issued, notifications sent), a **required reason select**, and **type-to-confirm** with the last 4 characters of the business id («Πληκτρολογήστε 4471 για επιβεβαίωση»). The danger button enables only on a match |
| 3 · Regulated, maker-checker | Party merge with in-force policies, legal-hold release, retention-policy change, AI kill switch | Level 2 + **sent for approval** (§5.14); the maker cannot complete it alone |

Danger buttons are labelled **verb + object** («Ακύρωση ασφαλιστηρίου»), never «Ναι» or «Διαγραφή». After a level 2 or 3 action, the record shows the new state with MI-15 and the timeline adds the event (MI-48). Part 4 §9.2: destructive modals use `alertdialog`.

## A.14 §5.14 Maker-checker approvals

**Traces to:** contract §3.9.2; PLT `ApprovalRequest` (`REQ-PLT-004`); UIL-C10, UIL-B4, UIL-U9.

**Maker side:**
- The button reads «Αποστολή για έγκριση» with the `user-check` icon.
- The submit sheet has a required **justification** (textarea), optional attachments, the **checker group** (pre-selected by rule, changeable within allowed groups) and a summary of what happens after approval.
- After submit, the object shows `pending-approval` (plum): «Αναμένει έγκριση · Ομάδα Πληρωμών Β · από 14:02». The maker can **withdraw** until a checker opens the item.

**Checker side (approval inbox):**
- A work view «Εγκρίσεις» with a count, sorted by SLA and amount.
- The **approval card** shows: maker (avatar, name, role); timestamp; justification; a **diff** of what changes (approve-the-diff layout **without AI styling**, neutral family); money in large tabular type with **amount in words**; the checker's authority meter; linked evidence (document viewer side sheet); the object's recent history.
- **Decision bar:** «Απόρριψη…» (requires a reason) · «Επιστροφή για διόρθωση…» · «Έγκριση». For payments, «Έγκριση» is the commit CTA. If the checker lacks authority, the primary becomes «Προώθηση σε ανώτερο».
- **The maker can never be the checker.** If the current user is the maker, the decision bar is replaced by the info message «Δεν μπορείτε να εγκρίνετε δική σας ενέργεια», and the item is excluded from their inbox.
- **On approval:** MI-40, the maker is notified, and the audit records both people. Payments → **SM-03** at release.

## A.15 §5.15 Session start and welcome screen

**Traces to:** INFRASTRUCTURE.md §6.1 (Microsoft-hosted Entra sign-in); C-02.
- **Welcome screen**, shown only when the session cookie is absent and before the redirect:
  - full-viewport ambient imagery under the scrim;
  - a centred `material.overlay` card (`radius.2xl`, **440 px**) with the wordmark, the legal entity name, an environment ribbon if non-prod, and **one button «Σύνδεση με λογαριασμό εργασίας»**;
  - on the High tier, the card edge uses real displacement refraction (a C-02 exception).
  - There is **no username or password field**. Entra owns credentials.
- After sign-in: **SM-08** (first load per day), then Home. External brokers and bank staff use the same welcome-then-redirect pattern (CHN, Stage 2).

## A.16 §5.16 Statutory clock pattern (cross-module)

**Traces to:** CMP clock register (CD-07), clock states (contract §3.2.4).
- Every clock instance renders identically: a **countdown pill** (§4.13). The clock name is in the tooltip and accessible name («Προθεσμία απάντησης σε παράπονο: 12 ημέρες, λήγει 19/10/2026»). The business-day basis is noted where used.
- Clicking opens a clock detail popover: start event, pauses, warning threshold, owner.
- Paused clocks show `pause` and «Σε παύση από 03/10».
- The CMP **clock dashboard** is a data table plus a 14-day horizon chart (§6.2).

## A.17 Pattern-level gaps (not in §5)

- **Forms in general.** Field-level validation, the error summary and layout are in Part 2 §4 (not read here). §9 G-18 requires labels, instructions, error identification, suggestions and an **error summary on submit** that receives focus and links to fields. The stack's **React Hook Form + Zod** is not mentioned anywhere in the guide. Zod messages must map to the i18n keys and the §8.4 formula.
- **Tables.** Data table behaviour (sorting, resizing, saved views, bulk bar, two-line rows) is in Part 2 §4.35. Part 4 §11.7 gives the row CSS and code. §9.2 lists the table ARIA requirements.

---

# Part B: Section 6, Data visualisation

**Traces to:** UIL-C2, UIL-U1, UIL-K3, DAT BI dashboards; IB-12, 15, 17, 26, 30; C-07.
**Library (Part 2):** **visx** (low-level, D3-based). This is a **new dependency**, not in the ADR list.

## B.1 Categorical palette (max 6 series), validated by `tools/dataviz_palette.py`

Every mark is ≥ 3:1 against the sheet (WCAG 1.4.11). Minimum pairwise ΔE76 is ≥ 14.9 under normal vision and simulated protanopia, deuteranopia and tritanopia (Machado 2009, severity 1.0). The report shows **FAILS 0**.

| Token | Name | Light | vs sheet | Dark | vs sheet |
|---|---|---|---|---|---|
| `chart.cat.1` | Aegean | `#2E5BDB` | 5.78 | `#6F8FFF` | 5.86 |
| `chart.cat.2` | Amber | `#DB7100` | 3.29 | `#FFAB52` | 9.30 |
| `chart.cat.3` | Pine | `#00735A` | 5.83 | `#16967F` | 4.74 |
| `chart.cat.4` | Rose | `#D0428E` | 4.33 | `#F07CC3` | 6.96 |
| `chart.cat.5` | Navy / Ice | `#13286A` | 13.64 | `#DCE4FF` | 13.78 |
| `chart.cat.6` | Lavender | `#9B7FE8` | 3.18 | `#C4B2FF` | 9.27 |
| `chart.cat.other` | Grey («Λοιπά») | `#6B7485` | 4.71 | `#98A1B0` | 6.70 |

Closest pairs (min ΔE):

| Vision | Light | Dark |
|---|---|---|
| Normal | 26.4 (Aegean–Lavender) | 27.8 (Aegean–Lavender) |
| Protanopia | 20.6 (Aegean–Lavender) | 17.1 (Rose–Lavender) |
| Deuteranopia | 15.7 (Pine–Rose) | 20.0 (Pine–Rose) |
| Tritanopia | 14.9 (Amber–Rose) | 15.9 (Ice–Lavender) |

**Rules:**
1. **Max 6 categorical series.** A 7th or later category goes into «Λοιπά» (grey), or the chart becomes small multiples.
2. **Fixed order.** Colours are assigned 1→6, so a series keeps its colour across charts on a page. Entity-coded charts use a config mapping, e.g. Αυτοκίνητο = cat.1, Κατοικία = cat.3, Επιχείρηση = cat.2.
3. **Direct labels** at line or bar ends when there are ≤ 4 series; otherwise a legend. Legends are interactive (click isolates, `Shift`-click toggles) and keyboard-reachable.
4. **Never colour alone.** Lines differ by dash pattern, in order: solid, `6-3`, `2-2`, `8-3-2-3`, solid + circle markers, solid + square markers. Stacked areas and bars use texture patterns in forced-colours mode and in print.
5. **Status colours are not chart colours.** `success/warning/danger` appear in charts only when they encode their own meaning (e.g. SLA met/breached).
6. **AI violet and the Iris gradient are never chart colours.** cat.6 is deliberately lighter and bluer than `ai.solid`.

## B.2 Sequential palette (single hue, 7 steps): magnitude, heatmaps, choropleths

| Step | Light | Dark |
|---|---|---|
| 1 | `#EEF3FF` | `#121C3D` |
| 2 | `#DCE6FF` | `#1C2E66` |
| 3 | `#BACCFF` | `#1F3586` |
| 4 | `#8FA9FF` | `#2E4FD6` |
| 5 | `#6283FA` | `#6283FA` |
| 6 | `#2E4FD6` | `#8FA9FF` |
| 7 | `#1C2E66` | `#DCE6FF` |

Dark mode reverses lightness, so "more" is always "more contrast".

## B.3 Diverging palette (favourable ↔ adverse, 7 steps): **not red–green**

Blue = favourable, amber/brown = adverse, neutral midpoint.

| | −3 (most adverse) | −2 | −1 | 0 | +1 | +2 | +3 (most favourable) |
|---|---|---|---|---|---|---|---|
| Light | `#7A3E00` | `#C46A10` | `#F2C48A` | `#EEF0F3` | `#A9BDFF` | `#4566E8` | `#1C2E66` |
| Dark | `#F2C48A` | `#C9832E` | `#6A4212` | `#2A303B` | `#2B3F8F` | `#6F8FFF` | `#C9D6FF` |

Uses: loss ratio vs plan, premium change distribution (UIL-U10), variance heatmaps. The legend always names the poles in words: «Δυσμενές ← → Ευνοϊκό».

**Not script-verified (Part 3 self-check):** the sequential and diverging ramps, because they are fills behind printed numbers. **Heatmap cell text must switch per cell between `text.primary` and white by computed contrast** (a runtime helper; the self-check says this is in §11, but no such helper code is in §11; see the gaps in §13).

## B.4 Chart type per use case (§6.2)

| Use case | Chart | Notes |
|---|---|---|
| One headline number + comparison | Hero metric / KPI tile with sparkline (§4.27) | §6.2 says "no ticker on first render". This **conflicts** with MI-69 count-up; see §13 |
| Trend over time | Line (≤ 6 series), or area for a single series (`gradient.horizon`) | Time on x; Greek month labels; the current incomplete period is dashed |
| Compare categories | **Horizontal bar**, sorted by value | Horizontal because Greek labels are long; value labels at the bar end |
| Part-to-whole | 100% stacked bar (one bar) or treemap. **No pie or donut**, except a single 2–3 segment donut inside a KPI tile | Angles are read poorly |
| Plan vs actual | Bullet chart (bar + target tick + neutral qualitative bands) | Period close, production targets |
| Progress to done | Progress bar (`gradient.tide`) + count (IB-12) | Close, migration, renewal season |
| Distribution | Histogram, diverging colours by bucket sign + median marker | UIL-U10 |
| Loss development | Development triangle as a heatmap table (sequential colours, **numbers always printed**) | DAT, FIN |
| SLA / statutory horizon | 14-day horizon timeline, markers coloured by clock state | §5.16 |
| Geographic accumulation | Choropleth by postcode or region (sequential) + proportional symbols; muted greyscale map tiles; **always paired with a ranked table** | RI, UW, CLM cat. The map tile provider is unspecified, which would be another dependency |
| Relationships | Node-link graph (IB-15), layered for RI (bottom → top, edge width = participation); table toggle mandatory (§4.38) | RI, SIU, PTY |
| Funnel | Horizontal funnel bars with conversion % between steps | CHN |
| Cohort / retention | Heatmap table | Renewals |
| Status mix in a queue | Segmented 100% bar in status families **with labels** | The one place status colours encode data |

## B.5 Anatomy and styling (§6.3)

| Element | Spec |
|---|---|
| Plot background | Transparent on `surface.sheet`. Charts **never sit on glass or imagery** |
| Gridlines | Horizontal only, 1 px `border.subtle`, max 5. Vertical lines only at year boundaries on time axes |
| Axes | Baseline 1 px `border.default`. Tick labels `type.caption` in `text.tertiary`. Y labels right-aligned and abbreviated («1,2 εκ.»). **Bar y-axes start at 0.** Lines may use a non-zero baseline with an explicit axis-break marker |
| Lines | 2 px (1.5 px in sparklines), round joins. Points only on hover, or when there are ≤ 12 points |
| Bars | 2 px corner radius at the value end only. Gap = 30% of the band. Minimum thickness 8 px |
| Area | `gradient.horizon.*` for a single series; flat 0.18 alpha fills for stacked |
| Annotations | Vertical 1 px dashed `text.tertiary` line with a label chip at the top |
| Tooltip | `surface.inverse` (solid), `type.caption`, `tabular-nums`. Shows x, then each series with its swatch **and** dash pattern. 12 px pointer offset. Keyboard arrows move the focused point and the tooltip follows |
| Crosshair | 1 px `border.strong` at 40% |
| Empty | «Δεν υπάρχουν δεδομένα για την περίοδο», centred, with axes drawn |
| Loading | Skeleton axes + a flat line, shimmer (MI-33) |
| Error | Inline banner in the chart card |
| Motion | Lines draw (`stroke-dashoffset`, 600 ms `ease.emphasized`). Bars grow from the baseline (scaleY, 400 ms, 16 ms stagger, max 12 bars). Updates morph over 400 ms `ease.standard` only when there are ≤ 200 points, otherwise instant. **RM: static** |

## B.6 KPI and number formatting (§6.4)

| Rule | Spec |
|---|---|
| Full values | el-GR «1.284.390,12 €»; en-GB «€1,284,390.12» (driven by the region-format setting) |
| Abbreviation (tiles, axes) | < 10.000: full («8.420 €»). 10.000–99.999: thousands with 1 decimal («48,2 χιλ. €»). 100.000–999.999: no decimal («482 χιλ. €»). ≥ 1.000.000: «1,28 εκ. €». ≥ 10⁹: «1,28 δισ. €». EN: «48.2K», «1.28M», «1.28B». **The full value is always in the tooltip and accessible name** |
| Percentages | 1 decimal in tiles («68,4 %»); per-column config in tables. Percentage points «+2,1 μ.» / «+2.1 pp» |
| Deltas | Sign always shown, using U+2212 for minus («−1,8 %»); arrow ▲▼ or `trending-up/down`; favourability per KPI definition; comparison basis in words («vs Σεπ», «vs πλάνο») |
| Ratios | Percent, 1 decimal; thresholds from config as a target tick |
| Counts | Integers with grouping («4.812»); never abbreviated below 10.000 |
| Currency mix | Group reporting in USD (Fairfax): «$» prefix in EN, «USD» suffix in EL when mixed with EUR on the same view |
| Durations | «2 ώρ. 15 λ.», «3 ημ.»; SLAs in business days state «εργ. ημ.» |
| As-of | Every KPI states its freshness: «Ενημέρωση 14:32» or «Δεδομένα έως 06/10/2026» |

## B.7 Sparklines (§6.5)

- **Size:** width 100% of the tile (min 80 px). Height 32 px in a tile, 20 px in a table cell, 64 px in the hero.
- **Mark:** 1.5 px line in `chart.cat.1`, or a success/danger **solid** token when it encodes favourability. Area uses `gradient.horizon.positive/adverse/neutral`.
- **Endpoint:** 4 px dot with a 2 px sheet-colour ring. Optional 3 px min/max dots in `text.tertiary`. Optional dashed 1 px reference line.
- **Interaction:** MI-57 scrub with a value tooltip; ←/→ when the tile is focused.
- **Accessibility:** `role="img"` with a summary `aria-label`, e.g. «Τάση 12 μηνών: από 1,02 εκ. σε 1,28 εκ. €, αύξηση 25 %». The KPI's «Προβολή αναφοράς» link gives the full data.
- **Motion:** MI-56 draw on first render; updates morph over 400 ms.

## B.8 Chart accessibility (§6.6)

- Every chart has a text summary and a **«Πίνακας δεδομένων» toggle** that shows the exact data as a table.
- The chart is one tab stop. Arrows move between points and series; `Enter` drills down.
- Forced colours: marks use `CanvasText` with patterns; gridlines use `GrayText`.
- Conversational analytics (IB-30): the generated query and the result table are always shown under the chart, labelled AI-generated.

---

# Part C: Section 7, Iconography and illustration

## C.1 Icon set (§7.1)

| Property | Spec |
|---|---|
| **Library** | **Lucide (`lucide-react`), ISC licence**, tree-shaken per icon. A **new dependency** (permissive) |
| Grid | 24×24 with 2 px padding (20×20 live area) |
| Sizes | 12 (pills, dense tables), 14 (pills in comfortable density, small buttons), 16 (default), 20 (nav rail, comfortable toolbars), 24 (page headers, touch), 32 and 40 (notification tiles, illustration companions) |
| Stroke | `absoluteStrokeWidth` on. **1.5 px at 12–16 px**, **1.75 px at 20 px**, **2 px at ≥ 24 px** |
| Caps / joins | Round / round |
| Colour | `currentColor`. Status icons use `status.<f>.fg` on a status background and `.solid` on the sheet |
| Alignment | A 16 px icon beside 13 px text is centred on the x-height + 1 px (`vertical-align: -0.2em` inline, or flex centring) |
| Accessibility | Decorative icons get `aria-hidden="true"`. Meaningful standalone icons need an accessible name |
| Direction | No mirroring (LTR only) |
| Filled variants | Not used, except 8 px status dots and the **selected nav rail item** (icon on a 32×32 tile with a 20% `brand.solid` fill, `radius.md`) |

**Custom insurance icons (§7.1.1):** 18 icons drawn to Lucide rules and exposed through `<Icon name="policy" />`. Part 4 §11.1 puts them as React components in `/icons`. The art does not exist yet; it has to be drawn.

| Name | Concept | Construction |
|---|---|---|
| `policy` | Ασφαλιστήριο | Document with a folded corner and a shield inset bottom-right |
| `policy-term` | Ασφαλιστική περίοδος | Document + bracket underneath |
| `endorsement` | Πρόσθετη πράξη | Document with a "+" tab on the right edge |
| `quote` | Προσφορά | Document with € |
| `bind` | Σύναψη | Two interlocking rings over a document baseline |
| `claim` | Ζημία | Folder with a lightning crack |
| `fnol` | Αναγγελία | Megaphone-shaped document |
| `exposure` | Έκθεση ζημίας | Shield split in two halves |
| `reserve` | Απόθεμα | Three stacked coins with a lock |
| `recovery` | Ανάκτηση | Coin with a curved return arrow |
| `friendly-settlement` | Φιλικός Διακανονισμός | Two cars facing, with a handshake arc |
| `joint-report` | Δήλωση ατυχήματος | Two-column form with a car pictogram |
| `treaty` | Σύμβαση αντασφάλισης | 3 stacked bars with a shield |
| `cession` | Εκχώρηση | Shield with an arrow leaving to the right |
| `mark` | ΜΑΡΚ (myDATA) | Stamp with a check |
| `clock-statutory` | Νόμιμη προθεσμία | Clock with a § sign |
| `greek-plate` | Πινακίδα | Rounded rectangle with the EU band on the left (single-colour line) |
| `maker-checker` | Τέσσερα μάτια | Two overlapping user silhouettes with a check |

**Usage rules (§7.2):**
- One icon per concept, used everywhere. The Part 1 §2.1.4 status map is binding.
- **Icon-only buttons** are allowed only for close, search, more, copy, filter, settings, download and refresh, and **always with a tooltip**. Everything else has a text label.
- The nav rail shows icons + tooltips when collapsed and icons + labels when expanded.
- **Fixed module icons:** Αρχική `house`, Εργασίες `inbox`, Πελάτες `users`, Ασφαλιστήρια `policy` (custom), Ανάληψη `scale`, Ζημίες `claim` (custom), Χρεώσεις `wallet`, Αντασφάλιση `treaty` (custom), Λογιστική `book-open`, Έγγραφα `files`, Συμμόρφωση `shield-check`, Αναφορές `chart-column`, Προϊόντα `boxes`, Διαχείριση `settings`.

## C.2 Illustration: "Cycladic line" (§7.3)

| Property | Spec |
|---|---|
| Concept | Calm architectural still-lifes from Cycladic forms (cubic houses, arches, stairs, sea horizon, olive branch, paper documents as architecture) with insurance objects (shield, document, car). **No umbrellas or piggy banks** |
| Construction | 2.5D flat planes, at most a single 30° isometric hint; 8 px grid at 240×180 |
| Line | 1.5 px at the 240×180 master. `neutral.700` `#3D4452` light / `neutral.300` `#B9C0CB` dark. Round caps |
| Fills | Max 3 tints. Light: `#DCE6FF`, `#CDF3EE`, `#EEF0F3`. Dark: `#1C2E66`, `#12413E`, `#232831`. **One accent per illustration:** `gradient.tide` (default), `gradient.iris` (AI-related empties only) or `gradient.laurel` (done-empty / SM-07 only) |
| People | None, or faceless abstract figures |
| Text | **Never** (localisation) |
| Sizes | 120×90 (in cards), 240×180 (page empties, permission denied), 320×240 (onboarding, errors), 480×360 (welcome) |
| Format | Inline SVG components, theme-aware through CSS variables, **≤ 8 KB each after SVGO** |
| Motion | Page-level only. A 6 s ease-in-out drift loop (±2 px on 2 layers, opposite directions); the sea line shimmers with a 1 px dash offset; pauses off-screen. **RM: static** |

**Catalogue (§7.3.2), still to be drawn:**

| ID | Scene | Use |
|---|---|---|
| ILL-01 | Empty arch with a document on the step | First-use empty |
| ILL-02 | Stair to a closed door with a small lock | Permission denied |
| ILL-03 | Calm horizon, cleared table, olive branch (Laurel) | Done-empty / SM-07 |
| ILL-04 | Map fold with a pin and a dashed path | No search results / filtered empty. Note: §5.11 says filtered-empty has **no illustration**, which is a minor inconsistency |
| ILL-05 | Cubic house with a cracked plane and a shield | 500 page error |
| ILL-06 | Cloud with a dashed line to a house | Offline |
| ILL-07 | Window with a sparkle (Iris) | AI empty / AI disabled |
| ILL-08 | Stack of paper planes forming steps | Onboarding |
| ILL-09 | Open ledger on a harbour wall | Finance empties |
| ILL-10 | Two boats tied to one bollard | Reinsurance empty |

**Onboarding (§7.3.3):**
- First login per role: a **3-step coach-mark tour** on the real UI: (1) the palette, «Πατήστε Ctrl+K για να βρείτε οτιδήποτε»; (2) work views and counts; (3) the context panel.
- Each coach mark is a `material.popover` with a 2 px brand spotlight ring. The rest of the screen dims to only `rgba(14,17,22,0.24)`.
- The tour is skippable, never shown again once dismissed, and replayable from Help.
- What's new: a «Νέα» chip on the help icon. The panel has 1-line descriptions and «Δείξε μου».

---

# Part D: Section 8, Content and localisation

**Traces to:** contract §3.3 (bilingual glossary, Greek terms binding), §3.9.5 (four independent settings: language, region format, legal jurisdiction, currency), §3.9.10 (Greek is the language of record), `REQ-MKT-005`.

## D.1 Voice and tone (§8.1)

**Voice (constant):** precise, calm, respectful, brief, accountable. "A senior colleague who knows the rules and respects the user's time."

| Trait | Greek | English | Not |
|---|---|---|---|
| Precise | «Λείπει η ημερομηνία ζημίας.» | "Loss date is missing." | «Κάτι δεν πήγε καλά με τα στοιχεία.» |
| Calm | «Η πληρωμή δεν στάλθηκε. Τα στοιχεία σας αποθηκεύτηκαν.» | — | «Σφάλμα!!! Η ενέργεια απέτυχε.» |
| Respectful | **Formal plural for every audience**: «Επιλέξτε», «Συμπληρώστε» | "Choose", "Enter" | Singular «Επίλεξε», or impersonal «Επιλογή απαιτείται» |
| Brief | One sentence per message where possible | — | Paragraphs in toasts |
| Accountable | Names who acted: «Εγκρίθηκε από Μ. Παπαδοπούλου» | — | Passive voice that hides the decider |

**Tone by situation:**
- Routine success: quiet («Αποθηκεύτηκε»).
- Milestone: warm, one notch of celebration («Το ασφαλιστήριο εκδόθηκε. Καλή συνέχεια!»).
- User-fixable error: solution-first («Ο ΑΦΜ πρέπει να έχει 9 ψηφία. Ελέγξτε τον αριθμό.»).
- System failure: apologise once, then be practical.
- Destructive confirmation: consequence-first («Το ασφαλιστήριο θα ακυρωθεί από 15/10/2026. Θα εκδοθεί επιστροφή 128,40 €.»).
- Legal: formal; cite the obligation **by name, never by article number alone**.
- AI disclosure: «Πρόταση από την τεχνητή νοημοσύνη. Ελέγξτε πριν την αποδοχή.»
- Customer portal: warmer, no internal terms («εργασία συμβολαίου» never appears).

**Word rules (Greek):**
- Use glossary terms exactly. Terms marked † are used as written until ROLE-46 confirms them.
- No needless anglicisms: «αναγγελία ζημίας», not «FNOL»; «παραγωγός», not «producer». Market acronyms are fine with a first-use expansion in help text: ΑΦΜ, ΔΟΥ, ΓΕΜΗ, ΜΑΡΚ, ΦΑΣ.
- **Sentence case.** No exclamation marks in staff UI except in signature-moment headlines (one allowed).
- Gender-inclusive: use role nouns and rephrase to avoid articles («Ανατέθηκε σε Μ. Παπαδοπούλου»). Where agreement is unavoidable, use ICU `select` on the person's recorded grammatical gender (a PTY field), falling back to neutral phrasing.

**Vocative greetings:** «Καλημέρα, Κώστα», not «Κώστας».
- Use the PTY/PLT **preferred greeting name** field.
- If it is empty, a rule-based helper guesses: ‑ος → ‑ε for most 3+ syllable names; ‑ος → ‑ο for common 2-syllable names (Γιώργος → Γιώργο); ‑ας → ‑α; ‑ης → ‑η; feminine names unchanged.
- **The guess is shown only after the user has confirmed it once in their profile.** Fallback: «Καλημέρα» with no name.

## D.2 Locale formats (§8.2), el-GR primary

| Item | el-GR | en-GB | Implementation |
|---|---|---|---|
| Date short | 07/10/2026 | 07/10/2026 | `Intl.DateTimeFormat(locale,{day:'2-digit',month:'2-digit',year:'numeric'})` |
| Date long | Τρίτη, 7 Οκτωβρίου 2026 (**genitive** month) | Tuesday, 7 October 2026 | `{weekday:'long',day:'numeric',month:'long',year:'numeric'}` |
| Month header | Οκτώβριος 2026 (**nominative**) | October 2026 | **Standalone** month (`LLLL`) via `@internationalized/date` / `formatToParts` |
| Short month | Οκτ | Oct | |
| Time | 14:32 | 14:32 | 24-hour for staff in both |
| Date-time | 07/10/2026 14:32 | same | |
| Relative | πριν 5 λεπτά · σε 3 ημέρες · χθες | 5 minutes ago | `Intl.RelativeTimeFormat` |
| Number | 1.234.567,89 | 1,234,567.89 | `Intl.NumberFormat` |
| Currency | 1.234,56 € (suffix, **U+00A0** NBSP) | €1,234.56 | `{style:'currency',currency:'EUR'}` |
| Negative currency | −1.234,56 € (**U+2212**) | −€1,234.56 | Replace hyphen-minus in `formatToParts` |
| Percent | 15 % (**U+202F** before %) | 15% | Check that `formatToParts` gives the narrow NBSP in el |
| Per mille | 2,5 ‰ | 2.5‰ | Custom formatter |
| Phone | +30 210 123 4567 · 69x xxx xxxx | same | **libphonenumber**, a new dependency |
| Address | «Βασ. Σοφίας 12, 106 74 Αθήνα» | ELOT 743 Latin form when the language is EN and a Latin form exists (PTY) | |
| Person name (display) | Γεώργιος Παπαδόπουλος | Latin only if an "as on ID document" form exists; **never auto-transliterated for legal documents** | |
| Person name (lists) | **Παπαδόπουλος Γεώργιος** (surname first), `Intl.Collator('el')` | Papadopoulos, George | |
| Postcode | 106 74 | same | |
| ΑΦΜ | 123 456 789 displayed, 123456789 stored | same | |
| IBAN | GR16 0110 1250 0000 0001 2300 695 | same | |
| Business days | «εργ. ημ.» | business days | PLT calendar |
| Time zone | Europe/Athens; EET/EEST suffix only where cross-zone confusion matters | same | |

**The four settings are independent.** An English-language user can have el-GR formats. **Never derive formats from the language.** For react-i18next this means number and date formatting must take the *region-format* setting, not `i18n.language`.

## D.3 Greek typography in code (Part 4 §11.8, with Part 1 §2.3.4 cross-ref)

- **Uppercase without tonos.** Do not rely on CSS `text-transform: uppercase`, because engines differ. Use `toGreekUpper()`:
  1. NFD-normalise.
  2. If an accented vowel (acute U+0301 or perispomeni U+0342) is followed by ι or υ, add a dialytika (U+0308) to that ι/υ.
  3. Strip U+0300, U+0301, U+0313, U+0314 and U+0342. Ypogegrammeni U+0345 becomes ι.
  4. `toLocaleUpperCase('el-GR')`, then NFC.
- **Mandatory Vitest cases:** Μάιος→ΜΑΪΟΣ, ρολόι→ΡΟΛΟΪ, πρωτεΐνη→ΠΡΩΤΕΪΝΗ, αϋπνία→ΑΫΠΝΙΑ, ευρώ→ΕΥΡΩ, Ασφαλιστήριο→ΑΣΦΑΛΙΣΤΗΡΙΟ, ᾠδή→ΩΙΔΗ, άυλος→ΑΫΛΟΣ. Negative cases: παίζω→ΠΑΙΖΩ, αύριο→ΑΥΡΙΟ.
- **Final sigma** (Part 1 §2.3.4 rule 5): uppercase Σ for both σ and ς. When lowercasing, use ς at word end via `toLocaleLowerCase('el-GR')`. For **search normalisation**, the v3 palette folds `ς→σ` after NFD accent-stripping and lowercasing. That is the right approach for matching.
- **Collation:** `Intl.Collator('el', { sensitivity: 'base', numeric: true })` (Part 2 §4.35), so «ά» sorts with «α» and «ΑΣΦ-2» comes before «ΑΣΦ-10».
- **Typeahead and search:** accent-insensitive (§9.2 Select). Greeklish tolerance via ELOT 743 and its inverse (Part 2 §4.4: «papad» matches «Παπαδόπουλος»).
- **`formatMoney()`:** `Intl.NumberFormat(locale,{style:'currency',currency,minimumFractionDigits:2}).formatToParts()`, mapping `minusSign` to `−`. In el-GR, `Intl` already puts a NBSP before €. **Not covered by §11.8:** per mille, abbreviations (χιλ./εκ./δισ.), percentage points and amount-in-words. These need custom helpers. Amount in words in Greek (for approvals and payments, §5.5.3, §5.14) **has no specified implementation**. This is a gap.
- **Polytonic support** (Part 1 A-11): names and legal citations may contain polytonic characters. The font stack is Inter + Noto Sans fallback, and CI uses fontTools to check coverage of U+0370–03FF and U+1F00–1FFF.

## D.4 Text expansion (§8.3): design Greek-first

- Greek is the source language for every mock-up. Expect +15–35% length, up to +60% for short labels («Bind» → «Δέσμευση»; «Save» → «Αποθήκευση»).
- **Buttons:** width is content-driven; labels never truncate; toolbar overflow moves to «⋯».
- **Labels:** wrap to 2 lines and never truncate.
- **Table headers: single line, never wrapping.** Size columns to fit the Greek label and move units into the cells. Only as a last resort, use an abbreviation from the controlled list, with the full text in a tooltip and accessible name.
- **Tabs:** overflow into «Περισσότερα». **Nav labels:** 2 lines allowed.
- **Legal text is never truncated**; it scrolls in a container with a visible affordance.
- **Pseudo-locale `el-XA` in CI:** Greek padded by 40%, wrapped in «⟦…⟧», with accented capitals. Playwright compares screenshots with the normal locale.

## D.5 Error message formula (§8.4)

**[What happened] + [Where / which value] + [How to fix it]**, optionally plus [what is preserved]. Never blame the user, and never show raw codes; the correlation id goes into «Λεπτομέρειες».

Examples by type: missing («Συμπληρώστε την ημερομηνία ζημίας.»), format, check digit, range, cross-field, authority («…Θα σταλεί για έγκριση στην Ομάδα Πληρωμών Β.»), conflict, permission, system, external service («…Μπορείτε να συνεχίσετε και ο έλεγχος θα γίνει αργότερα.»).

**API error mapping (contract §3.5.4, problem+json):**
- each `type` URI maps to a message key;
- `errors[].field` maps to the field label (the "where");
- `detail` is **never shown verbatim**;
- `traceId` goes into the disclosure.

## D.6 Microcopy library (§8.5): seed keys

| Key | EL | EN |
|---|---|---|
| `common.save` / `saveDraft` / `saved` | Αποθήκευση / Αποθήκευση πρόχειρου / Αποθηκεύτηκε | Save / Save draft / Saved |
| `common.cancel` / `close` | Άκυρο / Κλείσιμο | Cancel / Close |
| `common.back` / `next` | Πίσω / Επόμενο | Back / Next |
| `common.undo` / `retry` | Αναίρεση / Επανάληψη | Undo / Try again |
| `common.details` / `why` / `sources` / `more` | Λεπτομέρειες / Γιατί; / Πηγές / Περισσότερα | Details / Why? / Sources / More |
| `common.loading` | Φόρτωση… | Loading… |
| `common.empty` | — (SR text «κενό») | — ("empty") |
| `approval.send` / `pending` | Αποστολή για έγκριση / Αναμένει έγκριση | Send for approval / Pending approval |
| `approval.approve` / `reject` / `returnForChanges` | Έγκριση / Απόρριψη / Επιστροφή για διόρθωση | Approve / Reject / Return for changes |
| `ai.suggestion` / `generated` | Πρόταση ΤΝ / Δημιουργήθηκε με ΤΝ | AI suggestion / AI-generated |
| `ai.accept` / `edit` / `reject` | Αποδοχή / Επεξεργασία / Απόρριψη | Accept / Edit / Reject |
| `ai.unavailable` | Η βοήθεια ΤΝ δεν είναι διαθέσιμη· συνεχίστε χειροκίνητα | AI help isn't available; continue manually |
| `ai.disclosureCustomer` | Μέρος αυτού του κειμένου συντάχθηκε με τη βοήθεια τεχνητής νοημοσύνης και ελέγχθηκε από υπάλληλό μας. | Part of this text was drafted with the help of AI and reviewed by a member of our staff. |
| `unsaved.title` | Υπάρχουν μη αποθηκευμένες αλλαγές | You have unsaved changes |
| `offline.banner` | Εκτός σύνδεσης. Οι αλλαγές θα αποθηκευτούν όταν επανέλθει η σύνδεση. | You're offline. Changes will save when you're back online. |
| `search.placeholder` | Αναζήτηση ή εντολή… | Search or run a command… |

(The `ai.unavailable` string uses the Greek ano teleia «·», which is correct.)

## D.7 Translation mechanics (§8.6)

- **ICU MessageFormat via FormatJS `react-intl`**. This **conflicts with the stack's react-i18next**; see §13.
- Key format: `<module>.<screen>.<element>.<purpose>` (e.g. `clm.fnol.lossDate.label`, `uw.workbench.decision.approve`).
- **Plurals:** Greek and English both use `one` / `other`: «{count, plural, one {# ζημία} other {# ζημίες}}».
- **Never concatenate** or insert translated fragments; whole sentences with placeholders only.
- Every string has a translator **context note** (where it appears, max length, tone).
- Greek is the source of truth for customer documents. UI strings are authored in both languages by the feature team, and ROLE-46 reviews regulated wording.
- **Missing translation at runtime:** show the **Greek string** (never the key) and log `i18n.missing` to telemetry. In react-i18next: `fallbackLng: 'el'`, plus a `missingKeyHandler` that sends to telemetry. Also set `saveMissing`, or a custom handler, so keys are never rendered.

---

# Part E: Section 9, Accessibility checklist

**Standard:** WCAG 2.2 AA (contract §3.9.8), in Greek and English. Check types: A = automated, K = scripted keyboard, SR = screen reader, V = visual.

## E.1 Global checks (every screen)

| # | Check | How | WCAG |
|---|---|---|---|
| G-01 | `lang="el"`/`"en"` on `<html>`; mixed-language spans marked | A (axe + custom span rule) | 3.1.1, 3.1.2 |
| G-02 | Landmarks per Part 1 §3.4.8; one `<main>`; **skip link is the first focusable** | A+K | 1.3.1, 2.4.1 |
| G-03 | `<title>` = «{record or screen} · {module} · {product}»; updated on route change; route change announced politely | A+SR | 2.4.2 |
| G-04 | 2 px focus ring ≥ 3:1, **not obscured** by sticky headers, bulk bar or toasts (`scroll-padding-top` = top bar + sticky header; `scroll-padding-bottom` = 72 px when the bulk bar shows) | K+V | 2.4.7, 2.4.11 |
| G-05 | Everything works by keyboard; no traps except modals (which release on Esc) | K (Playwright per pattern) | 2.1.1, 2.1.2 |
| G-06 | Single-key shortcuts can be turned off and are inactive in text inputs | K | 2.1.4 |
| G-07 | Text ≥ 4.5:1 and UI ≥ 3:1 in both themes (scripts + axe on rendered pages) | A | 1.4.3, 1.4.11 |
| G-08 | No colour-only information (pills have icon + text; charts use patterns). Custom lint: StatusPill requires a label | V+A | 1.4.1 |
| G-09 | 200% zoom at 1280 and reflow at 320 (tables exempt but offer a card view) | V (viewports 640 and 320) | 1.4.4, 1.4.10 |
| G-10 | The text-spacing override does not clip (Playwright injects the CSS) | A | 1.4.12 |
| G-11 | Hover and focus content is dismissible (Esc), hoverable and persistent | K | 1.4.13 |
| G-12 | Targets ≥ 24×24 or equivalent spacing, including compact density | A (custom bounding-box rule) | 2.5.8 |
| G-13 | Drag-and-drop has a non-drag alternative | K | 2.5.7 |
| G-14 | Reduced motion is followed by every MI and SM (Playwright `reducedMotion:'reduce'` snapshots show no transforms) | A | 2.3.3 (AAA, adopted), 2.2.2 |
| G-15 | Nothing flashes more than 3 times per second | V | 2.3.1 |
| G-16 | Toasts pause on hover or focus; session expiry warns ≥ 2 min ahead with extend | K | 2.2.1 |
| G-17 | Status messages announced through live regions without moving focus | SR | 4.1.3 |
| G-18 | Labels, instructions, error identification, suggestions, **error summary on submit** | A+SR | 3.3.1–3.3.3 |
| G-19 | Redundant entry avoided (prefill and reuse of party and address) | V | 3.3.7 |
| G-20 | Accessible authentication: Entra is Microsoft's; no cognitive tests added | V | 3.3.8 |
| G-21 | **Consistent help: «?» in the same place in the top bar on every screen** | V | 3.2.6 |
| G-22 | Forced colours: controls, focus and status icons visible (Playwright `forcedColors:'active'`) | V | 1.4.11 |
| G-23 | Reduced transparency: every glass layer renders solid (app preference on) | A | — |
| G-24 | SR smoke test per release: NVDA+Chrome, JAWS+Edge, VoiceOver+Safari iPadOS, TalkBack+Chrome Android | SR | 4.1.2 |

## E.2 Per-component checks (§9.2), condensed

- **Button:** native `<button>`. Icon-only buttons have `aria-label`. Disabled-with-reason uses `aria-disabled` + `aria-describedby`. Loading uses `aria-busy` and keeps the name. Toggles use `aria-pressed`. Focus ring visible on gradient buttons (sheet halo).
- **Text input:**
  - `<label for>`;
  - `aria-required` + a visible marker;
  - errors: `aria-invalid` + `aria-describedby` with the **error id first**;
  - helper text linked;
  - `autocomplete` tokens on personal fields (`given-name`, `family-name`, `email`, `tel`, `postal-code`, `street-address`; WCAG 1.3.5);
  - masked P2 values have an accessible name that says «κρυφό».
- **Select:** React Aria Select (`aria-haspopup="listbox"`, `aria-expanded`, `aria-selected`). Typeahead works with **accented and unaccented** input.
- **Combobox:** `role=combobox`, `aria-expanded`, `aria-controls`, `aria-activedescendant`, `aria-autocomplete="list"`; result count announced; Esc behaviours; chips removable by keyboard.
- **Date picker:** segments are spinbuttons labelled «ημέρα / μήνας / έτος». The calendar `grid` has full date labels **including holiday names**. Unavailable dates are `aria-disabled` with the reason. **Weeks start on Monday in el.**
- **Currency / percent:** `NumberField` with locale; the value is announced with its unit («ευρώ», «τοις εκατό»). Amount-in-words is `aria-live="polite"` **only on approval screens**.
- **Checkbox / radio / switch:** `fieldset`/`legend` groups; `role=switch`; `aria-checked="mixed"` for indeterminate.
- **Tabs:** arrow keys; panel `tabindex=0` when it has no focusable content; **the error count is part of the tab name**.
- **Segmented control:** `radiogroup` semantics.
- **Status pill:** text always present (or `aria-label` for the dot variant); changes announced.
- **Tooltip:** `role=tooltip`; shows on focus; Esc dismisses; never interactive.
- **Popover / hover card:** `aria-expanded`; `role=dialog` with a label; focus moves in and returns. Hover cards also open with Space.
- **Menu:** arrows, typeahead, Esc, →/← for submenus.
- **Modal:** labelled; focus trapped and restored; Esc closes when not busy; `alertdialog` for destructive actions.
- **Drawer / side sheet:** modal variants behave as modals. Non-modal ones are labelled landmarks reachable by **F6**.
- **Toast:** labelled region; polite or assertive by type; **F8 focuses toasts**; timers pause on focus; undo is also available elsewhere.
- **Banner:** `status` or `alert`. The error summary receives focus, and its links move focus to the fields.
- **Stepper:** `nav` with a list; `aria-current="step"`; step names include their state.
- **Progress:** `progressbar` with `valuenow`/`min`/`max`/`valuetext`; skeletons set `aria-busy`.
- **Avatar / presence:** alt names; the stack button names the count; the editing lock is announced.
- **Card:** stretched-link pattern without nested interactive elements.
- **KPI tile:** value, unit, delta and period in the accessible name. The ticker or count-up must **not** trigger repeated announcements: announce the final value once, politely, at most once per 10 s per tile.
- **File upload:** the drop zone is a button; per-file progress announced; keyboard-only path.
- **Document viewer:** text layer present; keyboard page navigation.
- **Timeline:** `feed` with articles; diffs announced as «από … σε …».
- **Comments:** pins are named buttons; mentions are links.
- **Data table:** `table` or `grid`; `aria-rowcount`/`aria-rowindex` with virtualisation; `aria-sort`; `aria-selected`; row checkbox labels include the business id («Επιλογή ΠΑΡ-2026-0412»); keyboard column resize; totals labelled; empty cells announced as «κενό».
- **Command palette:** a modal dialog with a combobox and grouped listbox; group labels announced; result count polite.
- **AI surfaces:** the AI label is in the accessible name («Πρόταση ΤΝ: …»); confidence is announced; **streaming text is announced only on completion**; the kill-switch fallback is announced once.
- **Charts:** text summary, data table toggle, keyboard point navigation, patterns.

## E.3 Toolchain (§9.3)

- `@axe-core/playwright` on every route × theme × density × locale; zero serious or critical issues.
- Contrast scripts in CI on token changes.
- A Playwright keyboard-only suite per pattern (wizard, workbench, palette, table).
- Manual SR passes on the four pairs, with a 20-task script.
- At least 2 assistive-technology users in testing per major release.

## E.4 Verified evidence

- `contrast-report.txt`: 224 PASS lines, **TOTAL FAILS: 0**. It covers text on surfaces in both themes, glass worst cases over 9 backdrops, gradient stops, the **gradient-border primary button** (label ≥ 15.7 on every light fill; border stops 3.56–4.94 light and 5.11–6.88 dark), avatars, ribbons and tooltip.
- `dataviz-report.txt`: **FAILS 0**.

---

# Part F: Section 10, Signature moments SM-01 to SM-08

## F.1 Signature contract (applies to all eight)

| Rule | Spec |
|---|---|
| Frequency cap | Once per business object per user (SM-01 to SM-06). SM-07 and SM-08 once per day. **Never on bulk or batch actions** (one summary toast instead) |
| Non-blocking | Decorative layers are `pointer-events: none` at `z.signature` = **750**. Focus moves to the next logical action at **t = 0**. Any key, click or scroll fast-forwards the decoration to its end state within one frame |
| Truth first | The state change is committed in the UI at t = 0 (pill, data, live region). The animation decorates a fact; it never reveals it |
| Budget | ≤ 1,600 ms (`motion.duration.signature.max`). ≤ 24 animated nodes on High tier, ≤ 12 on Standard. Only transform, opacity, clip-path and stroke-dashoffset are animated |
| Tiers | High: full. Standard: ≤ 12 nodes, no extra particles or ribbons. Low: the RM version plus a colour change |
| User setting | «Εορτασμοί» off → RM version |
| RM | Static end state + a 120 ms opacity fade; live-region text unchanged |
| Sound | None by default. Optional setting «Ήχοι»: a 120 ms, −24 LUFS chime for SM-02 and SM-04 only |
| Haptics | `navigator.vibrate` 12 ms at the impact frame (Android only) |
| SR | One polite announcement at t = 0 |

t = 0 is **server confirmation**. Before that, the button shows a normal loading state.

## F.2 Moments

**SM-01 · Quote bound, "The knot" (Συνάφθηκε).** Trigger: Job Quoted → Bound (POL), in the §5.1 wizard.

| t (ms) | What happens |
|---|---|
| 0 | Pill morphs «Προσφορά» → «Συνάφθηκε» (MI-15, 150 ms). Live region: «Το ασφαλιστήριο ΑΣΦ-2026-004471 συνάφθηκε». Focus moves to «Έκδοση» |
| 0–240 | The commit button contracts into a 40 px circle (`clip-path: inset(0 calc(50% - 20px) round 20px)`); its label fades |
| 160–400 | A check draws (dashoffset 24→0). The circle fill cross-fades `gradient.cta` → `gradient.laurel` (**stale**: v3 buttons have a surface fill, so this needs reinterpretation) |
| 300–1,200 | **Ribbon:** a 2 px Laurel stroke traces the record-sheet perimeter (an SVG rounded rect matching `radius.lg`) from the button round to the status pill |
| 1,000–1,300 | Ring ripple at the pill (scale 1→1.8, opacity .35→0) |
| 1,200–1,600 | The ribbon fades; the circle morphs into «Έκδοση ασφαλιστηρίου» |
| — | Stepper step 8 draws its check; the connector fills (MI-38) |

RM: the pill swaps, the button shows a check for 600 ms, then the next action. No ribbon.

**SM-02 · Policy issued with MARK, "The stamp" (Εκδόθηκε).** Trigger: issuance complete **and** the fiscal document is Registered (MARK) by CMP. If MARK arrives late, the moment fires on the record, or as a notification if the user has left.

| t (ms) | What happens |
|---|---|
| 0 | Documents card thumbnail; status «Εκδόθηκε». Live: «Το ασφαλιστήριο εκδόθηκε. ΜΑΡΚ 400001234567890.» |
| 0–80 | A 72 px Laurel seal (white custom `mark` icon, 2 px white inner ring) appears at scale 1.25 (`ease.anticipate`) |
| 80–380 | Stamp: the seal drops to 0.92 and settles at 1 (`spring.expressive`, about 12% overshoot). At the ≈180 ms impact, the thumbnail scales 1→0.985→1 |
| 180–700 | Two 1.5 px Laurel ink rings expand 1→2.4, 120 ms apart |
| 300–900 | The MARK number slides up 6 px per character, staggered 18 ms. **Characters are final, never scrambled** |
| 900–1,500 | The seal shrinks (FLIP, `spring.smooth`) into a 20 px «ΜΑΡΚ ✓» badge docked at the thumbnail corner |

RM: the docked badge and number appear with a 120 ms fade.

**SM-03 · Claim payment released / settled, "Flow".** Trigger: Disbursement Approved → Released. The *settled* variant fires when the final payment closes all exposures.

| t (ms) | What happens |
|---|---|
| 0 | Row status «Αποδεσμεύτηκε» (**teal**). Financials headline cross-fades (no ticker). Live: «Πληρωμή 3.200,00 € προς Κ. Νικολάου αποδεσμεύτηκε.» |
| 0–150 | The amount chip lifts (scale 1.04, `elevation.3`) |
| 150–750 | The chip follows a quadratic Bézier (`offset-path` / Motion path) to the claim-strip stage «Πληρωμή», shrinking to 60%, with a Tide trail of 3 ghosts (opacity .2/.12/.06, 40 ms behind) |
| 750–1,000 | The stage node fills `status.brand.bg` → `status.success.solid` with a check; the connector fills with Tide |
| 1,000–1,300 | *Settled* only: exposure pills morph to «Κλειστή» (40 ms stagger) and «Κλείσιμο» lights in Laurel |

RM: statuses update; no flight.

**SM-04 · Period closed, "Horizon".** Trigger: FIN close completes. It fires for **everyone viewing** the close workspace in real time, and as a notification for checklist owners.

| t (ms) | What happens |
|---|---|
| 0 | Chip «Οκτώβριος 2026 · Κλειστή» + `lock`. Live: «Η περίοδος Οκτωβρίου 2026 έκλεισε για το βιβλίο IFRS 17.» |
| 0–400 | The 64 px progress ring completes and turns Laurel; a check draws |
| 400–1,000 | The ring flattens into a 2 px Laurel line that sweeps to both canopy edges |
| 600–1,400 | The ambient layer cross-fades to "dusk" (light `#FEEDC7` / `#DCE6FF`; dark `#381B05` / `#1C2E66`) at 60%, then returns over 600 ms |
| 800–1,400 | Owner avatars wave (scale 1→1.12→1, 50 ms stagger) |
| 1,400–1,600 | The checklist collapses to the summary card «Ολοκληρώθηκαν 46 εργασίες · 3 ημέρες 4 ώρες» |

RM: the chip and summary appear; the ring shows 100%.

**SM-05 · FNOL submitted, "The claim number".** The number is in the DOM and copyable at t = 0. Live: «Η ζημία καταχωρίστηκε με αριθμό ΖΗΜ-2026-031118.»

| t (ms) | What happens |
|---|---|
| 0–320 | Section cards fold (scaleY→0.6, opacity→0, 24 ms stagger, bottom to top) |
| 200–520 | The summary card becomes the 560 px claim card (FLIP, `radius.xl`) |
| 400–900 | The number in `type.display` mono slides up per character (22 ms stagger); «Αντιγραφή» appears (MI-10) |
| 700–1,200 | Clock pill «Προθεσμία προσφοράς: 3 μήνες · έως 07/01/2027»; the timer hand rotates once |
| 900–1,300 | The activities list fades in. «Άνοιγμα ζημίας» has had focus since t = 0 |

Mobile variant: a full-screen sheet with «Αποστολή με SMS/email». RM: the final card with a 120 ms fade.

**SM-06 · Referral decided with authority, "The signature".** Trigger: the UW manager approves (or approves with conditions) the **first referral of the day**. After that, MI-40 only.

| t (ms) | What happens |
|---|---|
| 0 | Pills → «Εγκρίθηκε». Live: «Η παραπομπή ΠΑΡ-2026-0412 εγκρίθηκε από εσάς.» Focus moves to the next row |
| 0–300 | The authority meter fills to the decision marker and pulses (marker scale 1→1.3→1) |
| 200–800 | A hand-drawn SVG underline (1 of 4 pre-authored variants) draws under the name in the receipt «Απόφαση: Μ. Παπαδοπούλου · 14:32», 1.5 px `status.brand.solid` |
| 600–900 | The receipt settles (elevation 2→1); the row leaves (MI-40) |

RM: the receipt appears with the underline already drawn.

**SM-07 · Queue cleared, "Calm water".** Fires when the user completes the last item in **their own** queue, at most once per day.

| t (ms) | What happens |
|---|---|
| 0 | The last row leaves. Live: «Ολοκληρώσατε όλες τις εργασίες της ουράς σας.» |
| 200–900 | ILL-03: 4 layers rise 16 px with parallax (80 ms stagger, `spring.gentle`) |
| 600–1,200 | The horizon line draws; the olive branch fades in |
| 900–1,400 | «Η ουρά σας είναι άδεια» + «Ολοκληρώσατε 38 εργασίες σήμερα» |
| 1,400+ | The illustration starts its 6 s idle drift (paused off-screen) |

**SM-08 · Session start, "Arrival".** First load each day after sign-in. **Input is live at t = 0** (`Ctrl+K` works throughout).

| t (ms) | What happens |
|---|---|
| 0 | Scrim at 0.95; the shell is already in its final geometry |
| 0–400 | Scrim → 0.80/0.82 |
| 80–440 | Rail slides in from −12 px, top bar from −8 px, status bar from +8 px (40 ms stagger); specular highlights fade in 120 ms after each lands |
| 200–600 | Greeting fades up 8 px; date line 60 ms later |
| 400–1,000 | Hero sparkline draws (MI-56); comparison chip slides in |
| 500–900 | Work cards rise 12 px (40 ms stagger, max 6, `spring.smooth`) |
| 900–1,200 | Sidebar counts appear "(no ticker on first render; plain fade)". This **conflicts with MI-69**; see §13 |
| High tier | The welcome card's refraction dissolves as the card FLIPs into the top-bar wordmark (400 ms) |

RM: a single 200 ms fade of the main region.

---

# Part G: Section 11, Developer handoff

## G.1 Repository layout (§11.1)

```
/web
  /src
    /design-system
      /tokens        aegean.css (generated), aegean.tokens.json (generated), index.ts (typed token names)
      /foundations   glass.css, focus.css, motion.css, typography.css, reset.css
      /components    Button/, TextField/, Select/, Combobox/, DatePicker/, MoneyField/, …, DataTable/, CommandPalette/
      /patterns      Wizard/, Workbench/, RecordView/, ApprovalInbox/, EmptyState/
      /motion        springs.ts, signature/ (SM-01…SM-08), useReducedMotion.ts, GlassBudget.tsx, deviceTier.ts
      /i18n          el.json, en.json, format.ts (money, dates, toGreekUpper)
      /icons         custom insurance icons (React components)
  /tools             copied from design-guide/tools; run in CI
```

**Token pipeline:** `tools/build_tokens.py` is the **single source of truth**. The generated files are committed, and CI checks them with `python tools/build_tokens.py && git diff --exit-code`. This needs a **Python runtime in CI**. The full regeneration chain is `build_tokens.py && contrast.py && dataviz_palette.py && build_single_file.py` (design-guide README). The front-end folder name `/web` is a proposal; `core-insurance-app` currently contains only `orchestration/`.

## G.2 Naming conventions (§11.2)

| Thing | Convention | Example |
|---|---|---|
| Token path (DTCG) | `category.role.variant.state`, lowercase, dots | `color.interactive.primary.bg-hover` |
| CSS custom property | `--` + path with `.`→`-` | `--color-interactive-primary-bg-hover` |
| Theme | `data-theme="light|dark"` on `<html>`; absent = follow the OS | |
| Density | `data-density="compact|comfortable"` on `<html>` or a table container. Touch is automatic via `@media (pointer: coarse)` | |
| Material | `data-material="chrome|overlay|popover|float|solid"` | |
| Component | PascalCase folder: `X.tsx`, `X.module.css`, `X.test.tsx`, `X.stories.tsx` | `StatusPill/StatusPill.tsx` |
| State styling | React Aria data attributes (`data-hovered`, `data-pressed`, `data-focus-visible`, `data-disabled`, `data-selected`, `data-invalid`) + ours (`data-loading`, `data-readonly`, `data-ai`) | `.row[data-selected]` |
| CSS | **CSS Modules, camelCase. No Tailwind.** One styling system: tokens + CSS Modules | `.decisionBar` |
| i18n keys | `<module>.<screen>.<element>.<purpose>` | `uw.workbench.decision.approve` |
| Microinteractions | `// MI-nn` code comment | `// MI-08 row select` |
| Signature moments | `SM-nn` component names | `<SignatureKnot />` (SM-01) |
| Test ids | `data-testid="<component>-<purpose>"` only when roles or labels are insufficient | |

## G.3 Tokens (§11.3, §11.4)

- **DTCG 2025.10 JSON.** Modes are carried in `$extensions["com.coreins.modes"]` (`dark`, `comfortable`, `touch`). `$value` is the light/compact default.
- Gradients and multi-layer shadows use the custom `$type` values `gradient-css` and `shadow-css` (stored as strings). Map them to strings if a design tool imports the file.
- Custom types also used: `cubicBezier`, `spring` (`stiffness`, `damping`, `mass`), `duration`, `typography`.
- **CSS output:**
  - `:root, [data-theme="light"]` with `color-scheme: light`, then `[data-theme="dark"]`, and `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) {…} }`.
  - Density: `:root, [data-density="compact"] { --size-row-table:32px; --size-control-md:28px }`, `[data-density="comfortable"] { 40px; 36px }`, `@media (pointer: coarse) { 48px; 44px }`.
- ⚠ **The §11.3 and §11.4 excerpts are stale.** They show `--color-interactive-primary-bg: #2E4FD6` and `--gradient-cta: linear-gradient(180deg, #3557E0, #2743BD)`. The generated `tokens/aegean.css` has `--color-interactive-primary-bg: #FFFFFF` (dark `#161A21`), `--color-interactive-primary-border-start/mid/end`, `--color-interactive-primary-glow`, `--color-text-on-primary: #161A21`, and `--gradient-cta: linear-gradient(120deg, #8250F2 0%, #3F63F0 50%, #14988D 100%)`. **Use the generated files, not the excerpts.**

## G.4 Glass material (§11.5)

- `.glass` uses `background-color: var(--_tint)`, `backdrop-filter: blur() saturate()`, and `box-shadow: inset 0 0 0 1px rim, 0 0 0 1px hairline`. The specular highlight is a `::before` with `gradient-specular`. Material variants are selected with `[data-material]`.
- **Fallbacks all resolve to the solid tint:** `[data-solid="true"]`, `:root[data-reduce-transparency="true"]`, `@supports not (backdrop-filter)`, and `forced-colors` (Canvas + a CanvasText border).
- **`GlassBudget`:** a registry that allows at most **N = 3** simultaneous backdrop-filter layers (N = 0 on the Low tier). Priority is 4 modal/palette > 3 popover > 2 float > 1 chrome. Chrome goes solid while a modal is open. API: `useGlass(priority)` returns `isSolid`; `<Glass material as className>`. Inside a modal, popovers render `<Glass material="popover" data-solid>`.
- The sample code types the `as` prop as `keyof JSX.IntrinsicElements`. Under React 19 types, use `React.JSX.IntrinsicElements` (the global `JSX` namespace is deprecated). This is a minor porting note.
- **ESLint allow-list for `<Glass>`:** TopBar, NavRail, StatusBar, Modal, CommandPalette, Popover, Menu, Listbox, DatePicker, HoverCard, Toaster, BulkActionBar, SignatureCard, WelcomeCard.

## G.5 Signature implementation (§11.6)

- `SignatureKnot` uses `motion/react` (`useAnimate`, `useReducedMotion`) plus app hooks `useCelebrationsEnabled`, `useDeviceTier` and `useSignatureCap("SM-01", objectId)`.
- A fixed SVG `<rect>` overlays the record sheet. The stroke is animated with `strokeDashoffset` (perimeter → 0, delay 0.3 s, 0.9 s, emphasized easing), then the overlay fades over 0.4 s.
- Any `keydown`, `pointerdown` or `wheel` (capture phase, once) calls `.complete()` on the running animations.
- The ripple is the CSS class `.ripple-once` on `[data-status-pill="{objectId}"]`.
- The button-to-check morph lives in **`Button` as a `data-state="committed"` variant** (clip-path transitions), reused by SM-01, SM-02 and SM-03.

## G.6 Data table rows (§11.7)

- `.row` height is `var(--size-row-table)`; the divider is `inset 0 -1px 0 border-subtle`; `contain: layout style`. Hover only under `@media (hover:hover)`.
- Selected: `state-row-selected` background, plus a 2 px left indicator `::before` that scales Y 0→1 (MI-08).
- Focus-visible: `inset 0 0 0 2px border-focus`. The **cursor row** (IB-02) gets a 50% `color-mix` ring.
- MI-07 quick actions fade and slide 4 px; always visible on touch.
- `.cellNumeric` right-aligned with `tabular-nums lining-nums`.
- RM, and `:root[data-reduce-motion="true"]`, kill transitions.
- **`Row.tsx`:** memoised TanStack row; `role=row`; `aria-rowindex = index + 2`; `aria-selected`; `transform: translateY(start)` for virtualisation; double-click opens; Checkbox `aria-label` via `intl.formatMessage({id:'table.row.select'},{label})` (**react-intl**; port to `t()`).
- ⚠ The **two-line queue row** (60 / 64 / 72 px, Part 2 §4.35 and the v3 mockup) is not reflected in the single `--size-row-table` token. A second row-height token per density is needed, and Part 2 requires a fixed height per row type for O(1) virtualisation.

## G.7 Device tier (§11.9)

`getDeviceTier()` returns `high | standard | low`:
- user override from `html[data-effects]` (`full` / `light`);
- otherwise a 7-day `localStorage` cache;
- otherwise heuristics: cores ≤ 4, or `deviceMemory` ≤ 4, or a software renderer (SwiftShader / llvmpipe / Basic Render via `WEBGL_debug_renderer_info`) → low; ≥ 8 cores and ≥ 8 GB → high; else standard.

A 120-frame rAF probe refines high vs standard. Three sessions with > 5% long frames (Long Animation Frames API) demote the tier.

## G.8 Stack notes and fallbacks (§11.10)

| Area | Decision |
|---|---|
| backdrop-filter | Glass budget N = 3; `@supports` fallback |
| Reduced transparency | **The app preference `data-reduce-transparency` is the source of truth**; the media query only sets the first-run default |
| View Transitions (MI-35, MI-58) | Feature-detect; fall back to Motion `layoutId` |
| Scroll-driven animations (MI-36) | Fall back to IntersectionObserver with `data-collapsed` |
| `linear()` easing | Declare `motion.ease.standard` first as the fallback |
| `offset-path` (SM-03) | Use Motion's JS path animation on Safari < 17 |
| Overlays | **React Aria overlays portalled to `#layer-root`**; native `<dialog>` only for the modal shell |
| `color-mix()` | Precompute the 50% focus colour as a token if old Safari matters |
| CSP | `style-src 'self'; style-src-attr 'unsafe-inline'` (Motion and virtualisation write inline styles); scripts `'self'` with a hashed bootstrap |
| Fonts | **Self-hosted WOFF2** under `/assets/fonts/*`, immutable cache, CORP same-origin, Latin + Greek subsets preloaded |
| Caching | `index.html` no-cache; hashed assets immutable |
| Auth | `/.auth/refresh` on a timer and on focus; on a 401, a re-auth popup and replay with the same idempotency key. **Assumes Container Apps built-in auth**, which conflicts with the ADR's MSAL React |
| ClearType | `wght` 420 for body-compact in dark mode |
| iOS zoom | Touch density sets inputs to 16 px |
| Large tables | TanStack Virtual, memoised rows, `useDeferredValue` for filter text, `startTransition` for sort and filter |
| PDF | `pdf.worker.min.mjs` from `/assets`; CSP `worker-src 'self'` |
| Hangfire dashboard | Out of scope for the design system |

## G.9 CI quality gates (§11.11)

| Gate | Tool | Blocks when |
|---|---|---|
| Tokens current | `build_tokens.py && git diff --exit-code` | Generated files differ |
| Contrast | `contrast.py`, `dataviz_palette.py` | Any FAIL |
| No raw values | **Stylelint** `declaration-property-value-disallowed-list` + a custom plugin allowing only `var(--…)` for colour, spacing, radius, z-index, duration and easing | Violation |
| Animated properties | Stylelint: only transform, opacity, clip-path, stroke-dashoffset, background-color, color, border-color, box-shadow (box-shadow only on pseudo-elements) | Violation |
| Glass placement | **Custom ESLint rule** with the allow-list | `<Glass>` elsewhere |
| Status rendering | **Custom ESLint rule**: status only through `<StatusPill>` | Hand-rolled pill |
| a11y | `@axe-core/playwright` on all stories and routes × theme × density × locale | Serious or critical |
| Visual regression | Playwright screenshots × theme (2) × density (2) × locale (el, el-XA, en) × RM | Unapproved diff |
| Performance | Playwright trace on a reference low-end VM: 1,000-row workbench scroll, palette open, SM-01 | Frame > 33 ms during MI/SM; INP > 200 ms |

"All stories" implies **Storybook** (not in the ADR list).

## G.10 Component API conventions (inferred from §11 + Part 2)

- Components are React Aria Components skinned by CSS Modules. State styling uses `data-*` selectors, never JS-computed class names.
- Do not mix two primitive libraries (Part 2 stack note). If a primitive is missing, build it on React Aria hooks (`useMove`, `useKeyboard`).
- Button variants: `primary | primary-gradient (commit) | secondary | ghost | danger | danger-ghost | link | ai`. Sizes: `sm | md | lg`. The committed state is `data-state="committed"`.
- Status must go through `<StatusPill>` with a required label.
- Signature components are named by moment (`<SignatureKnot/>` etc.) and gated by `useSignatureCap`, `useCelebrationsEnabled`, `useDeviceTier` and `useReducedMotion`.
- Icons: `lucide-react` with `absoluteStrokeWidth`, plus `<Icon name="…"/>` for the 18 custom icons. A wrapper should apply the size→stroke rule (12–16 px → 1.5; 20 → 1.75; ≥ 24 → 2).
- App preferences are attributes on `<html>`: `data-theme`, `data-density`, `data-reduce-transparency`, `data-reduce-motion`, `data-effects`.

---

# Part H: The approved v3 mockup (`aegean-screens-v3.html`)

The mockup's header comment says: "glass chrome (rail + top bar) floating over an ambient Aegean canvas; every working surface is an opaque sheet. Tokens = design-guide Part 1. Production self-hosts fonts (Part 1 §2.3.1); Google Fonts is used here only because this preview cannot host files."

## H.1 Screens

A review bar outside the product («ΜΑΚΕΤΑ · ΔΕΙΓΜΑΤΙΚΑ ΔΕΔΟΜΕΝΑ») switches between three screens and also sets theme (Αυτόματο / Φωτεινό / Σκοτεινό) and density (Συμπαγής / Άνετη).

1. **Screen A: Πύλη διαμεσολαβητή (broker home)**, §5.8. Switching to it forces **comfortable** density.
2. **Screen B: Πάγκος ανάληψης κινδύνου (UW workbench)**, §5.7. Switching to it forces **compact** density.
3. **Screen C: Φάκελος ζημίας (claim workbench / file)**, §5.5.

Overlays: the **command palette** (`Ctrl+K`), **toasts**, the **«Γιατί;» explain-why popover**, plus the SM-06 receipt and an SM-03-style flight-and-seal sequence.

## H.2 App shell structure

```
.app  CSS grid: columns 56px | 1fr ; rows 48px | 1fr | 24px ; isolation: isolate
├─ .ambient (z -1)   4 blurred colour blobs (sea1..sea4, blur 60px) + scrim ::after
├─ nav.rail.glass    rows 1–3 (full height), 56px wide
│    logo tile 32px "Α" (radius 9, --g-cta, blue glow)
│    6 module buttons 40×40 (radius 8): Αρχική, Εργασίες, Πελάτες, Ανάληψη κινδύνου, Ζημίες, Χρεώσεις
│    spacer, Ρυθμίσεις
│    tooltip to the right (inverse bg, 12px, delay .25s)
│    selected: br-bg tile + 3px left indicator bar (row-ind colour), aria-current="page"
├─ header.top.glass role=banner, 48px
│    entity chip «Παράδειγμα Ασφαλιστική Α.Ε. ▾» (bordered, 12px) + breadcrumb
│    palette trigger (centred, width min(440px,40vw), 30px, radius 8) «Αναζήτηση ή εντολή…» + kbd «Ctrl K»
│    bell icon button 32px with count badge 16px (#C81E1E, 2px sheet border), aria-label «Ειδοποιήσεις, 3 νέες»
│    avatar 28px (initials)
├─ main.main  padding 8px; hosts .screen sections (.on → display flex + screenIn 320ms enter)
└─ footer.status.glass role=contentinfo, 24px, 11.5px
     ● Συνδεδεμένο · «Παράδειγμα Ασφαλιστική Α.Ε. · PROD» · ● ΤΝ: ενεργή · right: «Τετ 07/10/2026 · 14:32 EEST»
```

**Glass in the mockup:** `rgba(248,249,251,.80)` (dark `rgba(18,21,27,.84)`), `blur(24px) saturate(180%)`, rim inset + hairline; specular `::before` `linear-gradient(180deg, rgba(255,255,255,.5) 0, transparent 42%)`.

**Ambient drift (from v2, = MI-70):** blobs animate `drift` over 24–36 s, alternate, ±6% translate, scale 0.95–1.1. The gutter scrim is `rgba(246,247,249,.66)` (dark `rgba(14,17,22,.72)`); the canopy keeps 0.80/0.82.

**Responsive (mockup-only breakpoints):**
- **≤ 1100 px:** the work-view sidebar is hidden; worklist and KPIs go to 2 columns; detail and claim bodies go to 1 column.
- **≤ 760 px:** the rail becomes a **bottom tab bar** (row 3, 56 px; logo, spacer and tooltips hidden; the indicator moves to the bottom). Top bar 52 px. Status bar hidden. Search collapses to an icon. Single-column cards. The UW screen stacks with the queue capped at 340 px.

The guide's breakpoints are `bp.sm` 600–904 (bottom tab bar), `bp.lg` 1240–1439 and `bp.xl` 1440–1919. **Use the Part 1 breakpoint tokens, not 760/1100.**

## H.3 Components visible (with measured CSS)

**Global and type:**
- Body 13/20 compact; comfortable 14/22. `font-feature-settings: "kern","calt"`. `.num` = `tabular-nums lining-nums`. `.mono` = JetBrains Mono 12.5 px with `tabular-nums slashed-zero`.
- Focus: `outline: 2px solid var(--focus); outline-offset: 2px`.
- Overline: 11 px, 600, +.06em, `t3`. H2: 20/28, 600, −.01em. H3: 15/22, 600, −.006em. Caption: 12/16 `t3`.
- Greeting: 32/40, 600, −.019em, `text-wrap: balance`. Hero value: 40/48, 600, −.022em.

**Buttons (`.btn`):**
- Height `--ctl` (28 compact / 36 comfortable), padding 0 12, **radius 6**, 1 px `b-control` border, 500 weight, 13 px. Active `scale(.97)`.
- **Final v3 override, option 17 ("gradient border"):** primary and cta use `background: linear-gradient(fill,fill) padding-box, var(--gb-border) border-box` with `border: 1.5px solid transparent`, label `t1`, **weight 600**. Border gradient: `linear-gradient(120deg, #8250F2 0%, #3F63F0 50%, #14988D 100%)`; dark `#9B74FC → #6283FA → #2BB5A8`.
- Shadows: rest `0 1px 2px rgba(15,23,42,.06)`; CTA rest `… , 0 4px 14px -6px rgba(63,99,240,.45)`; hover adds a tint layer `rgba(63,99,240,.05)` and `0 6px 20px -8px rgba(63,99,240,.55)`.
- An optional specular "sweep" `::after` uses `rgba(63,99,240,.12)`.
- `forced-colors`: ButtonFace / ButtonText border.
- The file still contains the superseded **ink** and **smoked-glass** button layers (with `!important`), which v3 overrides. **Port only the final layer.**
- The `kbd` hint inside a button is mono 10.5 px at opacity .55.
- `.btn.ghost` is transparent. `.btn.ai` uses ai-bg / ai-fg / ai-bd.

**Other controls and display components:**
- **Status pill:** 20 px high, padding 0 7, **radius 4**, 12 px, 500, 1 px border in family `bd`, 12 px icon with stroke 1.7. Families: n, s, w, d, i, br (brand), te (teal), pl (plum), ai, and **crit** (solid `#C81E1E`, white). Morph: `pillIn` 180 ms (translateY 6 → 0).
- **Filter chip:** 24 px, radius 12, brand family, «… ×».
- **Delta chip:** 22 px, radius 11, 12 px, 600, success or danger family, arrow glyph ▲.
- **kbd:** mono 11 px, sunken background, 2 px bottom border.
- **Segmented control:** sunken track, radius 6, 2 px padding; selected = sheet + `e1`; `aria-pressed`.
- **Input:** 36 px, radius 6, `b-control` border. Focus: `border-color: focus` + `0 0 0 3px halo` (`rgba(63,99,240,.22)`).
- **Plate input:** mono 15 px, +.04em letter-spacing. **It maps Latin keystrokes to Greek capitals** (A B E Z H I K M N O P T Y X → Α Β Ε Ζ Η Ι Κ Μ Ν Ο Ρ Τ Υ Χ) and uppercases. This is a useful Greek-plate behaviour; Part 2 §4.7 is the spec.
- **Sheet / card:** sheet radius 10 with a hairline ring; card radius 10, 1 px `b-default`, padding `--pad` (16 / 24). Quick-quote card radius 14 with `e3`.
- **Elevations e1–e5:** two-layer shadows. In dark mode they add an inset white ring of 0.04–0.08.
- **Work-left card (`.wl`):** an 8 px status dot at top-left with a glow (`color-mix` 18% ring + 10 px blur, from v2); title H3 + count 22 px/600; up to 3 items (12.5 px) with a pill or a ghost action button (24 px); hover `e2`.
- **KPI tile:** 26/34 600 value, caption + delta, 32 px sparkline.
- **Sparkline:**
  - line 1.5 px `br-so` (danger solid when the KPI is adverse);
  - area fill gradient from 0.22 opacity to 0;
  - endpoint `r = 3.5` with a 2 px sheet stroke;
  - draw animation 600 ms via `stroke-dasharray`/`--len`;
  - the hero sparkline is 220×56 with `role=img` and an `aria-label`.
- **Statement card (`.stmt`):** headline 28/36 600; line items with a top border; negative values use the danger colour and `−`.
- **Timeline (`.tl`):** 1 px rail at 5 px; 9 px dots in family colour with a 2 px sheet ring; caption with actor and relative time.
- **Avatar:** 28 px, initials 11 px/600. Colours `#2E4FD6`, `#0E7A72`, `#B32D82`, `#6F36DE`, `#075E8E` (contrast-verified). The presence stack overlaps by −8 px with a 2 px sheet ring.

**UW workbench (screen B):**
- **Work views** (220 px, sunken, radius 10):
  - items 30 px; selected = sheet + `e1`, `aria-current="true"`;
  - counts tabular with a **bump** animation (scale 1.15, 200 ms);
  - a «Πρόοδος ημέρας» card at the bottom: «11 από 19», a 6 px bar in `g-tide`, width transition 400 ms.
- **Queue** (`min(620px, 47%)`):
  - header «Οι παραπομπές μου» + filter chips;
  - grid columns **44 px | 1fr | 132 px | 76 px | 112 px** (Επιλογή | Πελάτης | Προτεραιότητα | SLA | Ασφάλιστρο);
  - **two-line rows at 60 px** (64 comfortable). Line 1: name 13.5/500. Line 2: mono id 11.5 `t2` · 3 px dot · product;
  - headers 38 px, 12 px/500 `t3`, single line; the sorted header turns `t1` with an arrow icon and `aria-sort`;
  - priority bar 64×6 px + number;
  - SLA pill;
  - premium right-aligned with a small «€» suffix in `t3` 12 px;
  - checkbox 16 px, radius 5, opacity .55 until row hover or selected;
  - selected row: `row-sel` plus a 2 px indicator (`ind` 150 ms);
  - leaving row: opacity → 0 and `translateX(-24px)`, 240 ms, exit easing;
  - footer «8 εγγραφές · 4.721 κανονικές κρυμμένες».
- **Detail pane** (`container-type: inline-size`; at ≤ 820 px it goes single-column):
  - header: overline «ΠΑΡΑΠΟΜΠΗ · {PRODUCT}», name, mono id, plum «Σε παραπομπή» pill, presence «Ο παραγωγός βλέπει», facts row;
  - body: AI risk card, issue card (rule name + «Πριν τη δέσμευση» warning pill; «Τιμή X · όριο κανόνα Y · Γιατί;»), sanctions issue «Καθαρό» (success);
  - key–value card: Προϊόν, Πακέτο, Ιστορικό ζημιών, Τρόπος πληρωμής, Κανόνας (mono `UW-MOT-014 v7`), Λήξη SLA.
- **Decision bar:**
  - authority text «Η εξουσιοδότησή σας: … · κεφάλαιο έως 60.000 €»;
  - a 6 px meter with a success fill (danger when ≥ 100%) and a 2 px limit tick;
  - buttons «Απόρριψη…», «Έγκριση με όρους…», CTA «Έγκριση Ctrl ↵».
- **SM-06 as built:** the meter animates to 100%; the receipt «Απόφαση: Κώστας Νικολάου · 14:32» shows with an SVG signature path (`dasharray 200`, 600 ms draw); the row leaves after 900 ms (first decision of the session) or 240 ms (later ones); counts bump; the day bar advances; a toast with **Αναίρεση** (undo) appears.
- **Keyboard:** `J`/`↓`, `K`/`↑`, `A` approve, `Ctrl+Enter` approve. Ignored in inputs and with modifiers.
- **Empty queue state:** a 240×140 inline SVG (house, horizon, Laurel arc) + «Η ουρά σας είναι άδεια» + «Ολοκληρώσατε N παραπομπές σήμερα.»

**Claim file (screen C):**
- **Header:** overline «ΖΗΜΙΑ · ΑΥΤΟΚΙΝΗΤΟ ΙΧ · ΥΛΙΚΕΣ ΖΗΜΙΕΣ», name H2, mono id, info pill «Ανοιχτή», warning clock pill «Προσφορά σε 23 ημ.» (`title` tooltip «Προθεσμία προσφοράς 3 μηνών: λήγει 07/01/2027»).
- **Facts:** Ημ. ζημίας, Αιτία, Ασφαλιστήριο link (mono), «Φιλικός Διακανονισμός Επιλέξιμη ✓».
- Presence avatars; buttons Ανάθεση, Απόθεμα.
- **Claim strip:**
  - stages **Αναγγελία · Επαφή · Εκτίμηση · Προσφορά · Πληρωμή · Κλείσιμο**;
  - nodes 22 px with a 1.5 px border; done = success solid + white check; current = primary + 4 px halo;
  - connectors 2 px filled with `g-tide` via `scaleX(var(--f))` over 320 ms;
  - deadline flag «⚑ 07/01/2027» in warning fg, 10.5 px/600, positioned within a connector.
- **AI summary card:**
  - **iris gradient 1 px border** (`padding-box` / `border-box` trick) on an ai-bg fill;
  - sparkle icon, «Σύνοψη ΤΝ», ai pill «Υψηλή βεβαιότητα 0,91», «Πρόταση · απαιτεί έλεγχο»;
  - superscript **citations** «[1]» in the link colour;
  - **approve-the-diff** box: «Απόθεμα · Υλικές ζημιές  ~~5.220,00 €~~ → **6.480,00 €** (+1.260,00 €)». The old value is struck through in `t3` with a **danger-coloured strike line**;
  - actions: Αποδοχή (ai), Επεξεργασία, Απόρριψη (ghost), Γιατί;
  - on accept: a 320 ms coin flight to the table cell, the cell flashes `br-bg` over 1.2 s, the incurred headline updates, and the actions are replaced by «✦ Δημιουργήθηκε με ΤΝ · Αποδεκτή από Κώστα Νικολάου · 14:32 · Αναίρεση», with a live-region message and a toast.
- **Explain-why popover:**
  - 300 px, radius 10, `material.overlay` (blur 20, saturate 150%), `e3`, `pop` 200 ms;
  - «Γιατί 6.480,00 €;» and «Βασικοί παράγοντες · μοντέλο CLM-RES v4.2 · βεβαιότητα 0,91»;
  - factor rows (label | bar | signed value) growing in with a 24 ms stagger; positive bars brand, the negative bar `#A86A0C`;
  - sources line.
- **Exposures table** (real `<table>`): Έκθεση (mono 01–03), Κάλυψη, Κατάσταση pill, Απόθεμα, Πληρωθέντα (right-aligned tabular).
- **Financials statement:**
  - «Πραγματοποιηθείσες ζημίες 5.720,00 €» = «Πληρωθέντα 500,00 € + Απόθεμα 5.220,00 €»;
  - payment lines with an «Εγκρίθηκε» pill and «εγκρίθηκε από Μ. Παπαδοπούλου»;
  - recovery in success colour «+2.900,00 €»;
  - CTA «Αποδέσμευση πληρωμής» + «Νέα πληρωμή».
- **Release sequence:**
  - the pill morphs to teal «Αποδεσμεύτηκε»; live region;
  - a Laurel coin flies to the «Πληρωμή» stage over 600 ms (a 3-keyframe arc, scale to .6);
  - the stage turns done and the connector fills;
  - **a 72 px Laurel seal "stamp" + ring ripple** appear at the node and fade after 900 ms;
  - the timeline prepends a flashing entry; the button becomes «Αποδεσμεύτηκε ✓»; a toast «… · αρχείο SEPA 14:35» appears.
- **History timeline.**

**Command palette:**
- Fixed at `top: 14vh`, width `min(640px, 100% − 32px)`, **radius 20**, overlay glass (blur 32, saturate 160%), `e5`, opening animation 240 ms (scale .97 → 1). Scrim `rgba(14,17,22,.40)` (dark .60).
- Input row 56 px, 16/24 text, search icon, kbd «Esc».
- Groups (uppercase overline): **Ενέργειες**, **Μετάβαση σε**, **Εγγραφές**. Items 44 px (icon + title/subtitle + kbd shortcut such as «Q», «N Z», «G P», «G H»). Selected = `ghost-p`. Matches are highlighted with `<mark>` (brand background).
- Footer key hints: «↑↓ πλοήγηση», «↵ άνοιγμα», «**Ctrl ↵ νέα καρτέλα**», plus a polite result count.
- No results: «Καμία αντιστοιχία · Αναζήτηση σε πλήρες κείμενο (↵)».
- **Matching:** NFD, strip combining marks, `toLocaleLowerCase('el')`, `ς→σ`, plus a naive Greeklish map (a→α, b→β … u→ου, v→β, c→χ, h→η, w→ω). Production should use ELOT 743 both ways (Part 2).
- ARIA: `role=dialog aria-modal`, input `role=combobox` with `aria-activedescendant`, `role=listbox`/`option`. Focus is restored on close.

**Toasts:**
- Fixed bottom-right (right 16, bottom 36), `column-reverse`, gap 8, width `min(360px, …)`, **max 3**.
- Float glass (`rgba(255,255,255,.90)`, blur 24, saturate 170%), **radius 14**, `e4`.
- Enter: 300 ms from translateY 16 + scale .96. Exit: 200 ms translateX 24.
- Success icon; title 14/500 + body; optional **Αναίρεση**; close ×.
- A 2 px progress bar shrinks over **6 s**. The timer pauses on hover and resumes with 3 s on mouse leave.
- The region has `aria-live="polite"`. A separate `.sr` live region is used for outcome sentences.

**Count-up (from v2, = MI-69):** on entering Home, the hero value, KPI values, work-card counts and the **commissions statement headline** count up over 1,300 ms with ease-out `1 − 2^(−10t)` and el-GR formatting. `aria-label` holds the final text. Skipped under RM. The script comment says "never on approval or payment values".

**Motion constants in the mockup:** `--ease: cubic-bezier(.2,0,0,1)`, `--enter: cubic-bezier(.05,.7,.1,1)`, `--exit: cubic-bezier(.3,0,.8,.15)`, stamp spring `cubic-bezier(.34,1.56,.64,1)`. A blanket `prefers-reduced-motion` rule sets every duration to `.01ms`.

## H.4 CSS and visual details in the mockup that are not in Parts 3–4

(Several may be in Parts 1–2, which this digest only grep-checked.)
- Shell grid 56 / 48 / 24 px (this matches Part 1 §3.4).
- Rail selected state: a 40×40 radius-8 `br-bg` tile + **3 px left indicator bar**. §7.1 says a 32×32 tile with a 20% `brand.solid` fill and does not mention an indicator bar.
- Mockup icons are hand-written SVG paths at **18 px, stroke 1.6**. §7.1 says 20 px at 1.75 in the nav rail via Lucide.
- Logo tile "Α" uses the old blue `--g-cta` (180°) with a blue glow.
- Notification badge `#C81E1E` hard-coded (the same value as the `crit` pill).
- Negative factor bar `#A86A0C` hard-coded (not a token).
- Danger-coloured strike-through on old values (`text-decoration-color: d-so`). Part 2 timeline diff only says "old value in `text.tertiary` strikethrough".
- Work-card status dot with glow (v2 carry-over); hover `e2`.
- Toast progress bar, 3-toast cap, and resume-with-3 s behaviour.
- Count bump (scale 1.15) on work-view counts.
- Delta chip shape (22 px, radius 11).
- AI card iris border via a background-clip trick (Part 2's `ai` button says `border-image`).
- Palette shortcut hints («Q», «N Z», «G P», «G H») and the «Ctrl ↵ νέα καρτέλα» footer.
- Detail pane container query at 820 px.
- Plate input Latin→Greek keystroke mapping.

## H.5 What the mockup does not show

- The quote-to-bind wizard, policy record and spine, FNOL, billing, the approval inbox, the notification drawer, side sheets, modals and confirmations, welcome screen, SM-01, SM-02 (except as borrowed in the release sequence), SM-04, SM-05, SM-07 (only a static placeholder) and SM-08.
- Charts other than sparklines.
- The «?» help button in the top bar (required by G-21).
- An expanded nav rail; non-prod environment ribbon; skip link; `<html lang>`.

These must be built from the written spec.

---

# Part I: Conflicts and ambiguities

## I.1 Mockup vs written guide

| # | Conflict | Sources | Recommendation |
|---|---|---|---|
| M1 | **Primary/CTA button:** v3 uses a surface fill + violet→blue→teal gradient border. Part 4 §11.3/§11.4 excerpts show a solid blue `#2E4FD6` primary and a 180° blue `gradient.cta`. SM-01's "circle fill cross-fades `gradient.cta` → `gradient.laurel`" and §5.8.2's FAB "56 px, `gradient.cta`" assume a filled gradient | mockup L514–542; Part 1 §2 / Part 2 §4.1 updated; Part 4 §11.3–11.4, §10 SM-01 stale | Use `tokens/aegean.css` (v3). Define how the SM-01 morph and the FAB render with a border-only CTA (e.g. a Laurel fill only in the committed circle state) |
| M2 | **Count-up / ticker:** the mockup and Part 1 C-07 / MI-69 count up hero, KPI, work-card counts **and statement headlines** on every Home visit. Part 3 §6.2 ("no ticker on first render") and SM-08 ("no ticker on first render; plain fade") say the opposite. The mockup also counts up the **commissions payable** statement headline, which is arguably a financial value (C-07 forbids counting in financial-transaction contexts) | mockup L1118–1135; Part 1 C-07; Part 2 MI-69; Part 3 §6.2; Part 4 SM-08 | Follow C-07/MI-69 (newer), but exclude money-owed headlines in statements. Needs a design decision |
| M3 | **Greek uppercase:** the mockup builds the overline `ΠΑΡΑΠΟΜΠΗ · ${prod.toUpperCase()}` and palette group labels with JS `toUpperCase()`, giving «ΑΥΤΟΚΊΝΗΤΟ», «ΜΕΤΆΒΑΣΗ ΣΕ» with tonos | mockup L951, L1077 vs Part 1 §2.3.4, Part 4 §11.8 | Always use `toGreekUpper()`. Do not copy the mockup code |
| M4 | **SM-03 visuals:** the mockup's release sequence adds a **72 px Laurel seal stamp + ripple** (SM-02 vocabulary) at the stage node, uses a Laurel coin with no Tide ghost trail, and takes 600 ms in total. The spec: chip lift, Bézier flight with a **Tide trail**, node fills brand→success with a check, **no seal** | mockup L1036–1051 vs Part 4 SM-03 | Follow Part 4. Ask the designer whether the seal on payment release is intended |
| M5 | **SM-06 meter:** the mockup fills the meter to 100%. The spec fills to the decision marker and pulses the marker | mockup L996 | Follow the spec |
| M6 | **Rail selected state and icon metrics:** see H.4 (3 px bar vs 20% fill tile; 18/1.6 vs 20/1.75) | mockup L142–147 vs §7.1 | Follow §7.1 for sizes. Ask the designer whether the indicator bar is approved (Part 1 was not checked for this) |
| M7 | **Breakpoints:** the mockup uses 1100/760 px. The guide uses the `bp.*` tokens (bottom tab bar at < 905) | mockup L385–409 vs Part 1 §3 | Use the tokens |
| M8 | **Reduced transparency:** the mockup uses `@media (prefers-reduced-transparency)`. Part 4 §11.10 says the **app preference** is the source of truth | mockup L137, L511 | Follow §11.10 |
| M9 | **Queue columns:** the mockup has 4 data columns (Πελάτης two-line with id and product, Προτεραιότητα, SLA, Ασφάλιστρο). §5.7.1 lists 9 (Παραπομπή, Πελάτης, Προϊόν, Παραγωγός, Ασφάλιστρο, Προτεραιότητα, SLA, Ζητήματα, Ανάθεση) | mockup L712–718 | Two-line row + column chooser; confirm the default columns |
| M10 | **Help «?» missing** from the top bar (G-21, Part 1 shell); no skip link; no `<html lang>` (G-01, G-02) | mockup L581–589 | Add them in the build |
| M11 | **Fonts from Google Fonts** in the mockup. Production must self-host (Part 1 §2.3.1, §11.10 CSP `'self'`) | mockup L3–5 | Self-host the WOFF2 subsets |
| M12 | **Hard-coded colours:** `#C81E1E` badge, `#A86A0C` negative factor bar, avatar hexes, Laurel stops inside SVG. These violate the Stylelint "no raw colours" gate | mockup | Add tokens (e.g. `status.danger.critical`, a chart adverse/negative token) |
| M13 | Claim header actions: the mockup shows only «Ανάθεση», «Απόθεμα». §5.5.1 lists Πληρωμή, Απόθεμα, Ανάθεση, ⋯ | mockup L747–748 | Follow the spec |
| M14 | The mockup's AI reserve «Αποδοχή» applies directly, with no maker-checker branch. §5.5.1 requires maker-checker above authority | mockup L1015–1022 | Follow the spec |
| M15 | Status bar shows «14:32 EEST». Part 1 says EET/EEST in the status bar; §8.2 says the suffix only where cross-zone confusion matters | — | Keep it in the status bar only |
| M16 | Filtered-empty: §5.11 says no illustration, while ILL-04 is catalogued for "No search results / filtered empty" | Part 3 §5.11 vs §7.3.2 | Use ILL-04 only for zero full-search results |

## I.2 Guide vs binding stack (new dependencies and conflicts)

| # | Item | Guide says | Stack (ADR) | Impact |
|---|---|---|---|---|
| S1 | **i18n library** | FormatJS **`react-intl`**, ICU MessageFormat (§8.6; `useIntl` in the §11.7 code) | **react-i18next** | Conflict. Options: `i18next-icu` (+ `intl-messageformat`) for ICU plural/select syntax, or i18next native `_one/_other` keys plus context for gender `select`. The §8.6 rules (fallback to Greek, never show keys, telemetry on missing keys, no concatenation) all map onto i18next config |
| S2 | **Authentication refresh** | Container Apps built-in auth, `/.auth/refresh`, silent iframe renew (§5.12, §11.10) | **MSAL React** + `Microsoft.Identity.Web` | Conflict. Use the MSAL `acquireTokenSilent`/popup equivalents, keep the 2-minute warning banner, and keep idempotent replay |
| S3 | **Motion for React** (`motion`) | Required for springs, FLIP, `useAnimate`, SM components | Not listed | New dependency (MIT) |
| S4 | **visx** (`@visx/*`) | Charts | Not listed | New dependency (MIT). No chart library is named in the ADR |
| S5 | **Lucide** (`lucide-react`) | Icons, **ISC** licence | Not listed | New dependency (permissive) |
| S6 | **TanStack Table v8 + Virtual v3** | Data tables | ADR lists TanStack **Query** only | New dependencies (MIT) |
| S7 | **pdf.js** (`pdfjs-dist`) | Document viewer | Not listed | New dependency (Apache-2.0); CSP `worker-src` |
| S8 | **libphonenumber** | Phone display (§8.2) | Not listed | New dependency (`libphonenumber-js`, MIT) |
| S9 | **Fonts**: Inter 4.1 (variable), Noto Sans, JetBrains Mono, Noto Sans Mono | Self-hosted WOFF2 | — | Asset dependency (SIL OFL 1.1). The CI font-coverage check uses Python **fontTools** |
| S10 | **Python in CI** | `build_tokens.py`, `contrast.py`, `dataviz_palette.py`, fontTools | GitHub Actions; .NET + Node toolchain | Adds a Python step to the pipeline |
| S11 | **Storybook** (implied by `*.stories.tsx`, "axe on all stories") | Component workbench | Not listed | New dev dependency |
| S12 | **Stylelint + custom plugin; custom ESLint rules** | CI gates | Not listed | New dev tooling |
| S13 | **`@axe-core/playwright`** | a11y in CI | Playwright is listed | Small addition |
| S14 | **`@internationalized/date`** | Standalone month, el calendar | Comes with React Aria | OK |
| S15 | **React Hook Form + Zod** | Not mentioned | Listed | No conflict, but the guide gives no integration with React Aria fields or with the §8.4 error formula. Define a `zod` → i18n key mapping |
| S16 | **Map tiles** for choropleths (§6.2) | "muted greyscale map tiles" | — | Provider unspecified; an external tile service would be a new dependency and needs CSP and GDPR review |
| S17 | **`/web` folder** | §11.1 | ADR: SPA served by ASP.NET Core | Confirm the folder name in `core-insurance-app` |
| S18 | React 19 typing | §11.5 uses the global `JSX.IntrinsicElements` | React 19 types | Use `React.JSX` |

## I.3 Internal gaps and ambiguities in Parts 3–4

- The **heatmap text-colour switch helper** is promised in §11 (Part 3 self-check) but no code is given.
- **Greek amount-in-words** (approvals, payments, currency input) has no implementation guidance.
- **Two-line row height token** is missing from `--size-row-table` (see G.6).
- **Number abbreviation and per-mille helpers** are not in `format.ts`.
- **Frecency half-life (14 days)** and **suggested-match confidence threshold** are provisional (Part 3 self-check).
- The vocative helper's coverage and the optional chime values are provisional (Part 4 self-check).
- **The policy spine and claim strip are new visuals** that need usability validation with underwriters and claims handlers before build (Part 4 open item 4).
- **"Aegean" is a placeholder brand** (open item 1). The legal entity name in the mockup, «Παράδειγμα Ασφαλιστική Α.Ε.», is sample data.
- Greek terms marked † await ROLE-46.
