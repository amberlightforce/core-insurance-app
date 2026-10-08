import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { call, signIn, type Json } from './support/api.js';

// E2E-02a through the claims screens in a real browser (Greek UI, role-based locators; SLICE-PLAN-2, D-SL2-01..13):
// FNOL -> claim file -> own-damage exposure -> payee account (IBAN only in a request body) -> reserve within authority ->
// reserve above it (PENDING_APPROVAL, approval checklist) -> the claims manager approves in the inbox (a second browser
// context) -> FINAL payment with the system-added release -> approval -> CLEARED -> money card -> close.
// Runs against the stack of tests/e2e/run-e2e02.sh (fresh database) or any stack with the dev sign-in.

const PRODUCT = 'MOTOR-GR';
const SEED = process.env['E2E_PRODUCT_SEED'] ?? resolve(import.meta.dirname, '../../../src/CoreIns.Modules.Product/Seed/motor-gr.product.json');
const IBAN = 'GR1601101250000000012300695';
const HANDLER = 'Dev Claims Handler (synthetic)';
const MANAGER = 'Dev Claims Manager (synthetic)';

async function alive(page: Page): Promise<void> {
  const answer = await Promise.race([page.evaluate(() => 'alive'), new Promise<string>((done) => setTimeout(() => done('frozen'), 5000))]);
  expect(answer, 'the page answers within 5 s (no render loop)').toBe('alive');
}

async function go(page: Page, url: string): Promise<void> {
  await page.goto(url);
  await alive(page);
}

/** Dev sign-in page: pick the user by the exact display name with the mouse, sign in. */
async function signInAs(page: Page, name: string): Promise<void> {
  await go(page, '/dev/sign-in');
  const signOut = page.getByRole('button', { name: 'Αποσύνδεση' });
  const picker = page.getByRole('button', { name: /Χρήστης/ });
  await expect(signOut.or(picker)).toBeVisible();
  if (await signOut.isVisible()) await signOut.click();
  await picker.click();
  await page.getByRole('option', { name, exact: true }).click();
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Συνδεθήκατε ως' })).toBeVisible();
}

/** A select of the design system is a button named after its label; the options open in a listbox. */
async function pick(page: Page, label: RegExp | string, option: RegExp | string): Promise<void> {
  await page.getByRole('button', { name: label }).click();
  await page.getByRole('option', { name: option }).click();
}

/** Money text of the el-GR UI: «6.200,00 €», with a no-break space before the euro sign. */
const euro = (grouped: string): RegExp => new RegExp(`${grouped.replaceAll('.', '\\.')}\\s?€`);

test.beforeAll(async ({ request }) => {
  test.setTimeout(120_000);
  const admin = await signIn(request, 'admin');
  const imported = await call(request, admin, 'POST', '/api/pfc/v1/product-versions/import', { definition: JSON.parse(readFileSync(SEED, 'utf8')) as Json, lock: true });
  expect(imported.status, imported.text).toBeLessThan(300);
});

test.use({
  actionTimeout: 15_000,
  contextOptions: { reducedMotion: process.env['E2E_REDUCED_MOTION'] ? 'reduce' : 'no-preference' },
});

/** A second browser context (own session storage) for the claims manager. */
async function managerPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:5000', locale: 'el-GR', timezoneId: 'Europe/Athens' });
  const page = await context.newPage();
  page.setDefaultTimeout(15_000);
  await signInAs(page, MANAGER);
  return page;
}

/** The manager decides every request waiting in the inbox (a set may need one approval per authority and cost type). */
async function approveAllInInbox(page: Page, comment: string): Promise<number> {
  let decided = 0;
  for (let round = 0; round < 6; round += 1) {
    await go(page, '/claims/approvals');
    await expect(page.getByRole('heading', { level: 1, name: 'Εγκρίσεις' })).toBeVisible();
    const empty = page.getByText('Δεν υπάρχουν εκκρεμείς εγκρίσεις');
    const rows = page.getByRole('grid', { name: 'Εκκρεμείς εγκρίσεις' }).getByRole('row').filter({ hasText: /Αλλαγή αποθεματικού|Πληρωμή|Σύνολο/ });
    await expect(empty.or(rows.first())).toBeVisible({ timeout: 30_000 });
    if (await empty.isVisible()) break;
    await rows.first().dblclick();
    await expect(page).toHaveURL(/\/claims\/approvals\/[0-9a-f-]{36}$/);
    await alive(page);
    await page.getByRole('textbox', { name: /^Σχόλιο/ }).fill(comment);
    await page.getByRole('button', { name: 'Έγκριση', exact: true }).click();
    await expect(page.getByText('Το αίτημα εγκρίθηκε').first()).toBeVisible();
    decided += 1;
  }
  return decided;
}

test('E2E-02a through the claims screens', async ({ page, browser, request }) => {
  test.setTimeout(600_000);

  // The IBAN may travel only in a request body, never in a URL (R-38).
  const urls: string[] = [];
  page.on('request', (r) => urls.push(r.url()));

  // ---- 1. Setup by API: party and a bound motor policy with OWN-DAMAGE whose term starts on a minute boundary a few
  // seconds from now (the FNOL form takes the loss time to the minute, so the loss is dated at that minute).
  const underwriter = await signIn(request, 'underwriter');
  const created = await call(request, underwriter, 'POST', '/api/pty/v1/parties', {
    partyType: 'PERSON',
    person: { givenNames: 'Νίκος', familyName: 'Αλεξίου', fatherName: 'Γεώργιος', birthDate: '1975-02-11' },
    identifiers: [],
    addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Λεωφ. Κηφισίας', number: '124', postcode: '11526', locality: 'Αθήνα' }],
    contactPoints: [{ type: 'EMAIL', value: 'e2e02.ui@example.org', primary: true }],
    reason: 'NEW_CUSTOMER',
  });
  expect(created.status, created.text).toBe(201);
  const party = created.body['party']['partyId'] as string;

  const start = new Date(Math.ceil((Date.now() + 25_000) / 60_000) * 60_000);
  const effectiveAt = start.toISOString().replace('.000Z', 'Z');
  const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Europe/Athens', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(start);
  const lossTime = parts; // HH:mm in Athens
  const submission = await call(request, underwriter, 'POST', '/api/pol/v1/submissions', { policyholderPartyId: party, product: PRODUCT, channel: 'STAFF', effectiveAt, quoteType: 'FULL' });
  expect(submission.status, submission.text).toBe(201);
  const jobId = submission.body['jobId'] as string;
  const first = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 0,
    instructions: [
      { op: 'SET_VEHICLE', vehicle: { plate: 'ikx-1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: '15000.00', currency: 'EUR' } } },
      { op: 'SET_ANSWERS', questionSet: { questionSetCode: 'MOTOR-RISK', questionSetVersion: '1', answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' } } },
    ],
  });
  expect(first.status, first.text).toBe(200);
  const vehicle = first.body['riskTree']['vehicles'][0]['locator'] as string;
  const second = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 1,
    instructions: [
      { op: 'SET_DRIVER', driver: { partyId: party, driverType: 'MAIN', yearFirstLicensed: 2008, vehicleLocator: vehicle, usagePercent: 100, claimsLast5Years: 0 } },
      { op: 'SET_COVERAGES', coverages: [
        { coverageCode: 'MTPL', elementLocator: vehicle, selected: true },
        { coverageCode: 'OWN-DAMAGE', elementLocator: vehicle, selected: true },
      ] },
    ],
  });
  expect(second.status, second.text).toBe(200);
  const quoted = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
  expect(quoted.status, quoted.text).toBe(200);
  const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(bind.status, bind.text).toBe(200);
  const policyNumber = bind.body['policyNumber'] as string;
  expect(policyNumber).toMatch(/^POL\d+$/);
  // The loss minute is the start minute: wait until it is in the past on the server too.
  while (Date.now() < start.getTime() + 4000) await new Promise((done) => setTimeout(done, 500));

  // ---- 2. FNOL as the claims handler.
  await signInAs(page, HANDLER);
  await go(page, '/claims/new');
  await expect(page.getByRole('heading', { level: 1, name: 'Αναγγελία ζημίας' })).toBeVisible();
  await page.getByRole('textbox', { name: /^Αριθμός ασφαλιστηρίου/ }).fill(policyNumber);
  await page.getByRole('button', { name: 'Αναζήτηση', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Σύνοψη ασφαλιστηρίου' }).or(page.getByText(policyNumber, { exact: true }).first())).toBeVisible();
  await page.getByRole('textbox', { name: /^Ώρα ζημίας/ }).fill(lossTime);
  await pick(page, /Αιτία ζημίας/, 'Σύγκρουση');
  await page.getByRole('textbox', { name: /^Τόπος ζημίας/ }).fill('Λεωφ. Κηφισίας 124, Αθήνα');
  await page.getByRole('textbox', { name: /^Περιγραφή/ }).fill('E2E-02a UI: σύγκρουση με σταθμευμένο όχημα (synthetic)');
  // The exposure is created from the claim file below, so the checkbox is cleared.
  await page.getByText('Δημιουργία έκθεσης ιδίων ζημιών για τον ασφαλισμένο').click(); // the input is visually hidden
  await expect(page.getByRole('checkbox', { name: 'Δημιουργία έκθεσης ιδίων ζημιών για τον ασφαλισμένο' })).not.toBeChecked();
  await page.getByRole('button', { name: 'Καταχώριση αναγγελίας' }).click();
  const createdBanner = page.getByText(/^Η ζημία \S+ δημιουργήθηκε$/).first();
  await expect(createdBanner).toBeVisible();
  const claimNumber = /^Η ζημία (\S+) δημιουργήθηκε$/.exec(await createdBanner.innerText())?.[1] as string;
  expect(claimNumber).toBeTruthy();
  // Coverage verified on the policy: no «coverage in question» warning and the own-damage indication is Covered.
  await expect(page.getByText('Η κάλυψη τίθεται υπό αμφισβήτηση')).toHaveCount(0);
  await expect(page.getByText('Καλύπτεται (ένδειξη)').first()).toBeVisible();

  // ---- 3. The claim file: own-damage exposure from the Overview tab.
  await page.getByRole('button', { name: 'Άνοιγμα ζημίας' }).click();
  await expect(page).toHaveURL(/\/claims\/[0-9a-f-]{36}$/);
  await alive(page);
  const claimUrl = page.url();
  await expect(page.getByText(claimNumber).first()).toBeVisible();
  await expect(page.getByRole('list', { name: 'Στάδια φακέλου' })).toBeVisible();
  await page.getByRole('form', { name: 'Νέα έκθεση' }).getByRole('button', { name: 'Δημιουργία έκθεσης' }).click();
  const exposures = page.getByRole('grid', { name: 'Εκθέσεις' });
  await expect(exposures.getByText('Ίδιες ζημιές')).toBeVisible();
  await expect(exposures.getByText('Καλύπτεται (ένδειξη)')).toBeVisible();

  // ---- 4. Financials tab: payee account, then the reserves.
  await page.getByRole('tab', { name: 'Οικονομικά' }).click();
  const payee = page.getByRole('form', { name: 'Τραπεζικός λογαριασμός δικαιούχου' });
  await payee.getByRole('textbox', { name: /^Δικαιούχος \(όνομα λογαριασμού\)/ }).fill('Νίκος Αλεξίου');
  await payee.getByRole('textbox', { name: /^IBAN/ }).fill(IBAN);
  await payee.getByRole('button', { name: 'Αποθήκευση λογαριασμού' }).click();
  const savedAccount = page.getByRole('status').filter({ hasText: 'IBAN' }).first();
  await expect(savedAccount).toContainText('****0695');
  await expect(savedAccount).not.toContainText('12300695');
  await expect(payee.getByRole('textbox', { name: /^IBAN/ })).toHaveValue('');
  await expect(page.getByText('12300695')).toHaveCount(0);

  const builder = page.getByRole('form', { name: 'Αλλαγή αποθεματικού ή πληρωμή' });
  const reserve = async (amount: string, reason: string) => {
    await builder.getByRole('textbox', { name: /^Ποσό αλλαγής αποθεματικού/ }).fill(amount);
    await pick(page, /Αιτιολογία αλλαγής/, reason);
    await builder.getByRole('button', { name: 'Υποβολή αποθεματικού' }).click();
  };

  // Reserve 1,200.00: inside the handler's authority -> APPROVED at once.
  await reserve('1200,00', 'Αρχική εκτίμηση');
  await expect(page.getByText('Το σύνολο εγκρίθηκε').first()).toBeVisible();
  const sets = page.getByRole('article', { name: /^Σύνολο / });
  await expect(sets.first()).toContainText('Εγκρίθηκε');

  // Reserve +5,300.00: the exposure reserve would be 6,500.00 -> PENDING_APPROVAL with a checklist.
  await reserve('5300,00', 'Αναθεωρημένη εκτίμηση');
  await expect(page.getByText('Το σύνολο αναμένει έγκριση').first()).toBeVisible();
  await expect(sets.first()).toContainText('Αναμένει έγκριση');
  const checklist = sets.first().getByRole('list', { name: 'Λίστα εγκρίσεων' });
  await expect(checklist).toBeVisible();
  await expect(sets.first().getByText(/^Εγκρίσεις: 0 από \d+$/)).toBeVisible();

  // ---- 5. Maker-checker: the handler cannot approve their own request.
  await checklist.getByRole('link', { name: 'Άνοιγμα αιτήματος' }).first().click();
  await expect(page).toHaveURL(/\/claims\/approvals\/[0-9a-f-]{36}$/);
  await alive(page);
  await page.getByRole('button', { name: 'Έγκριση', exact: true }).click();
  await expect(page.getByText('Δεν μπορείτε να εγκρίνετε αίτημα που υποβάλατε εσείς').first()).toBeVisible();
  await expect(page.getByText('Το αίτημα εγκρίθηκε')).toHaveCount(0);
  await go(page, `${claimUrl}?tab=financials`);

  // ---- 6. The claims manager approves in the inbox, in a second browser context.
  const manager = await managerPage(browser);
  const decided = await approveAllInInbox(manager, 'E2E-02a: εγκρίνεται (synthetic)');
  expect(decided, 'the manager decided at least the reserve request').toBeGreaterThanOrEqual(1);

  // The first context follows: the set page polls and the set becomes APPROVED.
  await expect(sets.first()).toContainText('Εγκρίθηκε', { timeout: 90_000 });
  await expect(sets.first()).not.toContainText('Αναμένει έγκριση');
  // The balances moved with the applied decision: 1,200.00 + 5,300.00 reserved.
  await expect(page.getByRole('grid', { name: 'Υπόλοιπα ανά γραμμή αποθεματικού' }).getByText(euro('6.500,00')).first()).toBeVisible({ timeout: 30_000 });

  // ---- 7. FINAL payment 6,200.00 to the captured account: the preview shows the system-added release of -300.00.
  await builder.getByRole('radio', { name: 'Πληρωμή', exact: true }).check();
  await builder.getByRole('textbox', { name: /^Ποσό πληρωμής/ }).fill('6200,00');
  await pick(page, /Λογαριασμός δικαιούχου/, /0695/);
  await builder.getByRole('radio', { name: 'Τελική', exact: true }).check();
  await builder.getByRole('button', { name: 'Προεπισκόπηση' }).click();
  const previewLines = page.getByRole('grid', { name: /^Συναλλαγές της προεπισκόπησης/ });
  await expect(previewLines).toBeVisible();
  await expect(previewLines).toContainText('Προστέθηκε από το σύστημα: αποδέσμευση υπολοίπου');
  await expect(previewLines).toContainText(/[-−]\s?300,00/);
  await builder.getByRole('button', { name: 'Υποβολή πληρωμής' }).click();
  await expect(page.getByText('Το σύνολο αναμένει έγκριση').first()).toBeVisible();
  await expect(sets.first()).toContainText('Αναμένει έγκριση');

  // The manager approves the payment; BIL pays it to CLEARED.
  expect(await approveAllInInbox(manager, 'E2E-02a: εγκρίνεται η πληρωμή (synthetic)')).toBeGreaterThanOrEqual(1);
  const payments = page.getByRole('grid', { name: 'Πληρωμές' });
  await expect
    .poll(
      async () => {
        await go(page, `${claimUrl}?tab=financials`);
        return payments
          .getByText('Εκκαθαρίστηκε')
          .waitFor({ timeout: 8000 })
          .then(() => 1)
          .catch(() => 0);
      },
      { timeout: 120_000, intervals: [3000] },
    )
    .toBe(1);
  await expect(payments).toContainText(euro('6.200,00'));
  await expect(payments).toContainText('0695');

  // ---- 8. The money card (Overview): paid 6.200,00 €, open reserve 0,00 €.
  await page.getByRole('tab', { name: 'Επισκόπηση' }).click();
  const money = page.getByRole('region', { name: 'Οικονομικά φακέλου' }).or(page.locator('section').filter({ has: page.getByRole('heading', { name: 'Οικονομικά φακέλου' }) }));
  await expect(money.first()).toContainText(/Πληρωθέντα\s+6\.200,00\s?€\s*\+\s*Απόθεμα\s+0,00\s?€/);

  // ---- 9. Close as COMPLETED; the stage strip shows Κλείσιμο done.
  const closeForm = page.getByRole('form', { name: 'Κλείσιμο ζημίας' });
  await closeForm.getByRole('button', { name: /Αποτέλεσμα/ }).click();
  await page.getByRole('option', { name: 'Ολοκληρώθηκε' }).click();
  await closeForm.getByRole('button', { name: 'Κλείσιμο ζημίας' }).click();
  await expect(page.getByText('Η ζημία είναι κλειστή').first()).toBeVisible();
  const closing = page.getByRole('list', { name: 'Στάδια φακέλου' }).getByRole('listitem').filter({ hasText: 'Κλείσιμο' });
  await expect(closing).toContainText('ολοκληρώθηκε');
  await expect(page.getByRole('form', { name: 'Κλείσιμο ζημίας' })).toHaveCount(0);

  // ---- 10. The IBAN never appeared in a request URL.
  expect(urls.length).toBeGreaterThan(20);
  for (const url of urls) {
    expect(decodeURIComponent(url).replace(/\s/g, '')).not.toContain(IBAN);
    expect(url).not.toContain('12300695');
  }
  await manager.context().close();
});
