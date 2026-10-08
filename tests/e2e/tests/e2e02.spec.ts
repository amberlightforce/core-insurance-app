import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test, type APIRequestContext } from '@playwright/test';
import { call as rawCall, cents, eur, eventually, money, signIn, sum, type Json } from './support/api.js';

// E2E-02a claims happy path at API level (SLICE-PLAN-2; D-SL2-01): policy in force -> FNOL -> coverage verified on the POL
// snapshot -> own-damage exposure -> payee account -> reserves (within and above the handler's authority, four-eyes) -> final
// payment through BIL -> CLEARED -> FIN journals -> close. Runs against a fresh stack (tests/e2e/run-e2e02.sh).
//
// Authority limits are the illustrative test data of D-SL2-03: the handler decides up to EUR 5,000.00, above that the set is
// referred to the claims manager. All amounts are asserted in integer cents.

const PRODUCT = 'MOTOR-GR';
const SEED = process.env['E2E_PRODUCT_SEED'] ?? resolve(import.meta.dirname, '../../../src/CoreIns.Modules.Product/Seed/motor-gr.product.json');
const IBAN = 'GR1601101250000000012300695';
const RESERVE_1 = 120_000n; // 1,200.00 -> inside the handler's authority
const RESERVE_2 = 530_000n; // +5,300.00 -> exposure reserve 6,500.00 > 5,000.00 -> referred
const PAYMENT = 620_000n; // 6,200.00 FINAL; the 300.00 left on the reserve is released in the same set
// The claim's journals by source: three reserve changes (+1,200.00, +5,300.00, the 300.00 release), the payment, and BIL's
// two disbursement entries (released, cleared).
const EXPECTED_SOURCES = ['BIL:BillingEntryPosted', 'BIL:BillingEntryPosted', 'CLM:PaymentIssued', 'CLM:ReserveChanged', 'CLM:ReserveChanged', 'CLM:ReserveChanged'];
const RELEASE = -30_000n;

test('E2E-02a claims happy path: FNOL, reserves, four-eyes approval, final payment via BIL, FIN journals, close', async ({ request }) => {
  test.setTimeout(300_000);
  const seed = JSON.parse(readFileSync(SEED, 'utf8')) as Json;

  // Every response body is kept to prove that the IBAN never comes back (REQ-CLM-123, R-38).
  const responses: string[] = [];
  const call = async (token: string, method: 'GET' | 'POST', path: string, body?: unknown) => {
    const result = await rawCall(request as APIRequestContext, token, method, path, body);
    responses.push(result.text);
    return result;
  };

  const admin = await signIn(request, 'admin');
  const underwriter = await signIn(request, 'underwriter');
  const claims = await signIn(request, 'claims');
  const manager = await signIn(request, 'claimsmgr');
  const finance = await signIn(request, 'finance');
  const billing = await signIn(request, 'billing');

  // ---- 1. Setup through the APIs: product, customer, quote, bind. The term starts within seconds (a policy cannot start in the
  // past, REQ-POL-137) and the loss is dated inside it, so the policy is in force at the loss date.
  await test.step('import and lock the MOTOR-GR product version', async () => {
    const imported = await call(admin, 'POST', '/api/pfc/v1/product-versions/import', { definition: seed, lock: true });
    expect(imported.status, imported.text).toBeLessThan(300);
  });

  const party = await test.step('create the policyholder', async () => {
    const created = await call(underwriter, 'POST', '/api/pty/v1/parties', {
      partyType: 'PERSON',
      person: { givenNames: 'Νίκος', familyName: 'Αλεξίου', fatherName: 'Γεώργιος', birthDate: '1975-02-11' },
      identifiers: [],
      addresses: [{ types: ['LEGAL', 'MAILING'], country: 'GR', street: 'Λεωφ. Κηφισίας', number: '124', postcode: '11526', locality: 'Αθήνα' }],
      contactPoints: [{ type: 'EMAIL', value: 'e2e02.person@example.org', primary: true }],
      reason: 'NEW_CUSTOMER',
    });
    expect(created.status, created.text).toBe(201);
    return created.body['party']['partyId'] as string;
  });

  const start = new Date(Date.now() + 20_000);
  start.setUTCMilliseconds(0);
  const effectiveAt = start.toISOString().replace('.000Z', 'Z');
  const bound = await test.step('quote and bind a motor policy with own damage that is in force today', async () => {
    const created = await call(underwriter, 'POST', '/api/pol/v1/submissions', { policyholderPartyId: party, product: PRODUCT, channel: 'STAFF', effectiveAt, quoteType: 'FULL' });
    expect(created.status, created.text).toBe(201);
    const jobId = created.body['jobId'] as string;
    const first = await call(underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
      jobId,
      versionNo: 1,
      expectedDraftVersion: 0,
      instructions: [
        { op: 'SET_VEHICLE', vehicle: { plate: 'ikx-1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: '15000.00', currency: 'EUR' } } },
        { op: 'SET_ANSWERS', questionSet: { questionSetCode: 'MOTOR-RISK', questionSetVersion: '1', answers: { 'Q-USAGE': 'PRIVATE', 'Q-HIRE-REWARD': 'NO' } } },
      ],
    });
    expect(first.status, first.text).toBe(200);
    const vehicle = first.body['riskTree']['vehicles'][0]['locator'] as string;
    const second = await call(underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
      jobId,
      versionNo: 1,
      expectedDraftVersion: 1,
      instructions: [
        { op: 'SET_DRIVER', driver: { partyId: party, driverType: 'MAIN', yearFirstLicensed: 2008, vehicleLocator: vehicle, usagePercent: 100, claimsLast5Years: 0 } },
        { op: 'SET_COVERAGES', coverages: [
          { coverageCode: 'MTPL', elementLocator: vehicle, selected: true },
          { coverageCode: 'OWN-DAMAGE', elementLocator: vehicle, selected: true },
        ] },
      ],
    });
    expect(second.status, second.text).toBe(200);
    const quoted = await call(underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
    expect(quoted.status, quoted.text).toBe(200);
    expect(quoted.body['bindable']).toBe(true);
    const bind = await call(underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
    expect(bind.status, bind.text).toBe(200);
    return bind.body;
  });
  const policyId = bound['policyId'] as string;
  const policyNumber = bound['policyNumber'] as string;
  expect(policyNumber).toMatch(/^POL\d+$/);

  // The loss is dated one second into the term; wait until that instant is in the past on the server too.
  const lossAt = new Date(start.getTime() + 1000).toISOString().replace('.000Z', 'Z');
  while (Date.now() < start.getTime() + 4000) await new Promise((done) => setTimeout(done, 500));

  // ---- 2. FNOL as the claims handler: coverage verified on the POL snapshot, own-damage exposure for the insured.
  const fnol = await test.step('FNOL (staff channel) with an own-damage exposure', async () => {
    const submitted = await call(claims, 'POST', '/api/clm/v1/fnol/submit', {
      lineOfBusiness: 'MOTOR',
      policyId,
      policyNumber,
      lossAt,
      lossCause: 'COLLISION',
      lossLocation: 'Λεωφ. Κηφισίας 124, Αθήνα',
      description: 'E2E-02a: collision with a parked car (synthetic)',
      channel: 'STAFF',
      receiptMedium: 'TELEPHONE',
      incidents: [{ incidentType: 'VEHICLE', vehicleRef: 'IKX1234', drivable: true, damageAreas: ['FRONT'] }],
      exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
    });
    expect(submitted.status, submitted.text).toBe(200);
    return submitted.body;
  });
  const claimId = fnol['claimId'] as string;
  const claimNumber = fnol['claimNumber'] as string;
  const claim = fnol['claim'] as Json;
  expect(claimNumber, 'gapless claim number (D-SL2-07)').toMatch(/^[A-Z]*\d+$/);
  expect(claim['status']).toBe('OPEN');
  expect(claim['policyNumber']).toBe(policyNumber);
  expect(claim['policyInForceAtLoss'], 'the term includes the loss date').toBe(true);
  expect(claim['coverageInQuestion']).toBe(false);
  expect(claim['snapshotStatus'], 'coverage verified on the POL snapshot').toBe('VERIFIED');
  expect(claim['snapshotRef'], 'the snapshot the coverage was verified on').toBeTruthy();
  const insured = claim['insuredPartyId'] as string;
  expect(insured).toBe(party);
  const exposures = fnol['exposures'] as Json[];
  expect(exposures).toHaveLength(1);
  const exposureId = exposures[0]!['exposureId'] as string;
  expect(exposures[0]!['kind']).toBe('OWN_DAMAGE');
  expect(exposures[0]!['coverageCode']).toBe('OWN-DAMAGE');
  expect(exposures[0]!['coverageIndication']).toBe('COVERED');
  expect(exposures[0]!['claimantPartyId'], 'the claimant is the insured').toBe(insured);
  expect(fnol['coverageIndications']).toContainEqual({ coverageCode: 'OWN-DAMAGE', indication: 'COVERED' });

  // ---- 3. Payee account of the insured (the IBAN goes to BIL only).
  const payeeAccountId = await test.step('capture the payee account of the insured', async () => {
    const captured = await call(claims, 'POST', '/api/clm/v1/payee-accounts/capture', { claimId, partyId: insured, iban: IBAN, holderName: 'Νίκος Αλεξίου' });
    expect(captured.status, captured.text).toBe(201);
    const view = captured.body['payeeAccount'] as Json;
    expect(view['maskedIban']).toMatch(/0695$/);
    expect(view['maskedIban']).not.toBe(IBAN);
    return view['payeeAccountId'] as string;
  });

  const reserveLine = (amount: bigint, reason = 'INITIAL_ESTIMATE') => ({
    kind: 'RESERVE', exposureId, costType: 'INDEMNITY', costCategory: 'VEHICLE_REPAIR', amount: money(amount), reason,
  });
  const build = async (transactions: unknown[]) => {
    const built = await call(claims, 'POST', '/api/clm/v1/transaction-sets/build', { claimId, transactions });
    expect(built.status, built.text).toBe(200);
    return built.body;
  };
  const submit = async (setId: string) => {
    const submitted = await call(claims, 'POST', '/api/clm/v1/transaction-sets/submit', { setId });
    expect(submitted.status, submitted.text).toBe(200);
    return submitted.body;
  };
  const getSet = async (token: string, setId: string) => (await call(token, 'GET', `/api/clm/v1/transaction-sets/${setId}`)).body['set'] as Json;

  // ---- 4. RESERVE 1,200.00 (VEHICLE_REPAIR): within the handler's authority, approved at submit.
  await test.step('reserve 1,200.00 is approved at submit', async () => {
    const built = await build([reserveLine(RESERVE_1)]);
    expect(built['status']).toBe('DRAFT');
    const submitted = await submit(built['setId']);
    expect(submitted['status']).toBe('APPROVED');
    expect((submitted['authorityChecks'] as Json[]).every((c) => c['decision'] === 'ALLOW')).toBe(true);
  });

  // ---- 5. RESERVE +5,300.00: the exposure reserve becomes 6,500.00, above the handler's limit -> referred.
  const reserveSet = await test.step('reserve +5,300.00 is referred (PENDING_APPROVAL)', async () => {
    const built = await build([reserveLine(RESERVE_2, 'ESTIMATE_REVISED')]);
    const submitted = await submit(built['setId']);
    expect(submitted['status']).toBe('PENDING_APPROVAL');
    expect((submitted['authorityChecks'] as Json[]).some((c) => c['decision'] === 'REFER')).toBe(true);
    const view = await getSet(claims, built['setId']);
    expect(view['status']).toBe('PENDING_APPROVAL');
    expect(view['maker']).toBeTruthy();
    return view;
  });

  /** The PLT requests of a set still pending: id and the payload hash the checker reviewed. */
  const pendingApprovals = async (setView: Json) => {
    const pending = (setView['approvals'] as Json[]).filter((a) => a['status'] === 'PENDING');
    expect(pending.length).toBeGreaterThan(0);
    return Promise.all(
      pending.map(async (a) => {
        const read = await call(manager, 'GET', `/api/plt/v1/approval/${a['approvalRequestId']}`);
        expect(read.status, read.text).toBe(200);
        const request = (read.body['request'] ?? read.body) as Json;
        expect(request['payloadHash']).toMatch(/^[0-9a-f]{64}$/);
        return { requestId: a['approvalRequestId'] as string, payloadHash: request['payloadHash'] as string };
      }),
    );
  };

  await test.step('the handler cannot approve their own set (maker-checker)', async () => {
    const [first] = await pendingApprovals(reserveSet);
    const attempt = await call(claims, 'POST', '/api/plt/v1/approval/decide', { requestId: first!.requestId, decision: 'Approve', payloadHash: first!.payloadHash });
    expect(attempt.status, attempt.text).toBe(403);
    expect(attempt.body['code'] ?? attempt.body['type'] ?? attempt.text).toMatch(/SELF-APPROVAL|EDITOR/);
    expect((await getSet(claims, reserveSet['setId'])).status).toBe('PENDING_APPROVAL');
  });

  // ---- 6. The claims manager approves in the PLT inbox; CLM applies the decision from ApprovalDecided.
  const approveAll = async (setView: Json) => {
    for (const approval of await pendingApprovals(setView)) {
      const decided = await call(manager, 'POST', '/api/plt/v1/approval/decide', { requestId: approval.requestId, decision: 'Approve', payloadHash: approval.payloadHash });
      expect(decided.status, decided.text).toBe(200);
    }
  };
  await test.step('the claims manager approves; the set becomes APPROVED', async () => {
    await approveAll(reserveSet);
    await eventually('the reserve set to be APPROVED', async () => ((await getSet(claims, reserveSet['setId']))['status'] === 'APPROVED' ? true : undefined));
  });

  const afterReserves = (await call(claims, 'GET', `/api/clm/v1/financials/get?claim=${claimId}`)).body;
  expect(cents(afterReserves['totals']['openReserve'])).toBe(RESERVE_1 + RESERVE_2);
  expect(cents(afterReserves['totals']['paid'])).toBe(0n);

  // ---- 7. FINAL payment 6,200.00 to the payee account: referred; the 300.00 left on the reserve is released in the same set.
  const paymentSet = await test.step('final payment 6,200.00 is referred, with the release of 300.00 proposed', async () => {
    const built = await build([
      { kind: 'PAYMENT', exposureId, costType: 'INDEMNITY', costCategory: 'VEHICLE_REPAIR', amount: money(PAYMENT), payeePartyId: insured, payeeAccountId, paymentType: 'FINAL' },
    ]);
    const transactions = (built['set']['transactions'] as Json[]).map((t) => ({ kind: t['kind'], amount: cents(t['amount']), proposed: t['proposed'] === true }));
    expect(transactions).toContainEqual({ kind: 'PAYMENT', amount: PAYMENT, proposed: false });
    expect(transactions.filter((t) => t.proposed).map((t) => t.amount), 'the auto release of the remaining reserve (REQ-CLM-099)').toEqual([RELEASE]);
    const submitted = await submit(built['setId']);
    expect(submitted['status']).toBe('PENDING_APPROVAL');
    const view = await getSet(claims, built['setId']);
    expect(view['status']).toBe('PENDING_APPROVAL');
    return view;
  });
  await test.step('the claims manager approves the payment; BIL pays it to CLEARED', async () => {
    await approveAll(paymentSet);
  });
  const payment = await eventually('the claim payment to be CLEARED', async () => {
    const list = await call(claims, 'GET', `/api/clm/v1/claims/${claimId}/payments`);
    expect(list.status, list.text).toBe(200);
    const items = list.body['items'] as Json[];
    return items.length === 1 && items[0]!['status'] === 'CLEARED' ? items[0]! : undefined;
  });
  expect(cents(payment['amount'])).toBe(PAYMENT);
  expect(payment['payeeAccountId']).toBe(payeeAccountId);
  expect(payment['paymentType']).toBe('FINAL');
  expect(payment['maskedAccount']).toMatch(/0695$/);
  const claimPaymentId = payment['claimPaymentId'] as string;
  expect(['APPROVED', 'POSTED']).toContain((await getSet(claims, paymentSet['setId']))['status']);

  // ---- 8. Derived balances in integer cents.
  const financials = (await call(claims, 'GET', `/api/clm/v1/financials/get?claim=${claimId}`)).body;
  const totals = financials['totals'] as Json;
  expect(cents(totals['openReserve']), 'open reserve').toBe(0n);
  expect(cents(totals['paid']), 'paid').toBe(PAYMENT);
  expect(cents(totals['incurred']), 'incurred').toBe(PAYMENT);
  expect(cents(totals['reserved']), 'reserved: 1,200.00 + 5,300.00 - 300.00 released').toBe(RESERVE_1 + RESERVE_2 + RELEASE);
  const lineBalances = financials['balancesByLine'] as Json[];
  expect(lineBalances).toHaveLength(1);
  expect(lineBalances[0]!['final']).toBe(true);

  // ---- 9. BIL disbursement: CLEARED, sourced by the claim payment (D-SL2-12b).
  const disbursement = await test.step('the BIL disbursement is CLEARED with the claim payment as source', async () => {
    const read = await call(billing, 'GET', `/api/bil/v1/disbursements/${payment['disbursementId']}`);
    expect(read.status, read.text).toBe(200);
    return read.body['disbursement'] as Json;
  });
  expect(String(disbursement['status']).toUpperCase(), 'BIL names the state in PascalCase (Cleared)').toBe('CLEARED');
  expect(disbursement['sourceId']).toBe(claimPaymentId);
  expect(disbursement['sourceType']).toBe('CLM_CLAIM_PAYMENT');
  expect(disbursement['claimId']).toBe(claimId);
  expect(cents(disbursement['amount'])).toBe(PAYMENT);
  expect(disbursement['maskedPayeeAccount']).toMatch(/0695$/);

  // ---- 10. FIN journals of the claim: every journal balanced, the clearing accounts net to zero.
  const journals = await eventually('FIN to post the claim journals (reserves, payment, disbursement cash)', async () => {
    const all: Json[] = [];
    let cursor: string | undefined;
    do {
      const page = await call(finance, 'GET', `/api/fin/v1/journals/query?claimId=${claimId}&limit=100${cursor ? `&cursor=${encodeURIComponent(cursor)}` : ''}`);
      expect(page.status, page.text).toBe(200);
      all.push(...(page.body['items'] as Json[]).map((i) => i['journal'] as Json));
      cursor = page.body['nextCursor'] ?? undefined;
    } while (cursor);
    // FIN posts each source fact asynchronously (Claim and Disbursement aggregates independently), so accounts alone can
    // appear before every journal is in: wait for exactly the expected six journals by source before reading balances.
    const sources = all.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort();
    return JSON.stringify(sources) === JSON.stringify(EXPECTED_SOURCES) ? all : undefined;
  });
  const lines = journals.flatMap((j) =>
    (j['lines'] as Json[]).map((l) => ({
      journal: j['journalNumber'] as string,
      account: l['account'] as string,
      side: l['side'] as string,
      amount: cents(l['amount']),
      dimensions: (l['dimensions'] ?? {}) as Json,
    })),
  );
  // Three reserve journals (+1,200.00, +5,300.00, the 300.00 release), the payment, and BIL's two disbursement entries.
  const bySource = journals.map((j) => `${j['sourceModule']}:${j['sourceEventType']}`).sort();
  expect(bySource).toEqual(EXPECTED_SOURCES);
  for (const journal of journals) {
    const own = lines.filter((l) => l.journal === journal['journalNumber']);
    expect(own.length, `${journal['journalNumber']} has at least two lines`).toBeGreaterThanOrEqual(2);
    expect(sum(own.filter((l) => l.side === 'DEBIT').map((l) => l.amount)), `${journal['journalNumber']} balances`).toBe(sum(own.filter((l) => l.side === 'CREDIT').map((l) => l.amount)));
  }
  const signed = (l: (typeof lines)[number]) => (l.side === 'DEBIT' ? l.amount : -l.amount);
  const net = (account: string, only: (l: (typeof lines)[number]) => boolean = () => true): bigint => sum(lines.filter((l) => l.account === account && only(l)).map(signed));
  test.info().annotations.push({ type: 'journal-rules', description: [...new Set(journals.flatMap((j) => j['ruleCodes'] as string[]))].sort().join(', ') });
  expect(net('GL-5110'), 'incurred claims expense = paid (reserves released), debit net').toBe(PAYMENT);
  expect(net('GL-2210'), 'LIC case reserve nets to zero once paid and released').toBe(0n);
  const ofPayment = (l: (typeof lines)[number]) => l.dimensions['claimPaymentId'] === claimPaymentId;
  expect(lines.filter((l) => l.account === 'GL-2510' && ofPayment(l)), 'CLM credits and BIL debits the clearing account of the payment').toHaveLength(2);
  expect(net('GL-2510', ofPayment), 'claim payment clearing nets to zero for the payment (D-SL2-08)').toBe(0n);
  expect(net('GL-2510'), 'claim payment clearing nets to zero').toBe(0n);
  expect(net('GL-2530'), 'disbursements in transit net to zero once cleared').toBe(0n);
  expect(net('GL-1110'), 'cash paid out').toBe(-PAYMENT);
  expect(sum(lines.map(signed)), 'the claim ledger balances').toBe(0n);

  // ---- 11. Close as COMPLETED.
  const closed = await test.step('close the claim as COMPLETED', async () => {
    const current = (await call(claims, 'GET', `/api/clm/v1/claims/${claimId}`)).body;
    const version = current['claim']['summary']['recordVersion'] as number;
    const result = await call(claims, 'POST', '/api/clm/v1/claims/close', { claimId, expectedRecordVersion: version, outcome: 'COMPLETED' });
    expect(result.status, result.text).toBe(200);
    return (result.body['claim'] ?? result.body) as Json;
  });
  expect(closed['status']).toBe('CLOSED');
  expect(closed['outcome']).toBe('COMPLETED');

  // ---- 12. The IBAN never comes back in a response; only the masked form does.
  for (const text of responses) {
    expect(text.replace(/\s/g, '')).not.toContain(IBAN);
    expect(text).not.toContain('12300695');
  }

  const summary = `claim ${claimNumber} on ${policyNumber}: reserved ${eur(cents(totals['reserved']))}, paid ${eur(PAYMENT)}, incurred ${eur(cents(totals['incurred']))} EUR; ${journals.length} journals; GL-5110 ${eur(net('GL-5110'))}, GL-1110 ${eur(net('GL-1110'))}`;
  test.info().annotations.push({ type: 'amounts', description: summary });
  console.log(`E2E-02a amounts: ${summary}`);
});
