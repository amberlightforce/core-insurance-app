import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { call, eventually, signIn, staysTrue, type Json } from './support/api.js';
import { importProducts, issuePolicy, serverNow, type Issued } from './support/servicing.js';
import { advanceClock } from './support/time.js';
import { addDays, athensDate } from './support/withdrawal.js';

// E2E-07 non-payment cancellation (SLICE-PLAN-6 E2E-07, D-SL6-01/03/04): unpaid policies go overdue past the grace period, the sweep opens
// a BIL delinquency case and DOC renders DT-NONPAY-NOTICE for registered post. Rendering is no proof: only the provider callback
// DELIVERED with a proof date starts the BIL_NONPAY_NOTICE clock (due = notification + 1 month, Europe/Athens). Variants on the same
// clock: (main) elapse -> cancellation request -> POL cancels, notice document; (a) cure before the elapse -> nothing; (c) returned
// letter -> no clock; unproven delivery -> no clock; (b) hold (vulnerable-customer path; the PTY flag is not served yet) -> clock paused,
// no cancellation. Runs through tests/e2e/run-e2e07.sh (worker role: CMP clock sweeper and POL scheduled-cancel sweeper).

/** The overdue sweep (dev-only endpoint). No Idempotency-Key: the sweep is a scheduled job, and one key would be reused by the DOC requests of every case it opens. */
async function sweep(request: APIRequestContext, billing: string): Promise<{ status: number; body: Json; text: string }> {
  const response = await request.fetch('/dev/bil/delinquency/sweep', { method: 'POST', headers: { authorization: `Bearer ${billing}` } });
  const text = await response.text();
  return { status: response.status(), body: text ? (JSON.parse(text) as Json) : {}, text };
}

const processOf = (item: Json): Json => item['process'] as Json;

async function caseOf(request: APIRequestContext, billing: string, policy: Issued): Promise<Json | undefined> {
  const list = await call(request, billing, 'GET', `/api/bil/v1/delinquencies?billingAccountId=${policy.accountId}`);
  expect(list.status, list.text).toBe(200);
  const items = (list.body['items'] as Json[]).map(processOf);
  return items.find((p) => p['termId'] === policy.termId);
}

const termState = async (request: APIRequestContext, token: string, policy: Issued): Promise<string> =>
  (await call(request, token, 'GET', `/api/pol/v1/terms/${policy.termId}`)).body['term']['state'] as string;

/** The provider callback of the stub registered-post service (doc.ProviderEvent.receive, Platform.Admin). */
async function providerEvent(request: APIRequestContext, admin: string, documentId: string, eventType: 'DELIVERED' | 'RETURNED', proofDate?: string): Promise<Json> {
  const doc = await call(request, admin, 'GET', `/api/doc/v1/documents/${documentId}`);
  expect(doc.status, doc.text).toBe(200);
  expect(doc.body['medium'], 'D-SL6-03: registered post only').toBe('REGISTERED_POST');
  const deliveryId = doc.body['deliveries'][0]['deliveryId'] as string;
  const sent = await call(request, admin, 'POST', '/api/doc/v1/provider-events/stub-post', {
    providerEventId: randomUUID(), deliveryId, eventType, proofDate, proofReference: 'REG-E2E07',
  });
  expect(sent.status, sent.text).toBe(202);
  if (eventType === 'DELIVERED') expect(sent.body['outcome'], sent.text).toBe('APPLIED');
  return sent.body;
}

test('E2E-07 non-payment: delivered notice starts the clock, elapse cancels; cure, returned letter, unproven delivery and hold cancel nothing', async ({ request }) => {
  test.setTimeout(1_500_000);
  const admin = await signIn(request, 'admin');
  const billing = await signIn(request, 'billing');
  const underwriter = await signIn(request, 'underwriter');
  await importProducts(request);

  // ---- 1. Five policies whose single instalment is never paid.
  const main = await issuePolicy(request, 'e2e07-main', { pay: false, plate: 'IKX-0701' });
  const cure = await issuePolicy(request, 'e2e07-cure', { pay: false, plate: 'IKX-0702' });
  const returned = await issuePolicy(request, 'e2e07-returned', { pay: false, plate: 'IKX-0703' });
  const unproven = await issuePolicy(request, 'e2e07-unproven', { pay: false, plate: 'IKX-0704' });
  const hold = await issuePolicy(request, 'e2e07-hold', { pay: false, plate: 'IKX-0705' });
  const all = [main, cure, returned, unproven, hold];

  // ---- 2. Overdue past the grace period (illustrative 5 days): the sweep opens a case per term and requests the notice.
  await advanceClock(request, { days: 12 });
  const swept = await sweep(request, billing);
  expect(swept.status, swept.text).toBe(200);
  expect(swept.body['opened'], 'one case per unpaid term').toBeGreaterThanOrEqual(all.length);
  const cases = new Map<string, Json>();
  for (const policy of all) {
    const process = await eventually(`the delinquency case of ${policy.policyNumber}`, async () => {
      const found = await caseOf(request, billing, policy);
      return found?.['noticeDocumentId'] ? found : undefined;
    });
    expect(process['state'], 'no proof yet, no clock').toBe('NOTICE_REQUESTED');
    expect(process['clockInstanceId'] ?? null).toBeNull();
    cases.set(policy.policyId, process);
  }
  const idOf = (policy: Issued): string => cases.get(policy.policyId)!['delinquencyId'] as string;
  const noticeOf = (policy: Issued): string => cases.get(policy.policyId)!['noticeDocumentId'] as string;
  const notice0 = await call(request, admin, 'GET', `/api/doc/v1/documents/${noticeOf(main)}`);
  expect(JSON.stringify(notice0.body), 'the notice DT-NONPAY-NOTICE was rendered').toContain('DT-NONPAY-NOTICE');

  // A sweep again opens nothing new (one open case per term).
  const again = await sweep(request, billing);
  expect(again.body['opened']).toBe(0);

  // ---- 3. Provider callbacks: delivered (main, cure, hold), returned, and none (unproven).
  const notification = athensDate(await serverNow(request));
  for (const policy of [main, cure, hold]) {
    await providerEvent(request, admin, noticeOf(policy), 'DELIVERED', notification);
  }
  await providerEvent(request, admin, noticeOf(returned), 'RETURNED');

  const running = await eventually('the nonpay clock of the delivered notice', async () => {
    const p = await caseOf(request, billing, main);
    return p?.['state'] === 'CLOCK_RUNNING' ? p : undefined;
  });
  expect(String(running['notificationDate']).slice(0, 10), 'the notification date is the proof date').toBe(notification);
  expect(running['clockInstanceId']).toBeTruthy();
  const deadline = new Date(running['deadline'] as string);
  const deadlineDay = athensDate(deadline);
  // One calendar month later (28..31 days), counted to the end of that day: the deadline is that day or the start of the next.
  expect(deadlineDay >= addDays(notification, 28) && deadlineDay <= addDays(notification, 32), `deadline ${deadlineDay} is notification + 1 month (Athens)`).toBe(true);

  // Variant (c): the returned letter starts no clock and records a work request. Unproven delivery: still no clock.
  await eventually('the returned notice to be recorded', async () => {
    const p = await caseOf(request, billing, returned);
    return p?.['state'] === 'NOTICE_RETURNED' ? p : undefined;
  });
  expect((await caseOf(request, billing, returned))!['clockInstanceId'] ?? null, 'a returned letter starts no clock').toBeNull();
  const noProof = await caseOf(request, billing, unproven);
  expect(noProof!['state']).toBe('NOTICE_REQUESTED');
  expect(noProof!['clockInstanceId'] ?? null, 'rendered or sent is not proof: no clock').toBeNull();

  // Variant (b): a hold pauses the clock (the vulnerable-customer exception path).
  await eventually('the delivered notice of the held case', async () => {
    const p = await caseOf(request, billing, hold);
    return p?.['state'] === 'CLOCK_RUNNING' ? p : undefined;
  });
  const held = await call(request, billing, 'POST', '/api/bil/v1/delinquencies/release-hold', { data: { delinquencyId: idOf(hold), reason: 'Vulnerable customer' } });
  expect(held.status, held.text).toBeLessThan(300);
  expect((await caseOf(request, billing, hold))!['state']).toBe('ON_HOLD');

  // Variant (a): the cure before the elapse stops the clock.
  await eventually('the delivered notice of the cured case', async () => {
    const p = await caseOf(request, billing, cure);
    return p?.['state'] === 'CLOCK_RUNNING' ? p : undefined;
  });
  const invoice = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${cure.invoiceId}`)).body['invoice'] as Json;
  const paid = await call(request, billing, 'POST', '/api/bil/v1/payments/take', { billingAccountId: cure.accountId, amount: invoice['total'], method: 'BANK_TRANSFER', invoiceId: cure.invoiceId });
  expect(paid.status, paid.text).toBe(201);
  const cured = await eventually('the cured case to resolve', async () => {
    const p = await caseOf(request, billing, cure);
    return p?.['state'] === 'RESOLVED' ? p : undefined;
  });
  expect(cured['outcome']).toBe('PAID');

  // Nothing is cancelled before the deadline.
  expect(await termState(request, underwriter, main)).toBe('IN_FORCE');

  // ---- 4. The waiting period elapses (just past the deadline): CMP ClockElapsed -> BIL requests the cancellation.
  const seconds = Math.ceil((deadline.getTime() - (await serverNow(request)).getTime()) / 1000) + 30;
  expect(seconds).toBeGreaterThan(0);
  await advanceClock(request, { seconds });
  const requested = await eventually('the cancellation request after the clock elapsed', async () => {
    const p = await caseOf(request, billing, main);
    return p?.['state'] === 'CANCELLATION_REQUESTED' ? p : undefined;
  });
  expect(requested['cancellationRequestId']).toBeTruthy();

  // POL cancels at the start of the day after the due date (REQ-POL-210): scheduled until then, then executed by the sweep.
  await advanceClock(request, { days: 2 });
  await eventually('the term to be cancelled for non-payment', async () => ((await termState(request, underwriter, main)) === 'CANCELLED' ? true : undefined));

  // The cancellation notice document of the policy.
  await eventually('the cancellation notice', async () => {
    const list = await call(request, billing, 'GET', `/api/doc/v1/documents/list-for-object?objectRef=${encodeURIComponent(`POL/Policy/${main.policyId}`)}`);
    return JSON.stringify(list.body).includes('DT-CANCELLATION-NOTICE') ? true : undefined;
  });

  // BIL credits the cancelled term (credit note against the invoice); the GR pack's CANCELLATION/NonPayment treatment row (D-SL6-15) makes it billable.
  await eventually('the credit note of the cancellation', async () => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${main.policyId}`);
    return (list.body['items'] as Json[]).some((i) => i['invoice']['kind'] === 'CREDIT_NOTE') ? true : undefined;
  });

  // ---- 5. The variants cancelled nothing.
  // The sweeper has just cancelled the main policy, so it is running; the variants must stay in force over a few more rounds (fails on the first violation).
  for (const policy of [cure, returned, unproven, hold]) {
    await staysTrue(`${policy.policyNumber} stays in force`, async () => (await termState(request, underwriter, policy)) === 'IN_FORCE', 3, 3000);
  }
  expect((await caseOf(request, billing, hold))!['state'], 'the hold keeps the case open and the clock paused').toBe('ON_HOLD');
  expect((await caseOf(request, billing, unproven))!['clockInstanceId'] ?? null).toBeNull();
});
