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
