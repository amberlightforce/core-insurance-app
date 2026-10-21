import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { call, eventually, signIn, type Json } from './support/api.js';
import { importProducts, issuePolicy, serverNow, type Issued } from './support/servicing.js';
import { advanceClock } from './support/time.js';
import { athensDate } from './support/withdrawal.js';
import { go, signInAs } from './support/ui.js';

// E2E-07 through the delinquency screens in a real browser (Greek UI, role-based locators; SLICE-PLAN-6, D-SL6-01/03/04): three unpaid
// policies go overdue, the sweep opens a case each and DOC renders the registered-post notice. Only the provider callback DELIVERED starts
// the clock: case A is delivered (clock running), case R is returned (no clock), case U has no proof (no clock). The billing clerk then works
// the cases on /billing/delinquency: list and status filter, the countdown / no-clock banners, record a contact, place and lift a
// vulnerable-customer hold; a non-billing role gets the no-permission state. The cancellation after the elapse is covered by the API-level
// spec. Setup (policies, clock, sweep, provider callbacks) goes through the API as there. Runs through tests/e2e/run-e2e07.sh.
// TODO(hardening): the countdown (daysRemaining) uses the browser's clock, not the server's shiftable dev clock, so only its presence is asserted.

const BILLING = 'Dev Billing Clerk (synthetic)';
const UNDERWRITER = 'Dev Underwriter (synthetic)';

test.use({
  actionTimeout: 15_000,
  contextOptions: { reducedMotion: process.env['E2E_REDUCED_MOTION'] ? 'reduce' : 'no-preference' },
});

async function sweep(request: APIRequestContext, billing: string): Promise<{ status: number; body: Json; text: string }> {
  const response = await request.fetch('/dev/bil/delinquency/sweep', { method: 'POST', headers: { authorization: `Bearer ${billing}` } });
  const text = await response.text();
  return { status: response.status(), body: text ? (JSON.parse(text) as Json) : {}, text };
}

async function caseOf(request: APIRequestContext, billing: string, policy: Issued): Promise<Json | undefined> {
  const list = await call(request, billing, 'GET', `/api/bil/v1/delinquencies?billingAccountId=${policy.accountId}`);
  expect(list.status, list.text).toBe(200);
  return (list.body['items'] as Json[]).map((i) => i['process'] as Json).find((p) => p['termId'] === policy.termId);
}

async function providerEvent(request: APIRequestContext, admin: string, documentId: string, eventType: 'DELIVERED' | 'RETURNED', proofDate?: string): Promise<void> {
  const doc = await call(request, admin, 'GET', `/api/doc/v1/documents/${documentId}`);
  expect(doc.status, doc.text).toBe(200);
  const sent = await call(request, admin, 'POST', '/api/doc/v1/provider-events/stub-post', {
    providerEventId: randomUUID(), deliveryId: doc.body['deliveries'][0]['deliveryId'], eventType, proofDate, proofReference: 'REG-E2E07-UI',
  });
  expect(sent.status, sent.text).toBe(202);
}

test('E2E-07 delinquency screens: list and filter, running clock, returned letter, unproven delivery, contact, hold and release, role guard', async ({ page, request }) => {
  test.setTimeout(900_000);
  const admin = await signIn(request, 'admin');
  const billing = await signIn(request, 'billing');
  await importProducts(request);

  // ---- Setup (API): three unpaid policies, overdue past the grace period, one case each with its notice rendered.
  const running = await issuePolicy(request, 'e2e07ui-run', { pay: false, plate: 'IKX-0711' });
  const returned = await issuePolicy(request, 'e2e07ui-ret', { pay: false, plate: 'IKX-0712' });
  const unproven = await issuePolicy(request, 'e2e07ui-unp', { pay: false, plate: 'IKX-0713' });
  await advanceClock(request, { days: 12 });
  const swept = await sweep(request, billing);
  expect(swept.status, swept.text).toBe(200);
  const cases = new Map<string, Json>();
  for (const policy of [running, returned, unproven]) {
    cases.set(policy.policyId, await eventually(`the delinquency case of ${policy.policyNumber}`, async () => {
      const found = await caseOf(request, billing, policy);
      return found?.['noticeDocumentId'] ? found : undefined;
    }));
  }
  const idOf = (policy: Issued): string => cases.get(policy.policyId)!['delinquencyId'] as string;
  const noticeOf = (policy: Issued): string => cases.get(policy.policyId)!['noticeDocumentId'] as string;
  await providerEvent(request, admin, noticeOf(running), 'DELIVERED', athensDate(await serverNow(request)));
  await providerEvent(request, admin, noticeOf(returned), 'RETURNED');
  await eventually('the clock of the delivered notice', async () => ((await caseOf(request, billing, running))?.['state'] === 'CLOCK_RUNNING' ? true : undefined));
  await eventually('the returned notice to be recorded', async () => ((await caseOf(request, billing, returned))?.['state'] === 'NOTICE_RETURNED' ? true : undefined));

  const prefix = (policy: Issued): string => policy.accountId.slice(0, 8);
  const grid = page.getByRole('grid', { name: 'Υποθέσεις καθυστέρησης' });

  // ---- 1. The billing clerk sees the three cases, with the clock column telling running from not running.
  await signInAs(page, BILLING);
  await go(page, '/billing/delinquency');
  await expect(page.getByRole('heading', { level: 1, name: 'Καθυστερήσεις' })).toBeVisible();
  for (const policy of [running, returned, unproven]) {
    await expect(grid.getByRole('row').filter({ hasText: prefix(policy) })).toBeVisible();
  }
  await expect(grid.getByRole('row').filter({ hasText: prefix(running) })).toContainText('Τρέχει χρονόμετρο');
  await expect(grid.getByRole('row').filter({ hasText: prefix(returned) })).toContainText('Χωρίς χρονόμετρο');
  await expect(grid.getByRole('row').filter({ hasText: prefix(unproven) })).toContainText('Χωρίς χρονόμετρο');

  // The status filter narrows the list to the returned letter.
  await page.getByRole('button', { name: /^Κατάσταση/ }).first().click();
  await page.getByRole('option', { name: 'Επεστράφη η ειδοποίηση' }).click();
  await expect(grid.getByRole('row').filter({ hasText: prefix(returned) })).toBeVisible();
  await expect(grid.getByRole('row').filter({ hasText: prefix(running) })).toHaveCount(0);

  // ---- 2. Case A (delivered): running clock, notification date, then a contact and a hold.
  await go(page, '/billing/delinquency');
  await grid.getByRole('row').filter({ hasText: prefix(running) }).dblclick();
  await expect(page).toHaveURL(new RegExp(`/billing/delinquency/${idOf(running)}$`));
  await expect(page.getByRole('heading', { level: 1, name: 'Υπόθεση καθυστέρησης' })).toBeVisible();
  await expect(page.getByText('Τρέχει χρονόμετρο', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/απομένει|απομένουν/).first()).toBeVisible();
  await expect(page.getByText(/^Λήξη:/)).toBeVisible();
  await expect(page.getByText('Ημερομηνία γνωστοποίησης')).toBeVisible();
  const timeline = page.getByRole('list', { name: 'Χρονολόγιο' });
  await expect(timeline).toContainText('Επίδοση ειδοποίησης');

  // A contact needs an outcome; then it appears in the timeline.
  await page.getByRole('button', { name: 'Καταγραφή', exact: true }).click();
  await expect(page.getByText('Υποχρεωτικό πεδίο').first()).toBeVisible();
  await page.getByRole('textbox', { name: /^Αποτέλεσμα/ }).fill('Ο πελάτης θα πληρώσει την Παρασκευή');
  await page.getByRole('button', { name: 'Καταγραφή', exact: true }).click();
  await expect(timeline).toContainText('Ο πελάτης θα πληρώσει την Παρασκευή');

  // A hold (vulnerable customer) needs a reason; the case is then ON_HOLD, and lifting it offers the hold again.
  await page.getByRole('button', { name: 'Θέση σε αναμονή', exact: true }).click();
  await expect(page.getByText('Υποχρεωτικό πεδίο').first()).toBeVisible();
  await page.getByRole('textbox', { name: /^Αιτιολόγηση/ }).fill('Ευάλωτος πελάτης');
  await page.getByRole('button', { name: 'Θέση σε αναμονή', exact: true }).click();
  await expect(page.getByText('Σε αναμονή', { exact: true }).first()).toBeVisible();
  expect((await caseOf(request, billing, running))!['state']).toBe('ON_HOLD');
  await page.getByRole('button', { name: 'Άρση αναμονής', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Θέση σε αναμονή', exact: true })).toBeVisible();
  expect((await caseOf(request, billing, running))!['state']).not.toBe('ON_HOLD');

  // ---- 3. Case R (returned letter) and case U (no proof): no clock, each says why.
  await go(page, `/billing/delinquency/${idOf(returned)}`);
  await expect(page.getByText('Επεστράφη η ειδοποίηση', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Δεν τρέχει χρονόμετρο')).toBeVisible();
  await expect(page.getByText('Η συστημένη επιστολή επεστράφη. Δεν ξεκινά χρονόμετρο.')).toBeVisible();
  await go(page, `/billing/delinquency/${idOf(unproven)}`);
  await expect(page.getByText('Αναμονή ειδοποίησης', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Χωρίς απόδειξη επίδοσης δεν υπάρχει ημερομηνία γνωστοποίησης.')).toBeVisible();
  expect((await caseOf(request, billing, unproven))!['clockInstanceId'] ?? null, 'rendered or sent is not proof: no clock').toBeNull();

  // ---- 4. Role guard: an underwriter has no business in the billing back office.
  await signInAs(page, UNDERWRITER);
  await go(page, '/billing/delinquency');
  await expect(page.getByText('Δεν έχετε δικαίωμα')).toBeVisible();
  await expect(grid).toHaveCount(0);
});
