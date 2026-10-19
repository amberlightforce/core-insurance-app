import { expect, test } from '@playwright/test';
import { call, cents, eventually, type Json } from './support/api.js';
import {
  applySet, bindMotorPolicy, capturePayee, expectAllBalanced, financials, linesOf, listPayments, net, paymentLine, reserveLine, stubBank,
  submitFnol, tokenFor, waitForDisbursement, waitForJournals, waitForPayment,
} from './support/claims4/index.js';

// Payment corrections (SLICE-PLAN-4 §3.3, SL4-CLM-PAYOPS + SL4-BIL-DISBOPS) on the manual stub bank (Billing:StubBank:Mode=Manual):
// stop a Released payment, the stop of an Issued one is refused and the payment is voided instead, a bank reject, and a reissue.
// Every correction restores the open reserve (the payments erode it) and reverses the ledger effect.

const K = 100n;
const RESERVE = 10_000n * K;
const PAYMENT = 500n * K;

test('payment corrections: stop, void, bank reject and reissue restore the reserve and keep the ledger balanced', async ({ request }) => {
  test.setTimeout(600_000);
  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const billing = await tokenFor(request, 'billing');
  const finance = await tokenFor(request, 'finance');

  const policy = await bindMotorPolicy(request, 'IKX-0801');
  const fnol = await submitFnol(request, claims, policy, {
    description: 'Payment corrections (synthetic)',
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
  });
  const claimId = fnol.claimId;
  const exposureId = fnol.exposures[0]!['exposureId'] as string;
  const insured = policy.policyholderPartyId;
  const payee = await capturePayee(request, claims, claimId, insured, 'Νίκος Αλεξίου');
  await applySet(request, claims, manager, claimId, [reserveLine(exposureId, RESERVE)]);

  const open = async () => cents((await financials(request, claims, claimId))['totals']['openReserve']);
  const paid = async () => cents((await financials(request, claims, claimId))['totals']['paid']);
  expect(await open()).toBe(RESERVE);

  /** Pays 500 and returns the payment once BIL holds it at Released (the manual stub bank keeps it there). */
  const payAndHold = async (): Promise<{ payment: Json; disbursementId: string }> => {
    const before = new Set((await listPayments(request, claims, claimId)).map((p) => p['claimPaymentId']));
    const set = await applySet(request, claims, manager, claimId, [paymentLine(exposureId, PAYMENT, insured, payee, 'PARTIAL')]);
    const payment = await eventually('payment handed to BIL', async () =>
      (await listPayments(request, claims, claimId)).find((p) => !before.has(p['claimPaymentId']) && p['setId'] === set['setId'] && p['disbursementId']));
    await waitForDisbursement(request, billing, payment['disbursementId'], 'Released');
    return { payment, disbursementId: payment['disbursementId'] as string };
  };
  const post = (path: string, body: Json) => call(request, claims, 'POST', path, body);

  // ---- 1. Stop a Released, not yet issued payment: BIL stops it, CLM reverses the effect, the reserve is restored.
  const stopped = await payAndHold();
  expect(await open()).toBe(RESERVE - PAYMENT);
  const stop = await post('/api/clm/v1/payments/stop', { paymentId: stopped.payment['claimPaymentId'], reason: 'WRONG_PAYEE' });
  expect(stop.status, stop.text).toBe(200);
  expect(stop.body['status']).toBe('STOPPED');
  await waitForPayment(request, claims, claimId, stopped.payment['claimPaymentId'], 'STOPPED');
  await waitForDisbursement(request, billing, stopped.disbursementId, 'Stopped');
  expect(await open(), 'open reserve restored by the stop').toBe(RESERVE);
  expect(await paid()).toBe(0n);

  // ---- 2. An Issued payment can no longer be stopped (BIL-ERR-NOT-STOPPABLE -> CLM-ERR-NOT-STOPPABLE); it is voided instead.
  const issued = await payAndHold();
  await stubBank(request, billing, issued.disbursementId, 'issue');
  await waitForPayment(request, claims, claimId, issued.payment['claimPaymentId'], 'ISSUED');
  const refused = await post('/api/clm/v1/payments/stop', { paymentId: issued.payment['claimPaymentId'], reason: 'WRONG_PAYEE' });
  expect(refused.status, refused.text).toBeGreaterThanOrEqual(400);
  expect(refused.body['code'] ?? refused.text).toContain('NOT-STOPPABLE');
  const voided = await post('/api/clm/v1/payments/void', { paymentId: issued.payment['claimPaymentId'], reason: 'ISSUED_IN_ERROR' });
  expect(voided.status, voided.text).toBe(200);
  expect(voided.body['status']).toBe('VOIDED');
  expect(voided.body['reversalTransactionId'], 'the reversal transaction is linked to the original').toBeTruthy();
  expect(await open(), 'open reserve restored by the void').toBe(RESERVE);
  expect(await paid()).toBe(0n);
  const again = await post('/api/clm/v1/payments/void', { paymentId: issued.payment['claimPaymentId'], reason: 'ISSUED_IN_ERROR' });
  expect(again.status, 'void replays').toBe(200);

  // ---- 3. The bank rejects a Released payment: it ends REJECTED and the reserve is restored.
  const rejected = await payAndHold();
  await stubBank(request, billing, rejected.disbursementId, 'reject');
  const rejectedPayment = await waitForPayment(request, claims, claimId, rejected.payment['claimPaymentId'], 'REJECTED');
  expect(await open()).toBe(RESERVE);

  // ---- 4. Reissue the rejected payment: a new payment linked to the original goes through BIL to CLEARED.
  const reissued = await post('/api/clm/v1/payments/reissue', { paymentId: rejectedPayment['claimPaymentId'], reason: 'BANK_REJECTED' });
  expect(reissued.status, reissued.text).toBe(200);
  expect(reissued.body['originalPaymentId']).toBe(rejectedPayment['claimPaymentId']);
  const newId = reissued.body['newPaymentId'] as string;
  expect(newId).not.toBe(rejectedPayment['claimPaymentId']);
  const fresh = await eventually('reissued payment handed to BIL', async () => {
    const p = (await listPayments(request, claims, claimId)).find((x) => x['claimPaymentId'] === newId);
    return p?.['disbursementId'] ? p : undefined;
  });
  expect(fresh['reissueOf']).toBe(rejectedPayment['claimPaymentId']);
  await waitForDisbursement(request, billing, fresh['disbursementId'], 'Released');
  await stubBank(request, billing, fresh['disbursementId'], 'issue');
  await stubBank(request, billing, fresh['disbursementId'], 'clear');
  await waitForPayment(request, claims, claimId, newId, 'CLEARED');
  expect(await paid(), 'only the reissued payment stands').toBe(PAYMENT);
  expect(await open()).toBe(RESERVE - PAYMENT);

  // ---- 5. FIN reverses what was reversed: all journals balance and the clearing accounts net to zero; cash left once, for the reissue.
  // The set is complete when the cash of the reissued payment is in and the clearing accounts have netted (stable on a second read).
  const journals = await waitForJournals(request, finance, { claimId }, 'payment, reversal and BIL journals', (all) => {
    const l = linesOf(all);
    return net(l, 'GL-1110') === -PAYMENT && net(l, 'GL-2510') === 0n && net(l, 'GL-2530') === 0n && all.length > 0;
  });
  test.info().annotations.push({ type: 'journals', description: journals.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort().join(', ') });
  expectAllBalanced(journals);
  const lines = linesOf(journals);
  expect(net(lines, 'GL-2510'), 'claim payment clearing nets to zero').toBe(0n);
  expect(net(lines, 'GL-2530'), 'disbursements in transit net to zero').toBe(0n);
  expect(net(lines, 'GL-1110'), 'cash paid out only for the reissued payment').toBe(-PAYMENT);
});
