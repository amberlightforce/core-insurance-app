# SL-UX visual review: mockup v3 against the app (D-USR-11)

Each PNG puts **mockup v3** (`core-insurance-prds/design-guide/mockups/aegean-screens-v3.html`) on the left and
**the app** on the right, at the same viewport. The app shots come from the production build (`vite build` +
`vite preview`) against an isolated local stack seeded with `infra/local/seed-demo.py --claims`, signed in as
the dev users, Europe/Athens time, reduced motion (so the drifting blooms are captured still).

| Screen | Light 1440 | Dark 1440 | Phone 390 |
| --- | --- | --- | --- |
| Home (underwriter): canopy, quick actions, work-left cards, KPI tiles, latest invoices | [home-light](home-light.png) | [home-dark](home-dark.png) | [home-390](home-390.png) |
| Home (claims manager): pending approvals, «Νέα δήλωση ζημίας» | [home-claimsmgr-light](home-claimsmgr-light.png) | | |
| List: party search (landing canopy + results card) | [list-parties-light](list-parties-light.png) | [list-parties-dark](list-parties-dark.png) | [list-parties-390](list-parties-390.png) |
| List: billing landing (all invoices) | [list-billing-light](list-billing-light.png) | [list-billing-dark](list-billing-dark.png) | |
| Record: policy (header strip, facts, as-of, cards, tables) | [record-policy-light](record-policy-light.png) | [record-policy-dark](record-policy-dark.png) | [record-policy-390](record-policy-390.png) |
| Record: invoice | [record-invoice-light](record-invoice-light.png) | [record-invoice-dark](record-invoice-dark.png) | |
| Record: party | [record-party-light](record-party-light.png) | | |
| Quote wizard (record sheet, side stepper panel, summary) | [wizard-light](wizard-light.png) | [wizard-dark](wizard-dark.png) | [wizard-390](wizard-390.png) |
| Sign-in (centred card on the ambient canvas) | [signin-light](signin-light.png) | [signin-dark](signin-dark.png) | [signin-390](signin-390.png) |
| Avatar menu (theme, density, language, help, account) | [avatar-menu-light](avatar-menu-light.png) | | |

The mockup has no list-of-parties, wizard or sign-in screen: those rows pair the app screen with the mockup screen
whose composition it follows (work-views queue, claim file, home canopy).

All figures in the app shots are real API data (3 invoices, 1 policy in the local seed); nothing is invented, so the
app shows fewer rows and no trends/deltas where the mockup shows sample numbers.

## Round 2 (D-USR-13)

### Focus states — `focus/`

Keyboard-focused controls, cropped at 2× (`before-*` = previous look, `after-*` = now), light and dark:
`textfield`, `select`, `datepicker`, `identifier`, `search`, `button`, `radio`.
Fields now draw **one shape** on their own border box: a solid 2 px edge in `border.focus` and a soft 3 px halo
(the mockup's `.input:focus`), so the ring follows the field's radius exactly, with no pale band detached from a thin
line and no layout shift. The focused date segment is a soft tint instead of a solid blue block. Buttons, radios
and checkboxes keep the mockup's 2 px ring with a 2 px gap, which follows their radius.

### Claims — `claims/` (mockup left, app right)

| Screen | Light 1440 | Dark 1440 | Phone 390 |
| --- | --- | --- | --- |
| Claim file «Φάκελος ζημίας» (open claim) | [claim-file-light](claims/claim-file-light.png) | [claim-file-dark](claims/claim-file-dark.png) | [claim-file-390](claims/claim-file-390.png) |
| Claim file, closed and paid | [claim-file-closed-light](claims/claim-file-closed-light.png) | [claim-file-closed-dark](claims/claim-file-closed-dark.png) | |
| Approvals inbox (queue rows) | [claims-inbox-light](claims/claims-inbox-light.png) | [claims-inbox-dark](claims/claims-inbox-dark.png) | [claims-inbox-390](claims/claims-inbox-390.png) |
| FNOL | [claims-fnol-light](claims/claims-fnol-light.png) | [claims-fnol-dark](claims/claims-fnol-dark.png) | [claims-fnol-390](claims/claims-fnol-390.png) |
| Claims landing | [claims-home-light](claims/claims-home-light.png) | [claims-home-dark](claims/claims-home-dark.png) | |

The stage strip is derived from recorded facts only: Αναγγελία (the claim exists), Εκτίμηση (an exposure is open),
Προσφορά (a reserve was set), Πληρωμή (a payment was issued), Κλείσιμο (closed). «Επαφή» is not recorded by the
system yet, so it is drawn dashed and announced as «δεν καταγράφεται ακόμη». The local seed has no pending approval,
so the inbox shows its empty state.
