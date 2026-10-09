import { expect, test } from '@playwright/test';
import { call, cents, eventually, signIn, sum, type Json } from './support/api.js';
import { advanceTo, importProducts, issuePolicy, iso, registerRefundAccount, routeExists, serverNow } from './support/servicing.js';

// E2E-03 policyholder cancellation -> pro-rata credit -> fiscal credit note -> refund payout -> FIN journals, at API level
// (SLICE-PLAN-3 §3.1). The E2E-01 policy (annual premium 430.00, IPT 64.50, paid in full at day 0), the dev clock at day 120:
// 430.00 x 245/365 = 288.63 credited (TERM_RATIO, illustrative), the IPT kept (KEEP_NOT_REDUCED, PendingOpinion, provisional:
// D-SL3-05, D-SL3-23). Runs through tests/e2e/run-e2e03.sh (Shiftable clock, D-SL3-12). The steps that need POL-CANCEL and
// BIL-REFUND skip with a reason until those are on the stack under test.

const FIRST_IBAN = 'GR9608100010000001234567890';
const IBAN = 'GR1601101250000000012300695'; // the changed account the refund is paid to
// The complete journal set of the account, by the first rule code of each journal (PITFALLS 29): four written lines (two premium,
// two IPT), billed, IPT due, cash received, allocated; then the credit: two credit-written lines, credit billed, refund
// receivable applied (GL-2535 refund payable), refund released to in-transit, and cleared to cash.
const EXPECTED_JOURNALS = [
  'WR-WRITTEN-UNBILLED', 'WR-WRITTEN-UNBILLED', 'WR-WRITTEN-UNBILLED', 'WR-WRITTEN-UNBILLED', 'BL-BILLED', 'ID-NOT-DUE', 'RC-CASH', 'AL-UNALLOCATED',
  'CW-PREMIUM', 'CW-PREMIUM', 'CB-UNBILLED', 'RA-RECEIVABLE', 'DR-REFUND-PAYABLE', 'DC-IN-TRANSIT',
].sort();
const CANONICAL_PREMIUM = 43_000n;
const CANONICAL_CREDIT = 28_863n; // 430.00 x 245/365

/** Σ over the premium lines of round-half-up(line x days / termDays), the per-line proration of RAT. */
const prorate = (lines: bigint[], days: bigint, termDays: bigint): bigint => sum(lines.map((l) => (l * days * 2n + termDays) / (termDays * 2n)));

test('E2E-03 cancellation: day-120 credit 288.63, IPT kept, credit note, fiscal CREDIT, refund payout, journals', async ({ request }) => {
  test.setTimeout(420_000);
  const admin = await signIn(request, 'admin');
  const underwriter = await signIn(request, 'underwriter');
  test.skip(!(await routeExists(request, underwriter, 'POST', '/api/pol/v1/cancellations')), 'POST /api/pol/v1/cancellations is not available yet (SL3-POL-CANCEL not merged)');
  void admin;
  const billing = await signIn(request, 'billing');
  const billingManager = await signIn(request, 'billingmgr');
  const finance = await signIn(request, 'finance');
  const refundsAvailable = await routeExists(request, billing, 'GET', '/api/bil/v1/refunds');

  await importProducts(request);
  const policy = await issuePolicy(request, 'e2e03');
  // The illustrative tariff prices the car by its age in the dev clock's year: 430.00 a year on the first journeys of a fresh stack
  // (the canonical amounts of the plan); a later clock year gives another premium and the credit is computed from the quote lines.
  const premiumLines = (policy.quote['charges'] as Json[]).filter((c) => c['chargeCategory'] === 'PREMIUM').map((c) => cents(c['amount']));
  const canonical = policy.premium === CANONICAL_PREMIUM;
  test.info().annotations.push({ type: 'premium', description: `${policy.premium} cents, canonical ${canonical}` });
  const bodies: string[] = [];
  // The policyholder registers an account and then changes it: a refund to a changed payee is referred to the billing manager
  // (REQ-BIL-188/189, D-SL3-14). The cooling-off of the change has passed long before the day-120 refund.
  if (refundsAvailable) await registerRefundAccount(request, policy.partyId, FIRST_IBAN, 'Μαρία Παπαδοπούλου');
  const payeeAccountId = refundsAvailable ? await registerRefundAccount(request, policy.partyId, IBAN, 'Μαρία Παπαδοπούλου') : undefined;

  // ---- 1-3. Cancel now at day 120 (source Policyholder): preview, then the cancellation itself.
  await advanceTo(request, policy, 120);
  const effectiveAt = iso(await serverNow(request));
  const request03 = { policyId: policy.policyId, source: 'Policyholder', reasonCode: 'CUSTOMER_REQUEST', effectiveAt, kind: 'STANDARD' };

  const dry = await call(request, underwriter, 'POST', '/api/pol/v1/cancellations?dryRun=true', request03);
  expect(dry.status, dry.text).toBe(200);
  const dryPreview = dry.body['servicingPreview'] as Json;
  const days = BigInt((dryPreview['proratedLines'] as Json[])[0]!['days']);
  const termDays = BigInt((dryPreview['proratedLines'] as Json[])[0]!['termDays']);
  const CREDIT = prorate(premiumLines, days, termDays);
  expect(cents(dryPreview['refundDue']), 'the dry run previews the credit and writes nothing').toBe(CREDIT);
  if (canonical) {
    expect(CREDIT, '430.00 x 245/365').toBe(CANONICAL_CREDIT);
    expect([days, termDays]).toEqual([245n, 365n]);
  }
  const term = await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`);
  expect(term.body['term']['state'], 'a dry run leaves the term in force').toBe('IN_FORCE');

  const cancelled = await call(request, underwriter, 'POST', '/api/pol/v1/cancellations', request03);
  expect(cancelled.status, cancelled.text).toBe(200);
  bodies.push(cancelled.text);
  expect(cancelled.body['state']).toBe('BOUND');
  expect(cancelled.body['kind']).toBe('STANDARD');
  const preview = cancelled.body['servicingPreview'] as Json;
  expect(preview['transactionKind']).toBe('CANCELLATION');
  expect(preview['cancellationSource']).toBe('Policyholder');
  expect(preview['refundMethod']).toBe('PRO_RATA');
  expect(cents(preview['annualBefore'])).toBe(policy.premium);
  expect(cents(preview['premiumChange']), 'credit = 430.00 x 245/365 (REQ-RAT-004, -155)').toBe(-CREDIT);
  expect(cents(preview['taxChange']), 'IPT not credited (REQ-MKT-330, -331)').toBe(0n);
  expect(cents(preview['totalChange'])).toBe(-CREDIT);
  expect(cents(preview['refundDue'])).toBe(CREDIT);
  expect(cents(preview['additionalDue'])).toBe(0n);
  expect(preview['provisional'], 'servicing IPT is provisional outside Production (D-SL3-23)').toBe(true);
  const taxLines = preview['taxLines'] as Json[];
  expect(taxLines.length).toBeGreaterThan(0);
  for (const tax of taxLines) {
    expect(tax['treatmentAction']).toBe('KEEP_NOT_REDUCED');
    expect(tax['legalStatus']).toBe('PendingOpinion');
    expect(tax['provisional']).toBe(true);
    expect(cents(tax['amount'])).toBe(0n);
  }
  const prorated = preview['proratedLines'] as Json[];
  expect(prorated.every((l) => BigInt(l['days']) === days && BigInt(l['termDays']) === termDays), 'every line prorates over the same days').toBe(true);
  expect(sum(prorated.map((l) => cents(l['amount'])))).toBe(-CREDIT);

  await test.step('the term is cancelled and the job is bound', async () => {
    const read = await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`);
    expect(read.body['term']['state']).toBe('CANCELLED');
    const again = await call(request, underwriter, 'POST', '/api/pol/v1/cancellations', request03);
    expect(again.status, 'a cancelled term cannot be cancelled again').toBeGreaterThanOrEqual(400);
  });

  // ---- 5. BIL: the credit is billed at once as a credit note against the original invoice.
  const creditNote = await eventually('the credit note of the cancellation', async () => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
    expect(list.status, list.text).toBe(200);
    return (list.body['items'] as Json[]).map((i) => i['invoice'] as Json).find((i) => i['kind'] === 'CREDIT_NOTE');
  });
  expect(creditNote['originalInvoiceId'], 'correlated to the original invoice (REQ-BIL-091)').toBe(policy.invoiceId);
  expect(cents(creditNote['total'])).toBe(CREDIT);
  expect(creditNote['invoiceNumber']).toMatch(/^CN/);
  const noteRead = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${creditNote['invoiceId']}`)).body;
  expect((noteRead['invoiceItems'] as Json[]).filter((i) => i['chargeCategory'] === 'TAX' && cents(i['amount']) !== 0n), 'no IPT is credited').toEqual([]);

  // ---- 6. Fiscal CREDIT document (stub), exactly one, correlated to the MARK of the original invoice.
  const fiscal = await eventually('the stub fiscal credit document', async () => {
    const f = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${creditNote['invoiceId']}`)).body['fiscalStatus'] as Json;
    return f['status'] === 'REGISTERED' ? f : undefined;
  });
  expect(fiscal['mark']).toMatch(/^STUB-\d+$/);
  const original = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${policy.invoiceId}`)).body['fiscalStatus'] as Json;
  expect(fiscal['mark'], 'a credit document of its own').not.toBe(original['mark']);

  test.skip(!refundsAvailable, 'GET /api/bil/v1/refunds is not available yet (SL3-BIL-REFUND not merged); the payout steps are skipped');

  // ---- 7-8. Refund of the credit: proposed by billing, decided by the billing manager, paid to the masked IBAN.
  const proposed = await call(request, billing, 'POST', '/api/bil/v1/refunds/propose', {
    billingAccountId: policy.accountId, payeeAccountId, reasonCode: 'POLICY_CANCELLED',
  });
  expect(proposed.status, proposed.text).toBeLessThan(300);
  bodies.push(proposed.text);
  let refund = proposed.body['refund'] as Json;
  expect(cents(refund['amount'])).toBe(CREDIT);
  expect(refund['payee']['maskedIban']).toMatch(/0695$/);
  expect(refund['payee']['maskedIban']).not.toBe(IBAN);
  expect(refund['payee']['payeeAccountId']).toBe(payeeAccountId);
  expect(refund['approvalState'], 'a changed payee is referred up to the billing manager').toBe('PENDING');
  if (refund['approvalState'] === 'PENDING') {
    const decided = await call(request, billingManager, 'POST', '/api/bil/v1/refunds/decide', { refundId: refund['refundId'], decision: 'APPROVE', comment: 'e2e03' });
    expect(decided.status, decided.text).toBe(200);
    bodies.push(decided.text);
  } else {
    expect(refund['approvalState'], 'auto-approved within the limit (illustrative)').toBe('NOT_REQUIRED');
  }
  refund = await eventually('the refund to be PAID', async () => {
    const read = await call(request, billing, 'GET', `/api/bil/v1/refunds/${refund['refundId']}`);
    expect(read.status, read.text).toBe(200);
    return read.body['refund']['state'] === 'PAID' ? (read.body['refund'] as Json) : undefined;
  });
  expect(refund['disbursementId']).toBeTruthy();
  const disbursement = await eventually('the refund disbursement to be CLEARED', async () => {
    const read = await call(request, billing, 'GET', `/api/bil/v1/disbursements/${refund['disbursementId']}`);
    expect(read.status, read.text).toBe(200);
    bodies.push(read.text);
    const d = read.body['disbursement'] as Json;
    return String(d['status']).toUpperCase() === 'CLEARED' ? d : undefined;
  });
  expect(cents(disbursement['amount'])).toBe(CREDIT);
  expect(disbursement['maskedPayeeAccount']).toMatch(/0695$/);
  expect(bodies.join('\n'), 'the IBAN never comes back in a response').not.toContain(IBAN);

  // ---- 9. FIN: every journal of the account, the complete set (PITFALLS 29), each balanced; the credit and the refund net out.
  const journals = await eventually('FIN journals of the cancellation, credit note and refund', async () => {
    const all: Json[] = [];
    let cursor: string | undefined;
    do {
      const page = await call(request, finance, 'GET', `/api/fin/v1/journals/query?limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
      expect(page.status, page.text).toBe(200);
      all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    const mine = all.filter((j) => (j['lines'] as Json[]).some((l) => l['dimensions']?.['billingAccountId'] === policy.accountId));
    const accounts = new Set(mine.flatMap((j) => (j['lines'] as Json[]).map((l) => l['account'] as string)));
    const net2535 = sum(mine.flatMap((j) => j['lines'] as Json[]).filter((l) => l['account'] === 'GL-2535').map((l) => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']))));
    // The refund journals (credit released, cash paid) are the last to arrive.
    const net1110 = sum(mine.flatMap((j) => j['lines'] as Json[]).filter((l) => l['account'] === 'GL-1110').map((l) => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']))));
    return accounts.has('GL-2535') && net2535 === 0n && net1110 === policy.total - CREDIT && mine.length === EXPECTED_JOURNALS.length ? mine : undefined;
  });
  expect(journals.map((j) => (j['ruleCodes'] as string[])[0]).sort(), 'the complete set of journal types').toEqual(EXPECTED_JOURNALS);
  const lines = journals.flatMap((j) => (j['lines'] as Json[]).map((l) => ({ journal: j['journalNumber'] as string, account: l['account'] as string, signed: l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']) })));
  for (const journal of journals) {
    expect(sum(lines.filter((l) => l.journal === journal['journalNumber']).map((l) => l.signed)), `${journal['journalNumber']} balances`).toBe(0n);
  }
  const net = (account: string): bigint => sum(lines.filter((l) => l.account === account).map((l) => l.signed));
  test.info().annotations.push({ type: 'journal-rules', description: [...new Set(journals.flatMap((j) => j['ruleCodes'] as string[]))].sort().join(', ') });
  expect(net('GL-2110'), 'written premium = the annual premium less the credit').toBe(-(policy.premium - CREDIT));
  expect(net('GL-2410'), 'IPT payable unchanged by the cancellation').toBe(-policy.tax);
  expect(net('GL-2535'), 'refund clearing nets to zero').toBe(0n);
  expect(net('GL-1210'), 'the receivable nets to zero').toBe(0n);
  expect(net('GL-1110'), 'cash: received in full, the credit paid out').toBe(policy.total - CREDIT);
  expect(sum(lines.map((l) => l.signed)), 'the ledger balances').toBe(0n);
});
