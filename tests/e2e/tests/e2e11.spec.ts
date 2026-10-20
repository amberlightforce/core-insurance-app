import { expect, test } from '@playwright/test';
import { call, cents, eventually, settled, signIn, sum, type Json } from './support/api.js';
import { scalar, sql } from './support/pg.js';
import { importProducts, issuePolicy, iso, serverNow } from './support/servicing.js';
import { advanceToAthens, athensDate } from './support/withdrawal.js';

// E2E-11 out-of-sequence change with an open claim and a cession (SLICE-PLAN-6 E2E-11; REQ-POL-108, D-SL6-10): a paid policy takes an
// in-sequence change, a claim opens on the new cover, the policy's first month is locked in FIN, and an earlier-dated change (POL-OOS,
// NET mode) is bound. Asserted: the claim keeps its snapshot and gets ReverificationRequired exactly once (one PolicyChanged for the
// whole correction); BIL bills the difference as one new invoice and leaves the paid invoice alone; FIN posts the correction in the first
// open month as one balanced journal and nothing new in the locked month; RI's cession target minus booked converges (30 percent quota
// share from config, ILLUSTRATIVE); the POL premium delta equals the preview and the premium difference (GROSS mode is unit-tested only,
// the DAT assertion is deferred, D-SL6-10). The RI, BIL respread and FIN link rows have no read API yet, so they are read with psql
// (tests/e2e/tests/support/pg.ts). Runs through tests/e2e/run-e2e11.sh (Shiftable clock, cession treaty QS-E2E11 in the stack env).

const SHARE_PCT = 30n;
const cessionOf = (premium: bigint): bigint => (premium * SHARE_PCT * 2n + 100n) / 200n; // half away from zero (positive premiums)
const dec = (text: string | undefined): bigint => cents(text ?? '0');

test('E2E-11 out-of-sequence change: claim re-verified once, BIL respread, FIN correction in the open month, RI cession converges', async ({ request }) => {
  test.setTimeout(900_000);
  await importProducts(request);
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  const finance = await signIn(request, 'finance');
  const claimsUser = await signIn(request, 'claims');

  // ---- 1. A paid policy; the clock moves into the next calendar month; an in-sequence change (value 18 000) is bound there.
  const policy = await issuePolicy(request, 'e2e11', { pay: true, plate: 'IKX-1101' });
  const lockedMonth = athensDate(policy.startAt).slice(0, 7);
  const [year, month] = lockedMonth.split('-').map(Number) as [number, number];
  const nextMonth = new Date(Date.UTC(year, month, 1)).toISOString().slice(0, 7); // month is 1-based, so this is the next month
  await advanceToAthens(request, `${nextMonth}-04`, '12:00:00');

  const edit = async (jobId: string, value: string): Promise<void> => {
    const r = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
      jobId, versionNo: 1, expectedDraftVersion: 0,
      instructions: [{ op: 'SET_VEHICLE', vehicle: { locator: policy.vehicleLocator, plate: 'IKX-1101', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: value, currency: 'EUR' } } }],
    });
    expect(r.status, r.text).toBe(200);
  };
  const quoteAndBind = async (jobId: string): Promise<{ preview: Json; bind: Json }> => {
    const quote = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
    expect(quote.status, quote.text).toBe(200);
    const preview = await call(request, underwriter, 'GET', `/api/pol/v1/policy-changes/${jobId}/preview`);
    expect(preview.status, preview.text).toBe(200);
    const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
    expect(bind.status, bind.text).toBe(200);
    expect(bind.body['state']).toBe('BOUND');
    return { preview: preview.body, bind: bind.body };
  };

  const created = await call(request, underwriter, 'POST', '/api/pol/v1/policy-changes', { policyId: policy.policyId, effectiveAt: iso(await serverNow(request)), description: 'e2e11 in-sequence change' });
  expect(created.status, created.text).toBe(201);
  await edit(created.body['jobId'], '18000.00');
  await quoteAndBind(created.body['jobId']);
  const premiumLines = (): bigint => dec(scalar(`SELECT coalesce(sum(amount), 0) FROM pol.charge_line WHERE policy_id = '${policy.policyId}' AND charge_category = 'PREMIUM'`));
  const premiumBefore = premiumLines();
  expect(premiumBefore > policy.premium, 'the in-sequence change raised the premium').toBe(true);

  // ---- 2. An open claim on the policy (reported after the in-sequence change).
  // The loss is in the span the earlier change will re-rate (two minutes after the policy start), so the claim's snapshot is superseded.
  const lossAt = iso(new Date(policy.startAt.getTime() + 120_000));
  const fnol = await call(request, claimsUser, 'POST', '/api/clm/v1/fnol/submit', {
    lineOfBusiness: 'MOTOR', policyId: policy.policyId, policyNumber: policy.policyNumber, lossAt, lossCause: 'COLLISION',
    lossLocation: 'Λεωφ. Κηφισίας 124, Αθήνα', description: 'out-of-sequence journey: claim open while an earlier change is applied (synthetic)', channel: 'STAFF', receiptMedium: 'TELEPHONE',
    incidents: [{ incidentType: 'VEHICLE', vehicleRef: 'IKX1101', drivable: true, damageAreas: ['FRONT'] }],
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
  });
  expect(fnol.status, fnol.text).toBe(200);
  const claimId = fnol.body['claimId'] as string;
  const oldRef = fnol.body['claim']['snapshotRef'] as string;
  expect(fnol.body['claim']['snapshotStatus']).toBe('VERIFIED');

  // ---- 3. The cession and the FIN postings of the term so far have settled; then the policy's first month is locked.
  const cessionRows = (): string[][] => sql(`SELECT target, delta, booked_before FROM ri.cession WHERE term_id = '${policy.termId}' ORDER BY recorded_at, cession_id`);
  await eventually('the cession of the opening and the in-sequence change', async () => {
    const rows = cessionRows();
    return rows.length >= 2 && dec(rows[rows.length - 1]![0]) === cessionOf(premiumBefore) ? true : undefined;
  });
  let stable = 0;
  let last = -1;
  await eventually('FIN journals of the term to settle', async () => {
    const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?policyNumber=${encodeURIComponent(policy.policyNumber)}&limit=100`);
    expect(page.status, page.text).toBe(200);
    const n = (page.body['items'] as Json[]).length;
    stable = n === last ? stable + 1 : 0;
    last = n;
    return stable >= 15 && n >= 6 ? true : undefined; // 15 quiet seconds: a loaded CI runner lags behind the outbox
  });
  const lock = await call(request, finance, 'POST', `/api/fin/v1/dev/periods/${lockedMonth}/lock`);
  expect(lock.status, lock.text).toBe(200);
  const lockedAt = scalar('SELECT now()')!;

  // ---- 4. The earlier-dated change (a minute after the policy start, value 20 000), started with the out-of-sequence operation.
  const earlier = iso(new Date(policy.startAt.getTime() + 60_000));
  const plain = await call(request, underwriter, 'POST', '/api/pol/v1/policy-changes', { policyId: policy.policyId, effectiveAt: earlier, description: 'plain create refuses it' });
  expect(plain.status, 'the plain create still refuses an earlier effective time').toBeGreaterThanOrEqual(400);
  expect(plain.body['code']).toBe('POL-ERR-OUT-OF-SEQUENCE');
  const gross = await call(request, underwriter, 'POST', '/api/pol/v1/policy-changes/out-of-sequence', { policyId: policy.policyId, effectiveAt: earlier, mode: 'GROSS' });
  expect(gross.status, 'GROSS mode is not enabled through the API (D-SL6-10)').toBe(422);

  const oos = await call(request, underwriter, 'POST', '/api/pol/v1/policy-changes/out-of-sequence', { policyId: policy.policyId, effectiveAt: earlier, description: 'e2e11 earlier-dated change', mode: 'NET' });
  expect(oos.status, oos.text).toBe(201);
  await edit(oos.body['jobId'], '20000.00');
  const bound = await quoteAndBind(oos.body['jobId']);
  const oosTransaction = bound.bind['transactionId'] as string;
  const premiumAfter = premiumLines();

  // ---- 5. POL: one delta set whose premium equals the preview and the premium difference; one record, NET.
  const deltas = bound.bind['chargeDeltas'] as Json[];
  const setPremium = sum(deltas.filter((d) => d['chargeCategory'] === 'PREMIUM').map((d) => cents(d['amount'])));
  const setTax = sum(deltas.filter((d) => d['chargeCategory'] === 'TAX').map((d) => cents(d['amount'])));
  expect(setPremium > 0n, 'raising the value before the in-sequence change raises the premium').toBe(true);
  expect(setPremium).toBe(premiumAfter - premiumBefore);
  expect(cents(bound.preview['servicingPreview']['premiumChange'])).toBe(setPremium);
  expect(scalar(`SELECT mode || '/' || status FROM pol.oos_correction WHERE transaction_id = '${oosTransaction}'`)).toBe('NET/APPLIED');
  // TODO(hardening): the GROSS-mode premium delta and the DAT assertion are deferred (D-SL6-10).

  // ---- 6. CLM: ReverificationRequired, the claim keeps its snapshot until a human decides.
  const pending = await eventually('the claim to require re-verification', async () => {
    const read = await call(request, claimsUser, 'GET', `/api/clm/v1/claims/${claimId}`);
    expect(read.status, read.text).toBe(200);
    const claim = (read.body['claim'] ?? read.body) as Json;
    return claim['summary']['snapshotStatus'] === 'REVERIFICATION_REQUIRED' ? claim : undefined;
  });
  expect(pending['summary']['snapshotRef'], 'REQ-CLM-002: the old ref stays until a human adopts').toBe(oldRef);
  expect(pending['pendingReverification']['oldSnapshotRef']).toBe(oldRef);
  expect(pending['pendingReverification']['newSnapshotRef']).not.toBe(oldRef);

  // ---- 7. BIL: the debit is one new invoice; the paid first invoice is untouched.
  const respread = await eventually('the BIL respread of the correction', async () =>
    sql(`SELECT net_amount, invoiced_amount, credited_amount, paid_invoices_untouched, oos_set_id IS NOT NULL FROM bil.respread WHERE term_id = '${policy.termId}' AND transaction_id = '${oosTransaction}'`)[0]);
  expect(dec(respread[1]), 'invoiced = premium plus IPT of the set').toBe(setPremium + setTax);
  expect(dec(respread[2]), 'a debit set credits nothing').toBe(0n);
  expect(dec(respread[0])).toBe(setPremium + setTax);
  expect(Number(respread[3]), 'the paid invoice was counted as untouched').toBeGreaterThanOrEqual(1);
  expect(respread[4]).toBe('t');
  const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
  expect(list.status, list.text).toBe(200);
  const invoices = (list.body['items'] as Json[]).map((i) => i['invoice'] as Json);
  const correctionInvoices = invoices.filter((i) => i['transactionId'] === oosTransaction);
  expect(correctionInvoices, 'the OOS debit is billed as exactly one new invoice').toHaveLength(1);
  expect(correctionInvoices[0]!['kind']).toBe('INVOICE');
  expect(cents(correctionInvoices[0]!['total'])).toBe(setPremium + setTax);
  const first = invoices.find((i) => i['invoiceId'] === policy.invoiceId)!;
  expect(first['state'], 'the paid invoice stays paid').toBe('PAID');
  expect(cents(first['total'])).toBe(policy.total);

  // ---- 8. FIN: the correction lands in the first open month as one balanced journal; nothing new in the locked month.
  const correctionJournals = await eventually('the FIN correction journals of the set', async () => {
    const rows = sql(`SELECT j.journal_id, j.accounting_date, p.period_code, sum(CASE l.side WHEN 'DEBIT' THEN l.amount ELSE -l.amount END)
      FROM fin.journal_entry j JOIN fin.financial_period p USING (period_id) JOIN fin.journal_line l USING (journal_id)
      WHERE j.oos_set_id IS NOT NULL AND EXISTS (SELECT 1 FROM fin.journal_line x WHERE x.journal_id = j.journal_id AND x.policy_id = '${policy.policyId}')
      GROUP BY 1, 2, 3`);
    return rows.length > 0 ? rows : undefined;
  });
  for (const [, accountingDate, period, balance] of correctionJournals) {
    // BIL dates the set's entries today, which is in the open month; the first-day shift is for entries dated in the locked month (FIN-OOS-GUARD).
    expect(accountingDate!.slice(0, 7), 'posted in the open month').toBe(nextMonth);
    expect(period).toBe(nextMonth);
    expect(dec(balance), 'the journal balances').toBe(0n);
  }
  expect(sql(`SELECT 1 FROM fin.journal_entry j JOIN fin.financial_period p USING (period_id) WHERE p.status = 'LOCKED' AND j.posted_at > '${lockedAt}'`), 'nothing posted into the locked month').toHaveLength(0);
  expect(scalar(`SELECT count(*) FROM fin.business_event WHERE status = 'SUSPENDED'`), 'no event was suspended by the lock').toBe('0');
  await eventually('written premium in the GL to equal the re-rated premium', async () => {
    const lines: Json[] = [];
    let cursor: string | undefined;
    do {
      const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?policyNumber=${encodeURIComponent(policy.policyNumber)}&limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
      expect(page.status, page.text).toBe(200);
      lines.push(...(page.body['items'] as Json[]).flatMap((i) => i['journal']['lines'] as Json[]));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    const written = sum(lines.filter((l) => l['account'] === 'GL-2110').map((l) => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']))));
    return written === -premiumAfter ? true : undefined;
  });

  // ---- 9. RI: target minus booked: the booked cessions of the term sum to 30 percent of the re-rated premium; no exception.
  const converged = await eventually('the cession to converge to the re-rated premium', async () => {
    const rows = cessionRows();
    return rows.length >= 3 && sum(rows.map((r) => dec(r[1]))) === cessionOf(premiumAfter) ? rows : undefined;
  });
  const lastRow = converged[converged.length - 1]!;
  expect(dec(lastRow[0]), 'target = share x written premium').toBe(cessionOf(premiumAfter));
  expect(dec(lastRow[2]), 'booked before = the target before the correction').toBe(cessionOf(premiumBefore));
  expect(dec(lastRow[1]), 'the delta is target minus booked').toBe(cessionOf(premiumAfter) - cessionOf(premiumBefore));
  expect(scalar(`SELECT count(*) FROM ri.cession_exception WHERE term_id = '${policy.termId}'`)).toBe('0');

  // ---- 10. Once: one PolicyChanged for the whole correction and one ReverificationRequired for the claim, however long we wait.
  // Wait for the outbox to drain (every message dispatched, and the count stable), then assert the counts.
  await eventually('the outbox to drain', async () => (scalar(`SELECT count(*) FROM plt.outbox_message WHERE dispatched_at IS NULL`) === '0' ? true : undefined), { timeoutMs: 120_000 });
  await settled('the outbox message count to settle', async () => scalar(`SELECT count(*) FROM plt.outbox_message`), { timeoutMs: 60_000 });
  expect(scalar(`SELECT count(*) FROM plt.outbox_message WHERE event_type = 'PolicyChanged' AND business_keys->>'transactionId' = '${oosTransaction}'`)).toBe('1');
  expect(scalar(`SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ReverificationRequired' AND business_keys->>'claimId' = '${claimId}'`)).toBe('1');
  expect(scalar(`SELECT count(*) FROM ri.cession WHERE term_id = '${policy.termId}' AND set_key = '${oosTransaction}'`), 'one cession row per set').toBe('1');
});
