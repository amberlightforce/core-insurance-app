import { expect, test, type APIRequestContext } from '@playwright/test';
import { call, cents, eventually, signIn, sum, type Json } from './support/api.js';
import { advanceTo, importProducts, issuePolicy, iso, serverNow, VEHICLE_VALUE, type Issued } from './support/servicing.js';

// Mid-term change journeys at API level (SLICE-PLAN-3 §3.3; POL X-2 in-sequence): a premium-raising change bills an additional
// invoice, a premium-lowering change credits the account (credit note against an invoice of the policy, IPT kept, D-SL3-05) and an
// open claim whose cover basis changed gets exactly one ReverificationRequired (REQ-CLM-002, -057, -058). Runs through
// tests/e2e/run-e2e03.sh (Shiftable clock, D-SL3-12). Servicing IPT is provisional (D-SL3-23): the flag is asserted.

const dayOfChange = 30;

async function change(request: APIRequestContext, policy: Issued, value: string, effectiveAt?: string): Promise<{ jobId: string; quote: Json; bind: Json; preview: Json }> {
  const underwriter = await signIn(request, 'underwriter');
  const effective = effectiveAt ?? iso(await serverNow(request));
  const created = await call(request, underwriter, 'POST', '/api/pol/v1/policy-changes', { policyId: policy.policyId, effectiveAt: effective, description: 'e2e vehicle value' });
  expect(created.status, created.text).toBe(201);
  expect(created.body['state']).toBe('DRAFT');
  expect(created.body['termId']).toBe(policy.termId);
  const jobId = created.body['jobId'] as string;
  const edit = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/update-draft', {
    jobId, versionNo: 1, expectedDraftVersion: 0,
    instructions: [{ op: 'SET_VEHICLE', vehicle: { locator: policy.vehicleLocator, plate: 'ikx-1234', make: 'Toyota', model: 'Yaris', firstRegistrationYear: 2021, engineCapacityCc: 1400, use: 'PRIVATE', value: { amount: value, currency: 'EUR' } } }],
  });
  expect(edit.status, edit.text).toBe(200);
  const quote = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/quote', { jobId, versionNo: 1 });
  expect(quote.status, quote.text).toBe(200);
  expect(quote.body['state']).toBe('QUOTED');
  const preview = await call(request, underwriter, 'GET', `/api/pol/v1/policy-changes/${jobId}/preview`);
  expect(preview.status, preview.text).toBe(200);
  // The dry-run bind prices the same deltas and writes nothing (REQ-POL-129).
  const dry = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind?dryRun=true', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(dry.status, dry.text).toBe(200);
  const bind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(bind.status, bind.text).toBe(200);
  expect(bind.body['state']).toBe('BOUND');
  expect(JSON.stringify(dry.body['chargeDeltas'].map((d: Json) => d['amount'])), 'preview equals bind').toBe(JSON.stringify(bind.body['chargeDeltas'].map((d: Json) => d['amount'])));
  return { jobId, quote: quote.body, bind: bind.body, preview: preview.body };
}

const invoicesOf = async (request: APIRequestContext, policy: Issued): Promise<Json[]> => {
  const billing = await signIn(request, 'billing');
  const list = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
  expect(list.status, list.text).toBe(200);
  return (list.body['items'] as Json[]).map((i) => i['invoice'] as Json);
};

test('change: a premium increase bills an additional invoice, a decrease issues a credit note; IPT treatment and journals', async ({ request }) => {
  test.setTimeout(420_000);
  await importProducts(request);
  const billing = await signIn(request, 'billing');
  const finance = await signIn(request, 'finance');
  const policy = await issuePolicy(request, 'change');
  await advanceTo(request, policy, dayOfChange);

  // ---- + debit: the car is worth more (own damage rate rises).
  const up = await change(request, policy, '20000.00');
  const upPreview = up.preview['servicingPreview'] as Json;
  expect(upPreview['provisional'], 'servicing IPT is provisional outside Production (D-SL3-23)').toBe(true);
  expect(cents(upPreview['annualBefore'])).toBe(policy.premium);
  expect(cents(upPreview['annualAfter']) > policy.premium).toBe(true);
  expect((upPreview['taxLines'] as Json[]).every((t) => t['treatmentAction'] === 'APPLY'), 'a debit gets IPT APPLY').toBe(true);
  const upDeltas = up.bind['chargeDeltas'] as Json[];
  const upPremium = sum(upDeltas.filter((d) => d['chargeCategory'] === 'PREMIUM').map((d) => cents(d['amount'])));
  const upTax = sum(upDeltas.filter((d) => d['chargeCategory'] === 'TAX').map((d) => cents(d['amount'])));
  expect(upPremium > 0n).toBe(true);
  expect(upTax > 0n).toBe(true);
  expect(cents(upPreview['premiumChange'])).toBe(upPremium);
  expect(cents(upPreview['additionalDue'])).toBe(upPremium + upTax);

  const debitInvoice = await eventually('the additional invoice of the change', async () => {
    return (await invoicesOf(request, policy)).find((i) => i['transactionId'] === up.bind['transactionId']);
  });
  expect(debitInvoice['kind']).toBe('INVOICE');
  expect(debitInvoice['policyTermId']).toBe(policy.termId);
  expect(cents(debitInvoice['total'])).toBe(upPremium + upTax);
  const paidDebit = await call(request, billing, 'POST', '/api/bil/v1/payments/take', {
    billingAccountId: policy.accountId, amount: debitInvoice['total'], method: 'BANK_TRANSFER', invoiceId: debitInvoice['invoiceId'],
  });
  expect(paidDebit.status, paidDebit.text).toBe(201);

  // ---- - credit: the value falls below the original; the credit is issued as a credit note, the IPT is kept.
  const down = await change(request, policy, '10000.00');
  const downPreview = down.preview['servicingPreview'] as Json;
  expect(downPreview['provisional']).toBe(true);
  expect(cents(downPreview['premiumChange']) < 0n, 'a premium credit').toBe(true);
  expect(cents(downPreview['taxChange']), 'IPT is not credited on an endorsement credit (KEEP_NOT_REDUCED, D-SL3-05)').toBe(0n);
  expect((downPreview['taxLines'] as Json[]).every((t) => t['treatmentAction'] === 'KEEP_NOT_REDUCED')).toBe(true);
  const downDeltas = down.bind['chargeDeltas'] as Json[];
  const downPremium = sum(downDeltas.filter((d) => d['chargeCategory'] === 'PREMIUM').map((d) => cents(d['amount'])));
  expect(downPremium).toBe(cents(downPreview['premiumChange']));
  expect(cents(downPreview['refundDue'])).toBe(-downPremium);

  const creditNotes = await eventually('the complete credit notes of the change', async () => {
    const notes = (await invoicesOf(request, policy)).filter(
      (invoice) => invoice['kind'] === 'CREDIT_NOTE' && invoice['transactionId'] === down.bind['transactionId'],
    );
    return notes.length > 0 && sum(notes.map((note) => cents(note['total']))) === -downPremium ? notes : undefined;
  });
  expect(sum(creditNotes.map((note) => cents(note['total']))), 'the complete correction equals the premium credit').toBe(-downPremium);
  const originals = (await invoicesOf(request, policy)).filter((i) => i['kind'] === 'INVOICE').map((i) => i['invoiceId']);
  for (const note of creditNotes) {
    expect(cents(note['total']) > 0n, 'each credit note carries a positive credit').toBe(true);
    expect(originals, 'each credit note corrects an invoice of the policy').toContain(note['originalInvoiceId']);
  }

  // ---- FIN: every journal type of the policy present (written, billed, IPT due, received, allocated, credits), all balanced.
  const journals = await eventually('FIN journals of the change credit note', async () => {
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
    return written === -(policy.premium + upPremium + downPremium) && all.length >= 10 ? all : undefined;
  });
  const signed = (l: Json): bigint => (l['side'] === 'DEBIT' ? cents(l['amount']) : -cents(l['amount']));
  for (const journal of journals) {
    const lines = journal['lines'] as Json[];
    expect(sum(lines.map(signed)), `${journal['journalNumber']} balances`).toBe(0n);
  }
  const net = (account: string): bigint => sum(journals.flatMap((j) => j['lines'] as Json[]).filter((l) => l['account'] === account).map(signed));
  expect(net('GL-2410'), 'IPT payable is unchanged by the credit').toBe(-(policy.tax + upTax));
  expect(net('GL-2110'), 'written premium follows the net premium of the three transactions').toBe(-(policy.premium + upPremium + downPremium));
});

test('change: an open claim on the changed cover gets ReverificationRequired and keeps its snapshot until a human decides', async ({ request }) => {
  test.setTimeout(420_000);
  await importProducts(request);
  const claimsUser = await signIn(request, 'claims');
  const policy = await issuePolicy(request, 'changeclaim', { pay: false });
  await advanceTo(request, policy, dayOfChange);

  // The loss and the change share one instant so the loss date is on or after the change's effective date.
  const at = iso(new Date((await serverNow(request)).getTime() - 5_000));
  const fnol = await call(request, claimsUser, 'POST', '/api/clm/v1/fnol/submit', {
    lineOfBusiness: 'MOTOR', policyId: policy.policyId, policyNumber: policy.policyNumber, lossAt: at, lossCause: 'COLLISION',
    lossLocation: 'Λεωφ. Κηφισίας 124, Αθήνα', description: 'change journey: claim open while the cover changes (synthetic)', channel: 'STAFF', receiptMedium: 'TELEPHONE',
    incidents: [{ incidentType: 'VEHICLE', vehicleRef: 'IKX1234', drivable: true, damageAreas: ['FRONT'] }],
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
  });
  expect(fnol.status, fnol.text).toBe(200);
  const claimId = fnol.body['claimId'] as string;
  const oldRef = fnol.body['claim']['snapshotRef'] as string;
  expect(fnol.body['claim']['snapshotStatus']).toBe('VERIFIED');

  await change(request, policy, '20000.00', at);

  const pending = await eventually('the claim to require re-verification', async () => {
    const read = await call(request, claimsUser, 'GET', `/api/clm/v1/claims/${claimId}`);
    expect(read.status, read.text).toBe(200);
    const claim = (read.body['claim'] ?? read.body) as Json;
    return claim['snapshotStatus'] === 'REVERIFICATION_REQUIRED' ? { claim, body: read.body } : undefined;
  });
  const claim = pending.claim;
  expect(claim['snapshotRef'], 'REQ-CLM-002: the claim keeps its old ref until a human adopts').toBe(oldRef);
  const demand = (claim['pendingReverification'] ?? pending.body['pendingReverification']) as Json;
  expect(demand['oldSnapshotRef']).toBe(oldRef);
  expect(demand['newSnapshotRef']).toBeTruthy();
  expect(demand['newSnapshotRef']).not.toBe(oldRef);

  // The handler adopts the new ref (REQ-CLM-058); the claim is verified again on the new snapshot.
  const decision = await call(request, claimsUser, 'POST', '/api/clm/v1/coverage/reverify', {
    claimId, decision: 'ADOPT', reasonCode: 'POLICY_CHANGED', expectedNewSnapshotRef: demand['newSnapshotRef'],
  });
  expect(decision.status, decision.text).toBe(200);
  expect(decision.body['snapshotStatus']).toBe('VERIFIED');
  expect(decision.body['snapshotRef']).toBe(demand['newSnapshotRef']);
  expect(decision.body['previousSnapshotRef']).toBe(oldRef);
  void VEHICLE_VALUE;
});
