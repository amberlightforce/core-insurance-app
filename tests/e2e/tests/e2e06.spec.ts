import { expect, test, type APIRequestContext } from '@playwright/test';
import { call, cents, eur, eventually, money, signIn, sum, type Json } from './support/api.js';
import {
  applySet, approvePending, bindMotorPolicy, capturePayee, countOf, createOrganisation, expectAllBalanced, financials, IBAN, linesOf, listPayments,
  net, paymentLine, postFsNotification, readJournals, registerClearingPayeeAccount, reserveLine, stubBank, submitFnol, tokenFor, waitForDisbursement,
  waitForJournals, waitForPayment, type BoundPolicy,
} from './support/claims4/index.js';

// E2E-06 Friendly Settlement (SLICE-PLAN-4 §3.2, D1 FS_CLEARING): own leg (customer paid, FS recovery reserve) + at-fault leg from the clearing
// notification (CLEARING payment, no disbursement) -> statement import and match -> net approval -> BIL FS_CLEARING disbursement released by a
// different BillingManager -> bank -> cleared -> statement Settled -> FIN GL-2515 nets to zero. A second journey covers the net RECEIVABLE
// variant (7'). Amounts follow REQ-FIN-299: own leg 1,200, at-fault leg 3,000, net payable 1,800.

const K = 100n;
const STUB_FS_REFERENCE = /^STUB-FS-/;

/** The calendar month of "today" (Athens) as the statement period the stub store selects the legs by. */
function currentMonth(): { from: string; to: string; today: string } {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Athens' }).format(new Date());
  const [year, month] = today.split('-').map(Number) as [number, number];
  const next = month === 12 ? `${year + 1}-01-01` : `${year}-${String(month + 1).padStart(2, '0')}-01`;
  return { from: `${year}-${String(month).padStart(2, '0')}-01`, to: next, today };
}

interface Legs {
  insurer: string;
  ownClaimId: string;
  ownPaymentId: string;
  atFaultClaimId: string;
  statementPolicy: BoundPolicy;
}

/** A counterparty insurer is an organisation party; its code is its party id. Optionally with the verified CLEARING payee account. */
async function counterparty(request: APIRequestContext, withAccount: boolean): Promise<string> {
  const billing = await tokenFor(request, 'billing');
  const claims = await tokenFor(request, 'claims');
  const name = 'Synthetic Insurer Y';
  const party = await createOrganisation(request, await signIn(request, 'underwriter'), name);
  if (withAccount) {
    const read = await call(request, await signIn(request, 'underwriter'), 'GET', `/api/pty/v1/parties/${party}`);
    const legalName = (read.body['party']?.['organisation']?.['legalName'] ?? name) as string;
    const account = await call(request, billing, 'POST', '/api/bil/v1/payee-accounts', { partyId: party, purpose: 'CLEARING', iban: IBAN, holderName: legalName, source: 'STAFF' });
    expect(account.status, `clearing payee account: ${account.text}`).toBe(201);
    await registerClearingPayeeAccount(request, claims, party, account.body['payeeAccountId']);
  }
  return party;
}

/** Own leg and at-fault leg against `insurer`: own amount paid to the customer, at-fault amount notified by the clearing house. */
async function legs(request: APIRequestContext, insurer: string, ownAmount: bigint, atFaultAmount: bigint, plates: [string, string], payOwn: boolean): Promise<Legs> {
  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const billing = await tokenFor(request, 'billing');
  const [ownPolicy, atFaultPolicy] = await Promise.all([bindMotorPolicy(request, plates[0]), bindMotorPolicy(request, plates[1])]);

  // 1. FNOL with the joint-report liability facts (0% insured fault, insurer Y, 2 vehicles, in Greece).
  const own = await submitFnol(request, claims, ownPolicy, {
    description: 'E2E-06: own leg of a Friendly Settlement (synthetic)',
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
    liabilityFacts: { insuredFaultPct: '0', faultSource: 'JOINT_REPORT', counterpartyInsurerPartyId: insurer, jointReportFlag: true, vehicleCount: 2, accidentInGreece: true },
  });
  expect(own.liabilityFacts['vehicleCount']).toBe(2);
  expect(own.liabilityFacts['accidentInGreece']).toBe(true);
  const damage = own.exposures[0]!;
  await applySet(request, claims, manager, own.claimId, [reserveLine(damage['exposureId'], ownAmount)]);

  // 2. Eligibility (stub, UNVERIFIED, provisional), then the own settlement: FS exposure, FS recovery reserve at the clearing value.
  const evaluated = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/evaluate', { claimId: own.claimId });
  expect(evaluated.status, evaluated.text).toBe(200);
  const eligibility = evaluated.body['eligibility'] as Json;
  expect(eligibility['result']).toBe('ELIGIBLE');
  expect(eligibility['legalStatus']).toBe('UNVERIFIED');
  expect(eligibility['provisional']).toBe(true);
  expect(eligibility['ruleId'], 'rule id stored with the decision').toBeTruthy();
  expect(eligibility['clearingValueBasis']).toBe('ACTUAL');
  expect(cents(evaluated.body['fsCase']['clearingValue'])).toBe(ownAmount);
  const caseId = evaluated.body['fsCase']['fsCaseId'] as string;

  const opened = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/open-own-settlement', {
    fsCaseId: caseId, expectedRecordVersion: evaluated.body['fsCase']['recordVersion'],
  });
  expect(opened.status, opened.text).toBe(200);
  const ownExposure = opened.body['exposureId'] as string;
  expect(opened.body['recoveryId']).toBeTruthy();
  await eventually('FS recovery reserve of the clearing value', async () => {
    const f = await financials(request, claims, own.claimId);
    return cents(f['totals']['openRecoveryReserve']) === ownAmount ? f : undefined;
  });

  // 3. Pay the customer through the ordinary set builder and BIL; the expected gain/loss is the clearing value minus paid.
  const insured = ownPolicy.policyholderPartyId;
  let ownPaymentId = '';
  if (payOwn) {
    const payee = await capturePayee(request, claims, own.claimId, insured, 'Νίκος Αλεξίου');
    const set = await applySet(request, claims, manager, own.claimId, [paymentLine(ownExposure, ownAmount, insured, payee)]);
    const payment = await eventually('own-leg payment handed to BIL', async () =>
      (await listPayments(request, claims, own.claimId)).find((p) => p['setId'] === set['setId'] && p['disbursementId']));
    ownPaymentId = payment['claimPaymentId'] as string;
    await waitForDisbursement(request, billing, payment['disbursementId'], 'Released');
    await stubBank(request, billing, payment['disbursementId'], 'issue');
    await stubBank(request, billing, payment['disbursementId'], 'clear');
    await waitForPayment(request, claims, own.claimId, ownPaymentId, 'CLEARED');
    const got = await call(request, claims, 'GET', `/api/clm/v1/friendly-settlements/${caseId}`);
    expect(cents(got.body['fsCase']['expectedGainLoss']), 'expected gain/loss = clearing value - paid').toBe(0n);
  }

  // 4. Submit once: the clearing reference is stored.
  const reread = await call(request, claims, 'GET', `/api/clm/v1/friendly-settlements/${caseId}`);
  const submitted = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/submit', { fsCaseId: caseId, expectedRecordVersion: reread.body['fsCase']['recordVersion'] });
  expect(submitted.status, submitted.text).toBe(200);
  expect(submitted.body['clearingReference']).toMatch(STUB_FS_REFERENCE);
  expect(submitted.body['fsCase']['state']).toBe('SUBMITTED');

  // 5. At-fault leg: the clearing house notifies; CLM creates the claim on the policy with an MTPL_PD exposure and a CLEARING payment.
  const notification = {
    clearingReference: `STUB-FS-${Math.random().toString(36).slice(2, 14).toUpperCase()}`,
    policyNumber: atFaultPolicy.policyNumber,
    lossDate: utcToday(),
    counterpartyInsurerPartyId: insurer,
    amount: eur(atFaultAmount),
    currency: 'EUR',
    damageType: 'MATERIAL_DAMAGE',
  };
  const received = await postFsNotification(request, manager, notification);
  expect(received['fsCase']['role']).toBe('AT_FAULT_INSURER');
  const atFaultClaimId = received['claimId'] as string;
  if (received['setId']) {
    const set = await call(request, manager, 'GET', `/api/clm/v1/transaction-sets/${received['setId']}`);
    if ((set.body['set']['approvals'] as Json[] | undefined)?.some((a) => a['status'] === 'PENDING')) {
      await approvePending(request, manager, set.body['set']);
    }
  }
  const replay = await postFsNotification(request, manager, notification);
  expect(replay['replay'], 'the same clearing reference replays').toBe(true);
  expect(replay['claimId']).toBe(atFaultClaimId);
  const clearing = await eventually('CLEARING payment of the at-fault leg', async () => {
    const payments = await listPayments(request, claims, atFaultClaimId);
    return payments.length === 1 && payments[0]!['method'] === 'CLEARING' ? payments[0] : undefined;
  });
  expect(cents(clearing['amount'])).toBe(atFaultAmount);
  expect(clearing['disbursementId'] ?? null, 'a CLEARING payment never has a disbursement').toBeNull();
  const atFaultClaim = await call(request, claims, 'GET', `/api/clm/v1/claims/${atFaultClaimId}`);
  expect(JSON.stringify(atFaultClaim.body)).toContain('MTPL_PD');

  return { insurer, ownClaimId: own.claimId, ownPaymentId, atFaultClaimId, statementPolicy: ownPolicy };
}

/** A statement is the month's view over all counterparties; this narrows it to one: its lines and its net. */
function ofCounterparty(statement: Json, code: string): { lines: Json[]; net: Json } {
  return {
    lines: (statement['lines'] as Json[]).filter((l) => l['counterpartyCode'] === code),
    net: (statement['nets'] as Json[]).find((n) => n['counterpartyCode'] === code)!,
  };
}

async function importStatement(request: APIRequestContext, token: string, insurer: string): Promise<Json> {
  const period = currentMonth();
  const imported = await call(request, token, 'POST', '/api/clm/v1/friendly-settlements/import-statement', { period: { from: period.from, to: period.to }, counterpartyCode: insurer });
  expect(imported.status, imported.text).toBe(200);
  return imported.body['statement'] as Json;
}

test('E2E-06 Friendly Settlement: own and at-fault legs, statement, net payable through BIL FS_CLEARING, GL-2515 nets to zero', async ({ request }) => {
  test.setTimeout(600_000);
  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const billingClerk = await tokenFor(request, 'billing');
  const billingManager = await tokenFor(request, 'billingmgr');
  const finance = await tokenFor(request, 'finance');

  const insurerY = await counterparty(request, true);
  const result = await legs(request, insurerY, 1_200n * K, 3_000n * K, ['IKX-0601', 'IKY-0602'], true);

  // ---- 6. Statement imported and matched: both legs, net payable 1,800; re-import changes nothing.
  const statement = await importStatement(request, manager, insurerY);
  const mine = ofCounterparty(statement, insurerY);
  expect(mine.lines).toHaveLength(2);
  expect(mine.lines.every((l) => l['matchStatus'] === 'MATCHED')).toBe(true);
  expect(mine.lines.map((l) => l['direction']).sort()).toEqual(['PAYABLE', 'RECEIVABLE']);
  expect(mine.net['direction']).toBe('PAYABLE');
  expect(cents(mine.net['amount'])).toBe(1_800n * K);
  expect(mine.net['status']).toBe('PENDING');
  const statementId = statement['fsStatementId'] as string;
  const statementRef = mine.net['statementRef'] as string; // the reference BIL and FIN carry: the counterparty's statement row
  const again = await importStatement(request, manager, insurerY);
  expect(ofCounterparty(again, insurerY).lines, 're-importing changes nothing').toHaveLength(2);
  expect(again['fsStatementId']).toBe(statementId);

  // The FS recovery of the own leg is recorded from the matched line (not from cash); the CLEARING payable is issued.
  await eventually('CLEARING payment issued and FS recovery recorded', async () => {
    const payments = await listPayments(request, claims, result.atFaultClaimId);
    const f = await financials(request, claims, result.ownClaimId);
    return payments[0]!['status'] === 'ISSUED' && cents(f['totals']['recoveries']) === 1_200n * K ? true : undefined;
  });

  // ---- 7. Net approval (a stale net is refused), then BIL: one FS_CLEARING disbursement of 1,800 with a mandatory release approval.
  const stale = await call(request, manager, 'POST', '/api/clm/v1/friendly-settlements/approve-net', {
    fsStatementId: statementId, counterpartyCode: insurerY, expectedRecordVersion: again['recordVersion'], expectedNet: money(1_700n * K),
  });
  expect(stale.status, 'a net that is not the computed one is stale').toBe(409);
  const approved = await call(request, manager, 'POST', '/api/clm/v1/friendly-settlements/approve-net', {
    fsStatementId: statementId, counterpartyCode: insurerY, expectedRecordVersion: again['recordVersion'], expectedNet: money(1_800n * K),
  });
  expect(approved.status, approved.text).toBe(200);
  expect(approved.body['outcome']).toBe('REQUESTED');
  expect(approved.body['net']['bilReferenceType']).toBe('DISBURSEMENT');
  const disbursementId = approved.body['net']['bilReferenceId'] as string;

  const requested = await waitForDisbursement(request, billingManager, disbursementId, 'PendingApproval');
  expect(requested['sourceType']).toBe('FS_CLEARING');
  expect(requested['method']).toBe('CLEARING');
  expect(requested['statementReference']).toBe(statementRef);
  expect(cents(requested['amount'])).toBe(1_800n * K);
  expect(sum((requested['lines'] as Json[]).map((l) => cents(l['amount']))), 'lines sum to the net').toBe(1_800n * K);

  // The release needs a Staff.BillingManager other than the requester: the clerk cannot, the manager can.
  const clerk = await call(request, billingClerk, 'POST', '/api/bil/v1/disbursements/approve-release', { disbursementId, decision: 'APPROVE' });
  expect(clerk.status, 'a billing clerk may not release').toBe(403);
  const released = await call(request, billingManager, 'POST', '/api/bil/v1/disbursements/approve-release', { disbursementId, decision: 'APPROVE' });
  expect(released.status, released.text).toBe(200);
  await waitForDisbursement(request, billingManager, disbursementId, 'Released');
  await stubBank(request, billingManager, disbursementId, 'issue');
  await stubBank(request, billingManager, disbursementId, 'clear');

  // ---- Cleared: the net is Settled, the cases reconciled, the CLEARING payment Cleared.
  await eventually('statement net SETTLED', async () => {
    const read = await call(request, manager, 'GET', `/api/clm/v1/friendly-settlements/statements/${statementId}`);
    return ofCounterparty(read.body['statement'], insurerY).net['status'] === 'SETTLED' ? read.body : undefined;
  });
  await waitForPayment(request, claims, result.atFaultClaimId, (await listPayments(request, claims, result.atFaultClaimId))[0]!['claimPaymentId'], 'CLEARED');
  const cases = await call(request, claims, 'GET', `/api/clm/v1/friendly-settlements?claimId=${result.ownClaimId}`);
  expect((cases.body['items'] as Json[]).map((c) => c['state'])).toEqual(['RECONCILED']);

  // ---- 8. FIN: GL-2515 nets to zero for (insurer Y, statement), no per-payment GL-2510 break for method CLEARING, cash only from BIL.
  // The net's BIL entries carry the disbursement (statement reference and counterparty), not the claim: pick them by disbursement.
  const claimJournals = [
    ...(await waitForJournals(request, finance, { claimId: result.atFaultClaimId }, 'at-fault leg journals', (all) => countOf(all, 'CLM:PaymentIssued') >= 1)),
    ...(await waitForJournals(request, finance, { claimId: result.ownClaimId }, 'own leg journals', (all) =>
      countOf(all, 'CLM:RecoveryRecorded') >= 1 && countOf(all, 'CLM:PaymentIssued') >= 1 && countOf(all, 'BIL:BillingEntryPosted') >= 2)),
  ];
  const netJournals = await eventually('BIL FS_CLEARING entries (released, cleared) in FIN', async () => {
    const mine = (await readJournals(request, finance, { query: 'sourceEventType=BillingEntryPosted' })).filter((j) =>
      (j['lines'] as Json[]).some((l) => (l['dimensions'] ?? {})['disbursementId'] === disbursementId));
    return mine.length >= 2 ? mine : undefined;
  });
  const unique = [...new Map([...claimJournals, ...netJournals].map((j) => [j['journalNumber'] as string, j])).values()];
  expectAllBalanced(unique);
  const fin = linesOf(unique);
  test.info().annotations.push({ type: 'journals', description: unique.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort().join(', ') });
  expect(fin.some((l) => l.account === 'GL-2515'), 'GL-2515 was posted').toBe(true);
  expect(net(fin, 'GL-2515'), 'FS clearing nets to zero per counterparty and statement').toBe(0n);
  expect(net(fin, 'GL-2530'), 'disbursements in transit net to zero').toBe(0n);
  expect(net(fin, 'GL-2510'), 'claim payment clearing nets to zero (own leg only)').toBe(0n);
  expect(net(fin, 'GL-1110'), 'cash: the customer payment and the net, both from BIL').toBe(-(1_200n + 1_800n) * K);
  const clearingPaymentId = (await listPayments(request, claims, result.atFaultClaimId))[0]!['claimPaymentId'];
  expect(fin.filter((l) => l.account === 'GL-2510' && l.dimensions['claimPaymentId'] === clearingPaymentId), 'no GL-2510 for method CLEARING').toHaveLength(0);

  // ---- The IBAN never comes back.
  expect(JSON.stringify(await readJournals(request, finance, { claimId: result.ownClaimId })).replace(/\s/g, '')).not.toContain(IBAN);
});

test('E2E-06 variant 7: net receivable registered at BIL, cash matched by the statement reference, statement Settled, GL-2515 nets to zero', async ({ request }) => {
  test.setTimeout(600_000);
  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const billing = await tokenFor(request, 'billing');
  const finance = await tokenFor(request, 'finance');

  const insurerZ = await counterparty(request, false);
  // Own leg 3,000 (below the stub's 5,000 limit) and at-fault leg 1,200: the clearing house owes us 1,800.
  const result = await legs(request, insurerZ, 3_000n * K, 1_200n * K, ['IKZ-0701', 'IKZ-0702'], false);

  const statement = await importStatement(request, manager, insurerZ);
  const mine = ofCounterparty(statement, insurerZ);
  expect(mine.lines.every((l) => l['matchStatus'] === 'MATCHED')).toBe(true);
  expect(mine.net['direction']).toBe('RECEIVABLE');
  expect(cents(mine.net['amount'])).toBe(1_800n * K);
  const statementId = statement['fsStatementId'] as string;

  const approved = await call(request, manager, 'POST', '/api/clm/v1/friendly-settlements/approve-net', {
    fsStatementId: statementId, counterpartyCode: insurerZ, expectedRecordVersion: statement['recordVersion'], expectedNet: money(1_800n * K),
  });
  expect(approved.status, approved.text).toBe(200);
  expect(approved.body['net']['bilReferenceType']).toBe('RECEIVABLE');
  const receivableId = approved.body['net']['bilReferenceId'] as string;

  const receivable = await call(request, billing, 'GET', `/api/bil/v1/receivables/${receivableId}`);
  expect(receivable.status, receivable.text).toBe(200);
  const view = receivable.body['receivable'] as Json;
  expect(view['sourceType']).toBe('FS_CLEARING');
  expect(view['statementRef']).toBe(mine.net['statementRef']);
  expect(cents(view['amount'])).toBe(1_800n * K);

  // The clearing house pays: matched to the receivable by the statement's payment reference.
  const take = await call(request, billing, 'POST', '/api/bil/v1/payments/take', {
    billingAccountId: view['billingAccountId'], amount: money(1_800n * K), method: 'BANK_TRANSFER', paymentReference: view['paymentReference'], receivableId,
  });
  expect(take.status, take.text).toBeLessThan(300);
  expect(['ALLOCATED', 'RECEIVABLE_ALLOCATED']).toContain(take.body['allocationOutcome']);
  await eventually('statement net SETTLED by CashAllocated', async () => {
    const read = await call(request, manager, 'GET', `/api/clm/v1/friendly-settlements/statements/${statementId}`);
    return ofCounterparty(read.body['statement'], insurerZ).net['status'] === 'SETTLED' ? read.body : undefined;
  });

  const journals = [
    ...(await waitForJournals(request, finance, { claimId: result.atFaultClaimId }, 'at-fault leg journals', (all) => countOf(all, 'CLM:PaymentIssued') >= 1)),
    ...(await waitForJournals(request, finance, { claimId: result.ownClaimId }, 'own leg journals', (all) => countOf(all, 'CLM:RecoveryRecorded') >= 1)),
  ];
  // The collected cash is a BIL RECEIVABLE_COLLECTED entry on the receivable's billing account, posted against GL-2515.
  const collected = await eventually('BIL receivable collection in FIN', async () => {
    const mine = (await readJournals(request, finance, { query: 'sourceEventType=BillingEntryPosted' })).filter((j) =>
      (j['lines'] as Json[]).some((l) => l['account'] === 'GL-2515' && (l['dimensions'] ?? {})['billingAccountId'] === view['billingAccountId']));
    return mine.length >= 1 ? mine : undefined;
  });
  const unique = [...new Map([...journals, ...collected].map((j) => [j['journalNumber'] as string, j])).values()];
  expectAllBalanced(unique);
  expect(net(linesOf(unique), 'GL-2515'), 'FS clearing nets to zero for the statement').toBe(0n);
});

// The loss date of the notification: today (UTC), which the handler dates just before now (inside a policy bound a moment ago).
function utcToday(): string {
  return new Date().toISOString().slice(0, 10);
}
