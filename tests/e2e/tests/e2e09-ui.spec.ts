import { randomUUID } from 'node:crypto';
import { expect, test, type Browser, type Page } from '@playwright/test';
import { call, signIn } from './support/api.js';
import { importProducts, issuePolicy } from './support/servicing.js';
import { alive, go, signInAs } from './support/ui.js';

// E2E-09 DSAR through the compliance screens (SLICE-PLAN-6, D-SL6-07): the DPO opens an ACCESS case for a data subject who holds a paid policy
// and a DAT fact, verifies identity, starts the fan-out (15 modules, all DONE; WRK has a DSAR participant since NX-HD-03/05), exports the package (the
// case completes) and records the disclosure; then an ERASURE case where the creator is refused their own approval (four eyes), a second DPO
// approves in another browser context and the erase returns RESTRICTED for the regulated classes and ERASED for the DAT fact; the erase
// completes the case. Greek UI, labels from compliance dsar.json.
// Runs against the stack of tests/e2e/run-e2e09.sh (its e2e09 filter picks this file up).

const DPO = 'Dev Compliance Officer / DPO (synthetic)';
const DPO2 = 'Dev Super User — all roles (synthetic)'; // holds Staff.ComplianceOfficer and is not the case creator
const MAP = ['PTY', 'POL', 'BIL', 'CLM', 'UW', 'RI', 'DOC', 'CHN', 'WRK', 'PLT', 'FIN', 'RAT', 'DAT', 'MIG', 'CMP'];

test.use({ actionTimeout: 20_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

async function pick(page: Page, label: RegExp | string, option: RegExp | string): Promise<void> {
  await page.getByRole('button', { name: label }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}

/** A second browser context (own session storage) for the second DPO. */
async function secondDpoPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ baseURL: process.env['E2E_BASE_URL'] ?? 'http://localhost:5000', locale: 'el-GR', timezoneId: 'Europe/Athens' });
  const page = await context.newPage();
  page.setDefaultTimeout(20_000);
  await signInAs(page, DPO2);
  return page;
}

/** Opens a case from the console list and returns its id (the URL of the case page). */
async function openCase(page: Page, subject: string, type: string): Promise<string> {
  await go(page, '/compliance/dsar');
  await expect(page.getByRole('heading', { level: 1, name: 'Αιτήματα υποκειμένων δεδομένων' })).toBeVisible();
  await page.getByRole('textbox', { name: /^Υποκείμενο δεδομένων/ }).fill(subject);
  await pick(page, /Τύπος αιτήματος/, type);
  await page.getByRole('button', { name: 'Άνοιγμα αιτήματος', exact: true }).click();
  await expect(page).toHaveURL(/\/compliance\/dsar\/[0-9a-f-]{36}$/);
  await alive(page);
  await expect(page.getByText('Παρελήφθη').first()).toBeVisible();
  return page.url().split('/').pop()!;
}

/** Identity check and fan-out of a fresh case: all 15 modules answer (WRK included), no module is NOT_BUILT; the case stays in review until exported/erased. */
async function verifyAndFanOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Επαλήθευση ταυτότητας', exact: true }).click();
  await expect(page.getByText('Ταυτότητα επαληθεύτηκε').first()).toBeVisible();
  await page.getByRole('button', { name: 'Έναρξη αναζήτησης σε όλες τις ενότητες' }).click();
  await expect(page.getByText('Σε εξέταση').first()).toBeVisible();

  const tasks = page.getByRole('table', { name: 'Αναζήτηση ανά ενότητα' });
  await expect(tasks.getByRole('row')).toHaveCount(MAP.length + 1); // header row
  for (const module of MAP) await expect(tasks.getByRole('row').filter({ has: page.getByRole('cell', { name: module, exact: true }) })).toHaveCount(1);
  const wrk = tasks.getByRole('row').filter({ has: page.getByRole('cell', { name: 'WRK', exact: true }) });
  await expect(wrk).toContainText('Ολοκληρώθηκε');
  await expect(wrk).not.toContainText('Δεν έχει υλοποιηθεί');
  await expect(tasks.getByText('Ολοκληρώθηκε')).toHaveCount(MAP.length);
  await expect(tasks.getByText('Δεν έχει υλοποιηθεί')).toHaveCount(0);
  await expect(page.getByText('Δεν καλύπτονται όλες οι ενότητες')).toHaveCount(0);
  await expect(page.getByRole('list', { name: 'Εμπόδια ολοκλήρωσης' })).not.toContainText('NOT_BUILT');
}

test('E2E-09 UI: DSAR access with export and disclosure, then erasure with four eyes and retention classes', async ({ page, browser, request }) => {
  test.setTimeout(600_000);
  await importProducts(request);
  const admin = await signIn(request, 'admin');

  // ---- Setup by API: a data subject with a paid policy (party, policy, invoice) and a DAT subject fact.
  const policy = await issuePolicy(request, 'e2e09ui', { pay: true, plate: 'IKX-0919' });
  const subject = policy.partyId;
  const fact = await call(request, admin, 'POST', '/api/dat/v1/dev/subject-facts', { subjectToken: subject, category: 'CONTACT', key: 'preferred-channel', value: 'e-mail' });
  expect(fact.status, fact.text).toBe(200);
  expect(fact.body['written']).toBe(true);

  // ---- 1. The DPO opens an ACCESS case; a malformed subject id is refused client-side first.
  await signInAs(page, DPO);
  await go(page, '/compliance/dsar');
  await page.getByRole('textbox', { name: /^Υποκείμενο δεδομένων/ }).fill('δεν-είναι-uuid');
  await page.getByRole('button', { name: 'Άνοιγμα αιτήματος', exact: true }).click();
  await expect(page.getByText('Δώστε το id του μέρους (UUID).')).toBeVisible();
  const accessId = await openCase(page, subject, 'Πρόσβαση (άρθρο 15)');
  await expect(page.getByText(subject).first()).toBeVisible();

  // ---- 2. Identity, fan-out, per-module outcomes: the party, policy, invoice and DAT fact are found.
  await verifyAndFanOut(page);
  const outcomes = page.getByRole('table', { name: 'Αποτελέσματα' });
  await expect(outcomes.getByRole('row').filter({ hasText: 'SUBJECT_FACT' })).toHaveCount(1);

  // ---- 3. Export: the package is downloaded as JSON and, with every module answered, the case completes.
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: 'Σύνταξη και λήψη εξαγωγής' }).click(),
  ]);
  expect(download.suggestedFilename()).toBe(`dsar-${accessId}.json`);
  await expect(page.getByText('Η εξαγωγή συντάχθηκε').first()).toBeVisible();
  await expect(page.getByText('Ολοκληρώθηκε').first()).toBeVisible();
  await expect(page.getByRole('list', { name: 'Εμπόδια ολοκλήρωσης' })).toHaveCount(0);

  // ---- 4. The disclosure to the data subject is logged and listed.
  await page.getByRole('textbox', { name: 'Αποδέκτης', exact: true }).fill('Υποκείμενο δεδομένων');
  await page.getByRole('textbox', { name: 'Σκοπός', exact: true }).fill('GDPR Art 15 πρόσβαση');
  await page.getByRole('textbox', { name: 'Νομική βάση', exact: true }).fill('GDPR Art 15');
  await page.getByRole('textbox', { name: /^Κατηγορίες δεδομένων/ }).fill('PTY, POL, BIL, DAT');
  await page.getByRole('button', { name: 'Καταχώριση γνωστοποίησης', exact: true }).click();
  const disclosures = page.getByRole('table', { name: 'Αρχείο γνωστοποιήσεων' });
  await expect(disclosures.getByRole('row').filter({ hasText: 'Υποκείμενο δεδομένων' })).toHaveCount(1);
  await expect(disclosures).toContainText('GDPR Art 15 πρόσβαση');

  // The console lists the cases (the list is paged, so only its presence is asserted).
  await go(page, '/compliance/dsar');
  await expect(page.getByRole('grid', { name: 'Αιτήματα' }).or(page.getByRole('table', { name: 'Αιτήματα' }))).toBeVisible();

  // ---- 5. ERASURE: the creator is refused their own approval.
  const eraseId = await openCase(page, subject, 'Διαγραφή (άρθρο 17)');
  await verifyAndFanOut(page);
  await expect(page.getByText('Εσείς ανοίξατε αυτό το αίτημα')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Διαγραφή δεδομένων', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Έγκριση διαγραφής (δεύτερος DPO)' }).click();
  await page.getByRole('textbox', { name: /^Αιτιολογία/ }).fill('Αίτημα υποκειμένου (synthetic)');
  await page.getByRole('button', { name: 'Έγκριση', exact: true }).click();
  await expect(page.getByText('Δεν έχετε δικαίωμα για αυτή την ενέργεια.')).toBeVisible();
  await page.getByRole('button', { name: 'Άκυρο', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Διαγραφή δεδομένων', exact: true })).toBeDisabled();

  // ---- 6. The second DPO approves in another browser context; the first sees the approval after a reload.
  const second = await secondDpoPage(browser);
  await go(second, `/compliance/dsar/${eraseId}`);
  await expect(second.getByRole('button', { name: 'Διαγραφή δεδομένων', exact: true })).toBeDisabled();
  await expect(second.getByText('Εσείς ανοίξατε αυτό το αίτημα')).toHaveCount(0);
  await second.getByRole('button', { name: 'Έγκριση διαγραφής (δεύτερος DPO)' }).click();
  await second.getByRole('textbox', { name: /^Αιτιολογία/ }).fill('Εγκρίνεται από δεύτερο DPO (synthetic)');
  await second.getByRole('button', { name: 'Έγκριση', exact: true }).click();
  await expect(second.getByText('Η έγκριση καταχωρήθηκε').first()).toBeVisible();
  await expect(second.getByRole('button', { name: /^Εγκρίθηκε από/ })).toBeDisabled();

  // ---- 7. Erase (by the second DPO): regulated classes come back RESTRICTED with retention UNSET_F404, the DAT fact is ERASED.
  await second.getByRole('button', { name: 'Διαγραφή δεδομένων', exact: true }).click();
  await second.getByRole('button', { name: 'Διαγραφή τώρα', exact: true }).click();
  await expect(second.getByText('Η διαγραφή εκτελέστηκε').first()).toBeVisible();
  const erased = second.getByRole('table', { name: 'Αποτελέσματα' });
  await expect(erased.getByRole('row').filter({ hasText: 'Περιορίστηκε' }).first()).toBeVisible();
  await expect(erased.getByRole('row').filter({ hasText: 'Περιορίστηκε' }).first()).toContainText('UNSET_F404');
  await expect(erased.getByRole('row').filter({ hasText: 'DAT' }).filter({ hasText: 'Διαγράφηκε' })).not.toHaveCount(0);
  await expect(second.getByRole('list', { name: 'Εμπόδια ολοκλήρωσης' })).toHaveCount(0);
  await expect(second.getByText('Ολοκληρώθηκε').first()).toBeVisible();

  // The first DPO sees the same recorded outcome and the approval.
  await go(page, `/compliance/dsar/${eraseId}`);
  await expect(page.getByText(/^ERASE · /)).toBeVisible();
  await expect(page.getByRole('table', { name: 'Αποτελέσματα' }).getByRole('row').filter({ hasText: 'Περιορίστηκε' }).first()).toBeVisible();

  // The server agrees: the DAT subject key is gone and the policy record is kept (restricted, not erased).
  const exportAfter = await call(request, admin, 'POST', '/api/dat/v1/subject/export', { requestId: `e2e09-ui-${randomUUID()}`, subjectToken: subject });
  expect(exportAfter.status, exportAfter.text).toBe(200);
  expect(exportAfter.body['status']).toBe('ERASED');
  expect(exportAfter.body['keyDestroyed']).toBe(true);
  await second.context().close();
});
