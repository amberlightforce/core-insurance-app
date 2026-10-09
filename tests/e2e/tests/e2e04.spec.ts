import { expect, test } from '@playwright/test';
import { call, cents, eventually, signIn, sum, type Json } from './support/api.js';
import { advanceTo, importProducts, issuePolicy, iso, serverNow } from './support/servicing.js';

// E2E-04 thin renewal at API level (SLICE-PLAN-3 §3.2; D-SL3-10): the E2E-01 policy, the dev clock at expiry - 30 days (inside the
// 45-day renewal window), staff "Renew now" -> rated renewal job -> offer -> explicit acceptance (channel STAFF) -> term 2 bound
// as Scheduled -> BIL invoices term 2 -> fiscal document -> FIN journals. Runs through tests/e2e/run-e2e03.sh (Shiftable clock).

test('E2E-04 renewal: renew now inside the window, offer, explicit acceptance, term 2 invoiced', async ({ request }) => {
  test.setTimeout(300_000);
  await importProducts(request);
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  const finance = await signIn(request, 'finance');
  const policy = await issuePolicy(request, 'e2e04');

  await test.step('renew now outside the 45-day window is refused (REQ-POL-245)', async () => {
    const early = await call(request, underwriter, 'POST', '/api/pol/v1/renewals', { termId: policy.termId });
    expect(early.status, early.text).toBeGreaterThanOrEqual(400);
    expect(early.body['code'], early.text).toBe('POL-ERR-VALIDATION');
  });

  // The term runs 365 days; expiry - 30 days is day 335.
  const now = await advanceTo(request, policy, 335);
  expect(now.getTime()).toBeGreaterThan(policy.startAt.getTime() + 334 * 86_400_000);

  const created = await test.step('renew now creates the renewal job (REQ-POL-258, -246)', async () => {
    const result = await call(request, underwriter, 'POST', '/api/pol/v1/renewals', { termId: policy.termId, reason: 'e2e04 renew now' });
    expect(result.status, result.text).toBe(201);
    return result.body;
  });
  expect(created['expiringTermId']).toBe(policy.termId);
  expect(created['renewalProductCode']).toBe('MOTOR-GR');
  expect(created['renewalProductVersion'], 'the version resolved at the new term start (REQ-PFC-001)').toBe('1.1');
  const jobId = created['jobId'] as string;

  await test.step('a second renewal job for the same term is refused (D-SL3-11: one open renewal per term)', async () => {
    const again = await call(request, underwriter, 'POST', '/api/pol/v1/renewals', { termId: policy.termId });
    expect(again.status, again.text).toBeGreaterThanOrEqual(400);
  });

  const offered = await test.step('offer the renewal (REQ-POL-249, -250, -263)', async () => {
    const result = await call(request, underwriter, 'POST', '/api/pol/v1/renewals/offer', { jobId, termId: policy.termId });
    expect(result.status, result.text).toBe(200);
    return result.body;
  });
  expect(offered['referred'], 'the standard car is not referred (PRE_BIND rules)').toBeFalsy();
  expect(offered['offerVersion']).toBeGreaterThanOrEqual(1);
  const summary = offered['premiumSummary'] as Json;
  // Rating worksheet / term 2 annual premium next to term 1 (RENEWAL mode, no cap in the slice).
  const preview = offered['servicingPreview'] as Json | undefined;
  if (preview) {
    expect(cents(preview['annualBefore'])).toBe(policy.premium);
    expect(preview['provisional']).toBe(true);
  }
  expect(JSON.stringify(summary)).toBeTruthy();

  const accepted = await test.step('explicit acceptance by staff binds term 2 (REQ-POL-257, -253)', async () => {
    const result = await call(request, underwriter, 'POST', '/api/pol/v1/renewals/accept', {
      jobId, termId: policy.termId, channel: 'STAFF', acceptedAt: iso(await serverNow(request)), acceptanceEvidence: 'e2e04-phone-call',
    });
    expect(result.status, result.text).toBe(200);
    return result.body;
  });
  expect(accepted['newTermNumber']).toBe(2);
  expect(accepted['predecessorTermId']).toBe(policy.termId);
  expect(accepted['termState']).toBe('SCHEDULED');
  const newTermId = accepted['newTermId'] as string;
  const deltas = accepted['chargeDeltas'] as Json[];
  const premiumDeltas = deltas.filter((d) => d['chargeCategory'] === 'PREMIUM');
  const taxDeltas = deltas.filter((d) => d['chargeCategory'] === 'TAX');
  expect(premiumDeltas.length).toBeGreaterThanOrEqual(2);
  const term2Premium = sum(premiumDeltas.map((d) => cents(d['amount'])));
  const term2Tax = sum(taxDeltas.map((d) => cents(d['amount'])));
  expect(term2Premium > 0n).toBe(true);

  await test.step('term 2 is a new term pinned to the version resolved at its start (REQ-POL-246)', async () => {
    const term = await call(request, underwriter, 'GET', `/api/pol/v1/terms/${newTermId}`);
    expect(term.status, term.text).toBe(200);
    expect(term.body['term']['termNumber']).toBe(2);
    expect(term.body['term']['productVersion']).toBe('1.1');
    expect(Date.parse(term.body['term']['period']['from'])).toBe(Date.parse(policy.startAt.toISOString()) + 365 * 86_400_000);
  });

  await test.step('accepting twice does not bind a third term', async () => {
    const again = await call(request, underwriter, 'POST', '/api/pol/v1/renewals/accept', {
      jobId, termId: policy.termId, channel: 'STAFF', acceptedAt: iso(await serverNow(request)),
    });
    expect(again.status, again.text).toBeGreaterThanOrEqual(400);
  });

  // ---- BIL: the worker invoices term 2 (RenewalBound intake); the fiscal stub registers it.
  const invoice2 = await eventually('the invoice of term 2', async () => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
    expect(list.status, list.text).toBe(200);
    const items = (list.body['items'] as Json[]).map((i) => i['invoice'] as Json);
    return items.find((i) => i['policyTermId'] === newTermId);
  });
  expect(invoice2['kind']).toBe('INVOICE');
  expect(cents(invoice2['total'])).toBe(term2Premium + term2Tax);
  const read = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoice2['invoiceId']}`)).body;
  const items = read['invoiceItems'] as Json[];
  expect(sum(items.filter((i) => i['chargeCategory'] === 'TAX').map((i) => cents(i['amount']))), 'IPT of term 2').toBe(term2Tax);
  for (const item of items.filter((i) => i['chargeCategory'] === 'TAX')) expect(item['provisional']).toBe(true);
  const fiscal = await eventually('the fiscal document of the term 2 invoice', async () => {
    const f = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${invoice2['invoiceId']}`)).body['fiscalStatus'] as Json;
    return f['status'] === 'REGISTERED' ? f : undefined;
  });
  expect(fiscal['mark']).toMatch(/^STUB-\d+$/);

  // ---- FIN: term 2 journals (written, billed, IPT due) balance; written premium of both terms nets to the two terms' premiums.
  const journals = await eventually('FIN journals of term 2', async () => {
    const all: Json[] = [];
    let cursor: string | undefined;
    do {
      const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?policyNumber=${encodeURIComponent(policy.policyNumber)}&limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
      expect(page.status, page.text).toBe(200);
      all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    const lines = all.flatMap((j) => j['lines'] as Json[]);
    const written = sum(lines.filter((l) => l['account'] === 'GL-2110').map((l) => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']))));
    const rules = new Set(all.flatMap((j) => j['ruleCodes'] as string[]).map((c) => c.split('-')[0]));
    return written === -(policy.premium + term2Premium) && ['WR', 'BL', 'ID'].every((f) => rules.has(f)) && all.length >= 8 ? all : undefined;
  });
  for (const journal of journals) {
    const lines = journal['lines'] as Json[];
    const side = (s: string) => sum(lines.filter((l) => l['side'] === s).map((l) => cents(l['amount'])));
    expect(side('DEBIT'), `${journal['journalNumber']} balances`).toBe(side('CREDIT'));
  }
  const net = (account: string) => sum(journals.flatMap((j) => j['lines'] as Json[]).filter((l) => l['account'] === account).map((l) => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']))));
  expect(net('GL-2110'), 'written premium of term 1 and term 2').toBe(-(policy.premium + term2Premium));
  expect(net('GL-2410'), 'IPT payable of term 1 and term 2').toBe(-(policy.tax + term2Tax));
});
