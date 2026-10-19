import { expect, test } from '@playwright/test';
import { call, cents, eur, eventually, money, signIn, sum, type Json } from './support/api.js';
import {
  addExposure, applySet, approvePending, buildSet, capturePayee, countOf, createAndApproveXolTreaty, createOrganisation, createPerson, expectAllBalanced,
  financials, getSet, IBAN, linesOf, net, paymentLine, readJournals, recoveryReserveLine, reserveLine, SL4_ENTRY_TYPES, sourceOf,
  stubBank, submitFnol, bindMotorPolicy, tokenFor, waitForDisbursement, waitForJournals, waitForPayment, type Line,
} from './support/claims4/index.js';

// E2E-02 extension (SLICE-PLAN-4 §3.1): FNOL with liability facts -> large-loss reserves -> RI per-risk XoL recoveries (+50k, +450k, -50k)
// -> payment through BIL -> subrogation (recovery reserve, BIL receivable, cash) and salvage -> FIN journals -> RI view on the claim.
// All amounts are the illustrative test data of the plan (GT-05, REQ-RI-119, REQ-CLM-145); runs on the stack of tests/e2e/run-e2e06.sh.

const K = 100n;
const BI_RESERVE_1 = 300_000n * K;
const BI_RESERVE_2 = 600_000n * K;
const BI_PAYMENT = 400_000n * K;
const SUBROGATION = 200_000n * K;
const SALVAGE_RESERVE = 1_500n * K;
const SALVAGE_PROCEEDS = 1_700n * K;

/** Recoverable of the claim per reinsurer, from ri.Recovery.listByClaim (latest rows). */
async function riView(request: Parameters<typeof call>[0], token: string, claimId: string) {
  const list = await call(request, token, 'GET', `/api/ri/v1/recoveries/list-by-claim?claimId=${claimId}`);
  expect(list.status, list.text).toBe(200);
  const rows = (list.body['items'] as Json[]).map((r) => ({
    participant: r['participantId'] as string,
    incurred: cents(r['recoverableIncurred']),
    paid: cents(r['recoverablePaid']),
    outstanding: cents(r['recoverableOutstanding']),
  }));
  return {
    rows,
    incurred: sum(rows.map((r) => r.incurred)),
    paid: sum(rows.map((r) => r.paid)),
    outstanding: sum(rows.map((r) => r.outstanding)),
  };
}

test('E2E-02 with recoveries: large loss, RI XoL recoveries, payment, subrogation and salvage through BIL, FIN journals', async ({ request }) => {
  test.setTimeout(600_000);
  const responses: string[] = [];

  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const recovery = await tokenFor(request, 'recovery');
  const billing = await tokenFor(request, 'billing');
  const finance = await tokenFor(request, 'finance');

  // ---- Setup: the policy, the counterparties and the illustrative treaty "EUR 500k xs 250k", 60/40, 100% placed.
  const policy = await test.step('bind a motor policy (MTPL and own damage) in force today', () => bindMotorPolicy(request, 'IKX-2002'));
  const admin = await signIn(request, 'underwriter'); // party creation
  const insurerX = await createOrganisation(request, admin, 'Synthetic Insurer X');
  const reinsurerA = await createOrganisation(request, admin, 'Synthetic Reinsurer A');
  const reinsurerB = await createOrganisation(request, admin, 'Synthetic Reinsurer B');
  const injured = await createPerson(request, admin, 'Ελένη', 'Παππά');
  // E2E_REUSE_TREATY=1 (local re-runs on a kept stack): a second Active treaty over the same scope would overlap with the first.
  if (!process.env['E2E_REUSE_TREATY']) await test.step('RI treaty is created by the accountant and approved by the manager (SoD)', () =>
    createAndApproveXolTreaty(request, {
      attachment: '250000.00', limit: '500000.00', productCodes: ['MOTOR-GR'], coverageCodes: ['MTPL'],
      participations: [{ reinsurerPartyId: reinsurerA, signedLinePct: '60' }, { reinsurerPartyId: reinsurerB, signedLinePct: '40' }],
    }));

  // ---- 1-3. FNOL with liability facts (0% insured fault, insurer X); an own-damage exposure and an MTPL_BI exposure for the injured party.
  const fnol = await test.step('FNOL with liability facts and the own-damage exposure', () =>
    submitFnol(request, claims, policy, {
      description: 'E2E-02 extension: at-fault third party, injured passenger (synthetic)',
      exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
      liabilityFacts: { insuredFaultPct: '0', faultSource: 'JOINT_REPORT', counterpartyInsurerPartyId: insurerX, jointReportFlag: false, vehicleCount: 2, accidentInGreece: true },
    }));
  const claimId = fnol.claimId;
  expect(fnol.claim['policyInForceAtLoss']).toBe(true);
  expect(fnol.claim['snapshotStatus'], 'coverage verified on the POL snapshot').toBe('VERIFIED');
  expect(fnol.liabilityFacts['counterpartyInsurerPartyId']).toBe(insurerX);
  expect(String(fnol.liabilityFacts['insuredFaultPct'])).toMatch(/^0(\.0+)?$/);
  const ownDamage = fnol.exposures.find((e) => e['kind'] === 'OWN_DAMAGE')!;
  const bi = await addExposure(request, claims, claimId, 'MTPL_BI', 'MTPL', injured);
  // The MTPL clocks are not tracked in slice 4 (D-SL4-09: statutory_clocks = NOT_TRACKED in the database; the exposure view has no field for it).
  expect(bi['coverageIndication']).toBe('COVERED');
  const payee = await capturePayee(request, claims, claimId, injured, 'Ελένη Παππά');

  // ---- 4-5. Reserve 300,000: referred to the claims manager (large loss, D-SL4-08); RI books +50,000.
  await test.step('BI reserve 300,000 is referred to the manager and approved', async () => {
    const built = await buildSet(request, claims, claimId, [reserveLine(bi['exposureId'], BI_RESERVE_1)]);
    const submitted = await call(request, claims, 'POST', '/api/clm/v1/transaction-sets/submit', { setId: built['setId'] });
    expect(submitted.status, submitted.text).toBe(200);
    expect(submitted.body['status']).toBe('PENDING_APPROVAL');
    expect((submitted.body['authorityChecks'] as Json[]).some((c) => c['decision'] === 'REFER')).toBe(true);
    await approvePending(request, manager, await getSet(request, claims, built['setId']));
    await eventually('reserve set APPROVED', async () => ((await getSet(request, claims, built['setId']))['status'] === 'APPROVED' ? true : undefined));
  });
  const pointA = await eventually('RI point a: incurred 50,000', async () => {
    const v = await riView(request, manager, claimId);
    return v.incurred === 50_000n * K ? v : undefined;
  });
  expect(pointA.paid).toBe(0n);
  expect(pointA.outstanding).toBe(50_000n * K);
  expect(pointA.rows.map((r) => r.incurred).sort()).toEqual([20_000n * K, 30_000n * K]); // 60/40

  // ---- Reserve +600,000 (900,000): RI +450,000.
  await applySet(request, claims, manager, claimId, [reserveLine(bi['exposureId'], BI_RESERVE_2, 'ESTIMATE_REVISED')]);
  const pointB = await eventually('RI point b: incurred 500,000', async () => {
    const v = await riView(request, manager, claimId);
    return v.incurred === 500_000n * K ? v : undefined;
  });
  expect(pointB.outstanding).toBe(500_000n * K);
  expect(pointB.rows.map((r) => r.incurred).sort()).toEqual([200_000n * K, 300_000n * K]);

  // ---- 8-10. Payment 400,000 to the injured party through BIL (manual stub bank), RI paid recoverable 150,000.
  const paymentSet = await applySet(request, claims, manager, claimId, [paymentLine(bi['exposureId'], BI_PAYMENT, injured, payee, 'PARTIAL')]);
  const submittedPayment = await eventually('claim payment handed to BIL', async () => {
    const list = await call(request, claims, 'GET', `/api/clm/v1/claims/${claimId}/payments`);
    const item = (list.body['items'] as Json[]).find((p) => p['setId'] === paymentSet['setId'] && p['disbursementId']);
    return item;
  });
  const disbursementId = submittedPayment['disbursementId'] as string;
  await waitForDisbursement(request, billing, disbursementId, 'Released');
  await stubBank(request, billing, disbursementId, 'issue');
  await stubBank(request, billing, disbursementId, 'clear');
  const payment = await waitForPayment(request, claims, claimId, submittedPayment['claimPaymentId'], 'CLEARED');
  expect(cents(payment['amount'])).toBe(BI_PAYMENT);
  expect(payment['maskedAccount']).toMatch(/0695$/);
  const pointC = await eventually('RI point c: paid 150,000', async () => {
    const v = await riView(request, manager, claimId);
    return v.paid === 150_000n * K ? v : undefined;
  });
  expect(pointC.incurred).toBe(500_000n * K);
  expect(pointC.outstanding).toBe(350_000n * K);
  // Settlement receipt as a fiscal document (stub): one per payment, MARK stored (D-SL4-11).
  const marked = await eventually('fiscal MARK of the settlement receipt', async () => {
    const list = await call(request, claims, 'GET', `/api/clm/v1/claims/${claimId}/payments`);
    const item = (list.body['items'] as Json[]).find((p) => p['claimPaymentId'] === payment['claimPaymentId']);
    return item?.['fiscalMark'] ? item : undefined;
  });
  expect(marked['fiscalMark']).toBeTruthy();

  // ---- 11. Subrogation against insurer X: recovery case, recovery reserve, demand (BIL receivable), cash, recorded from BIL's allocation.
  const subro = await test.step('subrogation case with a recovery reserve of 200,000', async () => {
    const created = await call(request, recovery, 'POST', '/api/clm/v1/recoveries', {
      claimId, exposureId: bi['exposureId'], type: 'SUBROGATION', counterpartyPartyId: insurerX, expectedAmount: money(SUBROGATION), reason: 'AT_FAULT_THIRD_PARTY',
    });
    expect(created.status, created.text).toBe(201);
    const view = created.body['recovery'] as Json;
    await applySet(request, claims, manager, claimId, [recoveryReserveLine(bi['exposureId'], view['recoveryId'], SUBROGATION)]);
    return view;
  });
  const demanded = await test.step('demand 200,000: a BIL receivable with a payment reference', async () => {
    const current = await call(request, recovery, 'GET', `/api/clm/v1/recoveries/${subro['recoveryId']}`);
    const demand = await call(request, recovery, 'POST', '/api/clm/v1/recoveries/demand', {
      recoveryId: subro['recoveryId'], expectedRecordVersion: current.body['recovery']['recordVersion'], amount: money(SUBROGATION), dueDate: '2099-12-31',
    });
    expect(demand.status, demand.text).toBe(200);
    expect(demand.body['paymentReference']).toBeTruthy();
    return demand.body;
  });
  await test.step('cash 200,000 arrives at BIL, matched by the payment reference', async () => {
    const take = await call(request, billing, 'POST', '/api/bil/v1/payments/take', {
      billingAccountId: demanded['billingAccountId'], amount: money(SUBROGATION), method: 'BANK_TRANSFER',
      paymentReference: demanded['paymentReference'], receivableId: demanded['receivableId'],
    });
    expect(take.status, take.text).toBeLessThan(300);
    expect(['ALLOCATED', 'RECEIVABLE_ALLOCATED']).toContain(take.body['allocationOutcome']);
  });
  await eventually('subrogation recovery recorded by CLM', async () => {
    const f = await financials(request, claims, claimId);
    return cents(f['totals']['recoveries']) === SUBROGATION ? f : undefined;
  });
  const pointD = await eventually('RI point d: incurred 450,000, paid 0', async () => {
    const v = await riView(request, manager, claimId);
    return v.incurred === 450_000n * K ? v : undefined;
  });
  expect(pointD.paid).toBe(0n);
  expect(pointD.outstanding).toBe(450_000n * K);

  // ---- Salvage on the own-damage exposure: reserve 1,500, sold for 1,700 -> recovery 1,700, open recovery reserve 0.
  await test.step('salvage reserve 1,500 sold for 1,700', async () => {
    const created = await call(request, recovery, 'POST', '/api/clm/v1/recoveries', {
      claimId, exposureId: ownDamage['exposureId'], type: 'SALVAGE', counterpartyPartyId: insurerX, expectedAmount: money(SALVAGE_RESERVE), reason: 'SALVAGE',
    });
    expect(created.status, created.text).toBe(201);
    const view = created.body['recovery'] as Json;
    await applySet(request, claims, manager, claimId, [recoveryReserveLine(ownDamage['exposureId'], view['recoveryId'], SALVAGE_RESERVE)]);
    const buyer = await createOrganisation(request, admin, 'Synthetic Salvage Buyer');
    const sold = await call(request, recovery, 'POST', '/api/clm/v1/recoveries/record-milestone', {
      recoveryId: view['recoveryId'], expectedRecordVersion: view['recordVersion'], milestone: 'SALVAGE_SOLD', occurredOn: new Date().toISOString().slice(0, 10),
      salvage: { buyerPartyId: buyer, salePrice: money(SALVAGE_PROCEEDS) },
    });
    expect(sold.status, sold.text).toBe(200);
    const demand = await call(request, recovery, 'POST', '/api/clm/v1/recoveries/demand', {
      recoveryId: view['recoveryId'], expectedRecordVersion: sold.body['recovery']['recordVersion'], amount: money(SALVAGE_PROCEEDS), dueDate: '2099-12-31',
    });
    expect(demand.status, demand.text).toBe(200);
    const take = await call(request, billing, 'POST', '/api/bil/v1/payments/take', {
      billingAccountId: demand.body['billingAccountId'], amount: money(SALVAGE_PROCEEDS), method: 'BANK_TRANSFER',
      paymentReference: demand.body['paymentReference'], receivableId: demand.body['receivableId'],
    });
    expect(take.status, take.text).toBeLessThan(300);
  });
  const finalFinancials = await eventually('both recoveries recorded', async () => {
    const f = await financials(request, claims, claimId);
    return cents(f['totals']['recoveries']) === SUBROGATION + SALVAGE_PROCEEDS ? f : undefined;
  });
  const totals = finalFinancials['totals'] as Json;
  expect(cents(totals['openRecoveryReserve']), 'no open recovery reserve once both are realised').toBe(0n);
  // Net incurred = incurred - recoveries - open recovery reserve (REQ-CLM-102).
  expect(cents(totals['netIncurred'])).toBe(cents(totals['incurred']) - (SUBROGATION + SALVAGE_PROCEEDS));

  // ---- 12-14. FIN: every journal balanced, claim and recovery clearing net to zero, RI recoverables posted.
  const journals = await waitForJournals(request, finance, { claimId }, 'claim, recovery and BIL journals', (all) =>
    countOf(all, SL4_ENTRY_TYPES.recoveryRecorded) >= 2 && countOf(all, SL4_ENTRY_TYPES.reserveChanged) >= 4 && countOf(all, SL4_ENTRY_TYPES.paymentIssued) >= 1
      && countOf(all, SL4_ENTRY_TYPES.riRecoveryCalculated) >= 4);
  expectAllBalanced(journals);
  const lines = linesOf(journals);
  const paymentId = payment['claimPaymentId'] as string;
  expect(net(lines, 'GL-2510'), 'claim payment clearing nets to zero').toBe(0n);
  expect(net(lines, 'GL-2510', (l: Line) => l.dimensions['claimPaymentId'] === paymentId), 'GL-2510 per payment').toBe(0n);
  expect(net(lines, 'GL-2530'), 'disbursements in transit net to zero').toBe(0n);
  expect(net(lines, 'GL-1340'), 'recovery clearing nets to zero (RecoveryRecorded vs BIL cash)').toBe(0n);
  test.info().annotations.push({
    type: 'fin',
    description: ['GL-5110', 'GL-2210', 'GL-1330', 'GL-1340', 'GL-1110', 'GL-4310', 'GL-4320'].map((a) => `${a} ${eur(net(lines, a))}`).join('; '),
  });

  const riJournals = journals.filter((j) => sourceOf(j) === SL4_ENTRY_TYPES.riRecoveryCalculated);
  expect(riJournals.length, 'RI recoverables posted for every calculation').toBeGreaterThanOrEqual(4);
  test.info().annotations.push({
    type: 'fin-ri',
    description: ['GL-1320', 'GL-4210'].map((a) => `${a} ${eur(net(lines, a))}`).join('; '),
  });

  // ---- RI trace and idempotence: the rows do not change when read again later.
  const settled = await riView(request, manager, claimId);
  expect(settled.incurred).toBe(450_000n * K);
  expect(settled.rows.map((r) => r.incurred).sort()).toEqual([180_000n * K, 270_000n * K]);
  const again = await riView(request, manager, claimId);
  expect(again.rows).toEqual(settled.rows);

  // ---- The IBAN never comes back.
  responses.push(JSON.stringify(await readJournals(request, finance, { claimId })));
  for (const text of responses) expect(text.replace(/\s/g, '')).not.toContain(IBAN);
});
