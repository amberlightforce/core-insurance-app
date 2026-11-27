# Wave-5 screen review vs Aegean mockup v3 (2026-11-27)

Live stack `coreins` (API+web on http://127.0.0.1:25000, current main e46176ff), dev personas, Playwright Chromium.
Each page: `<page>-light.png` / `-dark.png` (1440x900, full page) and `-390.png` (390x844). Reference: `mockup-home|uw|claim-light.png`.
Verdicts: **matches** / **minor** gaps / **off** = clearly off-mockup. V = visual issue, D = data or back-end issue (not a styling defect).

Not captured (no seed data, list pages have no rows): manual journal detail, document detail, fiscal document detail. Documents subject list used a guessed `policy/policy/1` ref and got HTTP 422, so only its error state is shown; the real subject list and document detail still need a shot once a document exists. Product detail was captured via `/products/MOTOR-GR`.

## Worst five (visual first, then user-visible error states)
1. **Billing manager home** (`home-billing-mgr`): all three KPI cards and the Latest invoices card show red "The data couldn't be loaded" banners. D: `GET /api/bil/v1/invoices?limit=50` returns 403 for `Staff.BillingManager`. Home cannot show its hero KPIs at all. Also V: the role home has no hero KPI number (RI and migration homes do).
2. **Documents subject list** (`documents-subject`): whole page is an error banner. D: 422 from `list-for-object` (my ref format may be wrong; verify with a real object). V: framed "sheet" page, so the lookup page and the result page look unrelated (one hero band + two cards, the other a white sheet).
3. **Trial balance / New manual journal** (`fin-trial-balance`, `fin-journal-new`): ops list and form pages use the framed record-sheet layout (as the mockup claim file does) instead of the hero band + elevated cards used by `/finance`, manual journals list, period close. V: no coloured-dot card headers, flat inner cards, inconsistent with sibling finance pages.
4. **Compliance officer home** (`home-compliance`): Open clocks card shows the error banner. D: `GET /api/cmp/v1/clocks/query?limit=200` returns 403 for `Staff.ComplianceOfficer`. Data-request card fine.
5. **Product detail** (`product-detail`): V: sheet layout with no dot headers; at 390px tables become nested cards inside a card with a big left inset, and "Windscreen and glass breakage" runs past the card edge. Coverage/rating tables are plain; status pills fine.

## Per page

| Page | Persona | Verdict | Notes |
|---|---|---|---|
| finance-home (`/`) | finance | minor | V: shell, hero, quick-actions card and dot card match. Only one tiny KPI card ("Recent policies 0") and ~60% of the viewport is empty; no hero KPI. Dark and 390 fine. |
| finance-ops (`/finance`) | finance | minor | V: "Finance operations" card is a plain underlined-link list with no dot header or icons; "Accounting periods" card is good (dot + count + Open pill). "Look up by policy" card has no dot. |
| fin-trial-balance | finance | minor | V: sheet layout, no hero band, no dot headers (see worst 3). D: empty because no postings in 2026-11 (empty state looks fine). |
| fin-journals | finance | matches | Hero band, card, empty state with title + hint. V nit: card header has no dot. |
| fin-journal-new | finance | minor | V: sheet layout, no dot headers on Header/Lines cards; Save draft sits outside any card. Info and warning banners and dark mode are good. |
| fin-intake-exceptions | finance | matches | Hero + card + good empty state. V nit: no dot header. |
| fin-period-close | finance | matches | Hero + table card + Open pill. V nit: no dot header. |
| fin-earning-run | finance | minor | V: hero, dot on "Run earning", warning banner good; "Latest run" card has no dot. D: `GET /api/fin/v1/earning/status` 404 renders a raw red "Not Found" banner; should be an empty "no run yet" state. |
| home-billing-mgr | billingmgr | off | See worst 1 (D: 403). Dot cards and hero shell match. |
| home-ri-mgr | rimgr | minor | V: hero KPI with chip, dot cards and KPI tiles good; no sparkline/trend pills, KPI tile row duplicates the dot cards (Awaiting approval and Active appear twice). Zeros only because no treaties. |
| home-ri-acct | riacct | minor | Identical to RI manager (same duplicate KPI row). |
| home-compliance | dpo | off | See worst 4 (D: 403). V otherwise matches. |
| home-migration | migmgr | minor | V: hero KPI "0 / opened in this browser" is a weak hero metric; one small dot card; page mostly empty. |
| products | superuser | matches | Hero band, elevated card per product, In-force pill, Locked pill, clean table. V nit: no dot header on product card. |
| product-detail | superuser | minor | See worst 5. 1440 light and dark fine; 390 overflow of long cell text. |
| documents | superuser | matches | Hero band + two elevated cards, forms spaced well. V nit: no dot headers. |
| documents-subject | superuser | off | See worst 2 (D: 422). |
| reports | superuser | minor | V: hero + dot cards (Receivables, Delinquency) good; "Detailed views" card is a plain underlined-link list with no dot or icons; Receivables card shows empty table header plus empty state (header row above "Nothing to report" looks redundant). |
| billing-suspense | billingmgr | matches | Hero, dot card with count, table head over illustrated empty state. |
| billing-plans | billingmgr | matches | Hero, dot card, populated table (POL...), search field. V nit: State column is plain text, not a pill. |
| billing-mandates | billingmgr | matches | Hero, info banner, dot card, empty illustration. |
| billing-account-currents | billingmgr | matches | Hero, dot card, "Not available yet" empty state is honest. D: endpoint not served yet. |
| compliance-fiscal | dpo | matches | Hero, two cards, Registered pills, mono MARK, 390 renders as cards correctly. V nit: no dot headers on the two cards. |
| compliance-ai-systems | dpo | minor | V: hero + two cards good, Draft pills; "Register" form card has a 4-field row with no dot and the Actions column is bare text ("Submit for review" / red "Retire" links), not buttons. |

## Cross-cutting
- Shell (rail, top bar, status bar, ambient wash, elevated cards, dot headers on newer cards) matches the mockup; dark mode renders with no contrast problems in any page I opened (new journal, RI home spot-checked; the rest scanned).
- No page overflowed horizontally at 390px (scripted check of `scrollWidth`); tables collapse to labelled cards. Only the product-detail text overrun above.
- Two layout patterns coexist: hero band + cards (list/ops pages) and white "sheet" with a plain title row (record/form pages). The mockup uses the sheet only for a record file (claim). Trial balance, new journal and the documents subject list should use the hero pattern.
- Many card titles (journals, intake exceptions, period close, documents, fiscal) lack the coloured dot used on billing/reports cards.
- Link-list cards (Finance operations, Detailed views) are the most plain-looking blocks.

## Counts (24 pages reviewed)
matches 10, minor 11, off 3 (all three "off" are driven mainly by 403/422 API errors, not styling).
