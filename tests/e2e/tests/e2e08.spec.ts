import { expect, test } from '@playwright/test';
import { call, cents, eventually, signIn, type Json } from './support/api.js';
import { importProducts, issuePolicy, registerRefundAccount, type Issued } from './support/servicing.js';
import { addDays, advanceToAthens, athensDate, athensTime, concludedDate, confirm, declare, nextDstChangeDay, readWithdrawal } from './support/withdrawal.js';

// E2E-08 distance withdrawal (SLICE-PLAN-6 E2E-08, D-SL6-02, D-SL6-06): a paid policy is withdrawn through CHN on the last day of
// the window (23:59:50 Europe/Athens on the dev clock), POL voids the term once, BIL credits and refunds the whole amount paid
// including IPT (deduction 0.00), DOC renders the acknowledgement once. A declaration confirmed after midnight is declined and
// voids nothing; the window is counted in local calendar days across a DST change; a double confirm is one void. F-401 default:
// full refund (the money leg, BIL credits the IPT of the void since GAP-SL3-BIL-VOID); the tax_position TAX_OPINION_PENDING note lives in the GR pack row and is not exposed by an API. Runs through tests/e2e/run-e2e08.sh.

const IBAN = 'GR1601101250000000012300695';
const WINDOW_DAYS = 14; // ILLUSTRATIVE (D-SL6-06), the CHN and POL default
const LAST_SECONDS = '23:59:50'; // a few seconds of headroom for the calls between the advance and the confirm

test('E2E-08 withdrawal refund: one credit note for the whole amount paid incl. IPT, stub fiscal credit, refund paid (deduction 0.00)', async ({ request }) => {
  test.setTimeout(600_000);
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  const billingManager = await signIn(request, 'billingmgr');
  await importProducts(request);
  const creditNotes = async (policy: Issued): Promise<Json[]> => {
    const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
    expect(list.status, list.text).toBe(200);
    return (list.body['items'] as Json[]).map((i) => i['invoice'] as Json).filter((i) => i['kind'] === 'CREDIT_NOTE');
  };
  const main = await issuePolicy(request, 'e2e08-money', { pay: true, plate: 'IKX-0806' });
  const d = await declare(request, underwriter, main.policyId, 'money');
  expect(d.status, d.text).toBeLessThan(300);
  const confirmed = await confirm(request, underwriter, d.body['requestNumber'], d.body['confirmToken']);
  expect(confirmed.status, confirmed.text).toBe(200);
  const again = await confirm(request, underwriter, d.body['requestNumber'], d.body['confirmToken']);
  expect(again.status, again.text).toBe(200);
  const credit = await eventually('the credit note of the withdrawal', async () => (await creditNotes(main))[0]);
  expect(await creditNotes(main), 'a double confirm is one void').toHaveLength(1);
  expect(credit['originalInvoiceId']).toBe(main.invoiceId);
  expect(cents(credit['total']), 'refund = total paid, premium plus IPT').toBe(main.total);
  expect(main.tax, 'the policy carries IPT').toBeGreaterThan(0n);
  const noteRead = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${credit['invoiceId']}`)).body;
  const taxCredit = (noteRead['invoiceItems'] as Json[]).filter((i) => i['chargeCategory'] === 'TAX').reduce((a, i) => a + cents(i['amount']), 0n);
  expect(taxCredit, 'the IPT is credited in full').toBe(main.tax);

  // The stub fiscal credit document, registered with its own MARK.
  const fiscal = await eventually('the stub fiscal credit document', async () => {
    const f = (await call(request, billing, 'GET', `/api/bil/v1/invoices/${credit['invoiceId']}`)).body['fiscalStatus'] as Json;
    return f['status'] === 'REGISTERED' ? f : undefined;
  });
  expect(fiscal['mark']).toMatch(/^STUB-\d+$/);

  // ---- 6. BIL: the refund is proposed, approved if referred, and paid in full.
  const payeeAccountId = await registerRefundAccount(request, main.partyId, IBAN, 'Μαρία Παπαδοπούλου');
  const proposed = await call(request, billing, 'POST', '/api/bil/v1/refunds/propose', { billingAccountId: main.accountId, payeeAccountId, reasonCode: 'POLICY_CANCELLED' });
  expect(proposed.status, proposed.text).toBeLessThan(300);
  let refund = proposed.body['refund'] as Json;
  expect(cents(refund['amount']), 'the whole amount paid comes back').toBe(main.total);
  if (refund['approvalState'] === 'PENDING') {
    const decided = await call(request, billingManager, 'POST', '/api/bil/v1/refunds/decide', { refundId: refund['refundId'], decision: 'APPROVE', comment: 'e2e08' });
    expect(decided.status, decided.text).toBe(200);
  }
  refund = await eventually('the refund to be PAID', async () => {
    const read = await call(request, billing, 'GET', `/api/bil/v1/refunds/${refund['refundId']}`);
    expect(read.status, read.text).toBe(200);
    return read.body['refund']['state'] === 'PAID' ? (read.body['refund'] as Json) : undefined;
  });
  expect(refund['disbursementId']).toBeTruthy();

});

test('E2E-08 distance withdrawal window: last-day confirm, one void, acknowledgement once, late declined, DST, double confirm', async ({ request }) => {
  test.setTimeout(1_500_000);
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  await importProducts(request);

  const termState = async (policy: Issued): Promise<string> => (await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`)).body['term']['state'] as string;
  const declared = async (policy: Issued, tag: string): Promise<{ number: string; token: string }> => {
    const d = await declare(request, underwriter, policy.policyId, tag);
    expect(d.status, d.text).toBeLessThan(300);
    expect(d.body['status']).toBe('DECLARED');
    expect(d.body['confirmToken'], 'the stamp returns the token (no mail channel)').toBeTruthy();
    return { number: d.body['requestNumber'] as string, token: d.body['confirmToken'] as string };
  };

  // ---- 1. Three paid policies concluded on the same day; the window is 14 days.
  const main = await issuePolicy(request, 'e2e08-main', { pay: true, plate: 'IKX-0801' });
  const late = await issuePolicy(request, 'e2e08-late', { pay: true, plate: 'IKX-0802' });
  const never = await issuePolicy(request, 'e2e08-never', { pay: true, plate: 'IKX-0803' });
  const day0 = concludedDate(main);
  const lastDay = addDays(day0, WINDOW_DAYS);
  expect(await termState(main)).not.toBe('VOIDED');
  // The confirmation token lives 48 h (CHN), so both steps happen on the last day: declare at 23:59:35, confirm at 23:59:50 or later.
  await advanceToAthens(request, lastDay, '23:59:35');
  const dMain = await declared(main, 'main');
  const dLate = await declared(late, 'late');
  const open = await readWithdrawal(request, underwriter, dMain.number);
  expect(open['status']).toBe('DECLARED');
  expect(open['declineCode'] ?? null).toBeNull();
  expect(athensDate(new Date(open['deadlineAt'])), 'the deadline is on the last day').toBe(lastDay);
  expect(athensTime(new Date(open['deadlineAt'])), 'and is its last second').toBe('23:59:59');

  // ---- 2. Last day, 23:59:50: confirm (step 2 of 2) -> POL voids once.
  const lastNow = await advanceToAthens(request, lastDay, LAST_SECONDS);
  expect(athensDate(lastNow)).toBe(lastDay);
  const confirmed = await confirm(request, underwriter, dMain.number, dMain.token);
  expect(confirmed.status, confirmed.text).toBe(200);
  expect(confirmed.body['declineCode'] ?? null, confirmed.text).toBeNull();
  expect(['CONFIRMED', 'SUBMITTED'], confirmed.text).toContain(confirmed.body['status']);
  const confirmedAt = new Date(confirmed.body['confirmedAt']);
  expect(athensDate(confirmedAt), 'confirmed on the last day').toBe(lastDay);
  expect(athensTime(confirmedAt) >= LAST_SECONDS, 'in the last seconds of it').toBe(true);

  // Double confirm (a second click, a retry): the same answer, still one void.
  const again = await confirm(request, underwriter, dMain.number, dMain.token);
  expect(again.status, again.text).toBe(200);
  expect(again.body['status']).toBe(confirmed.body['status']);
  expect(again.body['confirmedAt']).toBe(confirmed.body['confirmedAt']);

  expect(await termState(main), 'the term is voided ab initio').toBe('VOIDED');
  const row = await readWithdrawal(request, underwriter, dMain.number);
  expect(row['status']).toBe('SUBMITTED');

  // ---- 5. DOC: the acknowledgement was rendered once.
  const requestId = row['withdrawalRequestId'] as string;
  const docs = await call(request, billing, 'GET', `/api/doc/v1/documents/list-for-object?objectRef=${encodeURIComponent(`CHN/WithdrawalRequest/${requestId}`)}`);
  expect(docs.status, docs.text).toBe(200);
  const acks = (docs.body['items'] as Json[]).filter((d) => JSON.stringify(d).includes('DT-WITHDRAWAL-ACK'));
  expect(acks, 'one acknowledgement however often it is confirmed').toHaveLength(1);

  // CHN closes the request with the POL outcome.
  const closed = await call(request, underwriter, 'POST', `/api/chn/v1/withdrawals/${encodeURIComponent(dMain.number)}/close`, { outcome: 'PROCESSED' });
  expect(closed.status, closed.text).toBe(200);
  expect(closed.body['status']).toBe('PROCESSED');

  // ---- 7. Late: confirmed at 00:00:05 on the day after the last day -> declined, nothing voided.
  const lateNow = await advanceToAthens(request, addDays(lastDay, 1), '00:00:05');
  expect(athensDate(lateNow)).toBe(addDays(lastDay, 1));
  const lateConfirm = await confirm(request, underwriter, dLate.number, dLate.token);
  expect(lateConfirm.status, lateConfirm.text).toBe(200);
  expect(lateConfirm.body['status']).toBe('DECLINED');
  expect(lateConfirm.body['declineCode']).toBe('CHN-ERR-RIGHT-EXPIRED');
  expect(await termState(late), 'a late declaration voids nothing').not.toBe('VOIDED');
  // A declaration made after the window is recorded and declined at once.
  const lateDeclare = await declare(request, underwriter, never.policyId, 'never');
  expect(lateDeclare.status, lateDeclare.text).toBeLessThan(300);
  expect(lateDeclare.body['status']).toBe('DECLINED');
  expect(lateDeclare.body['declineCode']).toBe('CHN-ERR-RIGHT-EXPIRED');
  expect(await termState(never)).not.toBe('VOIDED');
  expect((await readWithdrawal(request, underwriter, lateDeclare.body['requestNumber']))['status'], 'the late declaration is recorded').toBe('DECLINED');

  // ---- 8. DST variant: conclude four days before the next UTC offset change; the window ends at local midnight, not 24 h x 15.
  const change = nextDstChangeDay(athensDate(lateNow));
  const dstDay0 = addDays(change, -4);
  await advanceToAthens(request, dstDay0, '10:00:00');
  const dstOk = await issuePolicy(request, 'e2e08-dst-ok', { pay: true, plate: 'IKX-0804' });
  const dstLate = await issuePolicy(request, 'e2e08-dst-late', { pay: true, plate: 'IKX-0805' });
  expect(concludedDate(dstOk)).toBe(dstDay0);
  const dstLast = addDays(dstDay0, WINDOW_DAYS);
  expect(dstLast > change, 'the window spans the offset change').toBe(true);
  await advanceToAthens(request, dstLast, '23:59:35');
  const dOk = await declared(dstOk, 'dst-ok');
  const dDstLate = await declared(dstLate, 'dst-late');
  await advanceToAthens(request, dstLast, LAST_SECONDS);
  const dstConfirm = await confirm(request, underwriter, dOk.number, dOk.token);
  expect(dstConfirm.status, dstConfirm.text).toBe(200);
  expect(dstConfirm.body['status'], 'the last local day still counts across the DST change').not.toBe('DECLINED');
  expect(await termState(dstOk)).toBe('VOIDED');
  await advanceToAthens(request, addDays(dstLast, 1), '00:00:05');
  const dstLateConfirm = await confirm(request, underwriter, dDstLate.number, dDstLate.token);
  expect(dstLateConfirm.body['status'], 'the first local second after the last day is late').toBe('DECLINED');
  expect(await termState(dstLate)).not.toBe('VOIDED');
});
