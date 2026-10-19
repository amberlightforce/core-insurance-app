import { expect, test } from '@playwright/test';
import { call, eventually, signIn, type Json } from './support/api.js';
import { createParty, importProducts, serverNow } from './support/servicing.js';
import { advanceClock } from './support/time.js';
import { GR1, createBatch, dayAt, noonAt, outcomeNames, reverse, runBatch, sql } from './support/migration.js';

// E2E-05 migration of an in-force motor policy (SLICE-PLAN-6, D-SL6-05). Fixture GR-1 (tests/e2e/fixtures/legacy-gr1.json): a legacy
// annual policy with one vehicle, two drivers, a SEPA mandate, an open receivable, an open bodily-injury claim and a motor XoL treaty.
// Dates are offsets from the conversion day T0 (the dev clock's day when the journey starts): T0 stands for 2027-01-01 (mid-term
// conversion, term T0-184d..T0+181d = 2026-07-01..2027-06-30). The renewal-based conversion runs later on the same dev clock, in the
// last month of the term (the importer only takes an in-force term). Amounts, the treaty and the clock length are illustrative.
// Facts the public API does not expose (baseline hash, opening flags, the opening receivable, the single RI tracker) are read with SQL.
// Maker is migmgr, checker migmgr2 (the starter can never approve their own batch).

test('E2E-05 migration of GR-1: baseline, idempotent imports, opening deltas, receivable, claim clock, RI tracker, FIN reconciliation, routing, rollback', async ({ request }) => {
  test.setTimeout(900_000);
  const maker = await signIn(request, 'migmgr');
  const checker = await signIn(request, 'migmgr2');
  const underwriter = await signIn(request, 'underwriter');
  const billing = await signIn(request, 'billing');
  const finance = await signIn(request, 'finance');
  const claims = await signIn(request, 'claims');
  const tag = Date.now().toString();
  await importProducts(request);

  const t0 = await serverNow(request);
  const termStart = noonAt(t0, GR1['termStartOffsetDays']);
  const legacyA = `GR1-A-${tag}`;
  const legacyCopy = `GR1-COPY-${tag}`;
  const legacyB = `GR1-B-${tag}`;
  const holder = await createParty(request, underwriter, `gr1-${tag}`);
  const second = await createParty(request, underwriter, `gr1b-${tag}`);

  const policyItem = (legacy: string, mode: 'OPENING' | 'RENEWAL_BASED', plate: string): Json => ({
    module: 'POL', objectType: 'Policy', legacyId: legacy,
    payload: {
      legacyNumber: legacy, mode, policyholderPartyId: holder, product: 'MOTOR-GR', currency: 'EUR', termStart, status: 'IN_FORCE', paymentPlan: 'ANNUAL',
      risk: {
        vehicle: { ...GR1['vehicle'], plate },
        drivers: (GR1['drivers'] as Json[]).map((d, i) => ({ ...d, partyId: i === 0 ? holder : second })),
        coverages: GR1['coverages'],
      },
      charges: GR1['charges'],
    },
  });
  const receivableItem = (invoice: string, legacyPolicy: string): Json => ({
    module: 'BIL', objectType: 'Receivable', legacyId: invoice,
    payload: {
      legacyInvoiceNumber: invoice, payerPartyId: holder, policyLegacyNumber: legacyPolicy, dueDate: dayAt(t0, GR1['receivable']['dueOffsetDays']),
      originalAmount: GR1['receivable']['outstanding'] + 10, outstanding: GR1['receivable']['outstanding'],
      openItems: (GR1['receivable']['openItems'] as number[]).map((amount) => ({ amount, description: 'premium instalment' })),
    },
  });
  const mandateItem = (reference: string): Json => ({
    module: 'BIL', objectType: 'Mandate', legacyId: reference,
    payload: { mandateReference: reference, payerPartyId: holder, signatureDate: GR1['mandate']['signatureDate'], ibanMasked: GR1['mandate']['ibanMasked'] },
  });
  const balanceItem = (legacy: string, accountingDate: string, creditAccount?: string, trialDebit?: number): Json => ({
    module: 'FIN', objectType: 'OpeningBalance', legacyId: legacy,
    payload: {
      legacyId: legacy, accountingDate, currency: 'EUR',
      lines: (GR1['openingBalance']['lines'] as Json[]).map((l) => (creditAccount && l['credit'] > 0 ? { ...l, accountCode: creditAccount } : l)),
      trialBalance: { debit: trialDebit ?? GR1['openingBalance']['trialBalance']['debit'], credit: GR1['openingBalance']['trialBalance']['credit'] },
    },
  });
  const readPolicy = async (policyId: string): Promise<Json> => {
    const read = await call(request, underwriter, 'GET', `/api/pol/v1/policies/${policyId}`);
    expect(read.status, read.text).toBe(200);
    return read.body;
  };

  // ---- Wave 1: policy, opening receivable, SEPA mandate and the FIN opening balance, dry run first, approved by a second user.
  const mandateRef = `UMR${tag}`;
  const invoiceA = `INV-${tag}-A`;
  const wave1 = await runBatch(request, maker, checker, 'GR-1 wave 1: policy, receivable, mandate, opening balance', [
    policyItem(legacyA, 'OPENING', 'ikx-5001'),
    receivableItem(invoiceA, legacyA),
    mandateItem(mandateRef),
    balanceItem(`TB-${tag}`, dayAt(t0, 0)),
  ]);
  const policyA = wave1.outcomes[0]!['coreId'] as string;
  const journalId = wave1.outcomes[3]!['coreId'] as string;

  await test.step('2. party import twice is a no-op', async () => {
    const register = (): Promise<{ status: number; body: Json; text: string }> =>
      call(request, maker, 'POST', '/api/mig/v1/xrefs/register', { source: 'PTY', objectType: 'Party', legacyKey: `PTY-${tag}`, coreId: holder, batchId: wave1.batchId });
    const first = await register();
    expect(first.status, first.text).toBe(200);
    const again = await register();
    expect(again.status, again.text).toBe(200);
    expect(again.body['xrefId'], 'the same cross-reference, not a second one').toBe(first.body['xrefId']);
    const resolved = await call(request, maker, 'POST', '/api/mig/v1/xrefs/resolve', { objectType: 'Party', source: 'PTY', legacyKey: `PTY-${tag}` });
    expect(resolved.body['coreId']).toBe(holder);
    expect(sql(`SELECT count(*) FROM mig.xref WHERE source = 'PTY' AND legacy_key = 'PTY-${tag}'`)).toBe('1');
  });

  await test.step('1. baseline hash: the term pins the batch baseline; the policy is MIGRATION origin, IN_FORCE, one OPENING transaction', async () => {
    const policy = await readPolicy(policyA);
    const baseline = wave1.approved['batch']['baselineHash'] as string;
    expect(baseline).toMatch(/^[0-9a-f]{64}$/);
    expect(policy['term']['configurationHash']).toBe(baseline);
    expect(sql(`SELECT baseline_hash FROM pol.migration_import WHERE policy_id = '${policyA}'`)).toBe(baseline);
    expect(sql(`SELECT configuration_hash FROM pol.policy_transaction WHERE policy_id = '${policyA}'`)).toBe(baseline);
    expect(policy['policy']['status']).toBe('IN_FORCE');
    expect(policy['term']['period']['from']).toBe(termStart);
    expect((policy['transactions'] as Json[]).map((t) => `${t['kind']}/${t['sequence']}`)).toEqual(['OPENING/1']);
    expect(sql(`SELECT origin FROM pol.policy_transaction WHERE policy_id = '${policyA}'`)).toBe('MIGRATION');
    // The premium is the legacy premium as given: nothing was rated.
    expect(policy['transactions'][0]['premium']['amount']).toBe('432.36');
    expect(policy['transactions'][0]['taxes']['amount']).toBe('46.85');
    expect(policy['transactions'][0]['total']['amount']).toBe('479.21');
    expect((policy['riskTree']['drivers'] as Json[]).length).toBe(2);
  });

  await test.step('3. opening deltas are flagged, and the import is not new business', async () => {
    expect(sql(`SELECT count(*) FILTER (WHERE opening) || '/' || count(*) FROM pol.charge_line WHERE policy_id = '${policyA}'`)).toBe('3/3');
    expect(
      sql(`SELECT count(*) FROM plt.outbox_message WHERE event_type = 'ChargeDeltaEmitted' AND aggregate_id = '${policyA}' AND payload->>'opening' = 'true' AND origin = 'MIGRATION'`),
    ).toBe('3');
    expect(sql(`SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '${policyA}' AND origin <> 'MIGRATION'`), 'no live-origin event on the imported policy').toBe('0');
    // Let the worker drain the outbox, then: no invoice was generated for the imported policy.
    await eventually('the outbox of the imported policy to drain', async () =>
      sql(`SELECT count(*) FROM plt.outbox_message WHERE aggregate_id = '${policyA}' AND dispatched_at IS NULL`) === '0' ? true : undefined);
    const invoices = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policyA}`);
    expect(invoices.status, invoices.text).toBe(200);
    expect(invoices.body['items'], 'no new-business invoice for a migrated policy').toEqual([]);
  });

  await test.step('4. the opening receivable equals the legacy outstanding to the cent', async () => {
    const row = sql(`SELECT outstanding_amount::text || '/' || original_amount::text || '/' || origin || '/' || collected_amount::text FROM bil.migration_receivable WHERE legacy_invoice_number = '${invoiceA}'`);
    expect(row).toBe('250.3500/260.3500/MIGRATION/0.0000');
    expect(sql(`SELECT mandate_reference || '/' || signature_date::text || '/' || origin FROM bil.migration_mandate WHERE mandate_reference = '${mandateRef}'`)).toBe(`${mandateRef}/${GR1['mandate']['signatureDate']}/MIGRATION`);
  });

  await test.step('7. FIN: one balanced MIG journal reconciled to the trial-balance totals, nothing on income or expense accounts', async () => {
    const got = await call(request, finance, 'GET', `/api/fin/v1/journals/${journalId}`);
    expect(got.status, got.text).toBe(200);
    const journal = got.body['journal'] as Json;
    expect(`${journal['sourceModule']}/${journal['sourceEventType']}`).toBe('MIG/OpeningBalanceImported');
    const lines = journal['lines'] as Json[];
    const total = (side: string): number => lines.filter((l) => l['side'] === side).reduce((a, l) => a + Math.round(Number(l['amount']['amount']) * 100), 0);
    expect(total('DEBIT')).toBe(Math.round(GR1['openingBalance']['trialBalance']['debit'] * 100));
    expect(total('CREDIT')).toBe(Math.round(GR1['openingBalance']['trialBalance']['credit'] * 100));
    expect(total('DEBIT') - total('CREDIT'), 'reconciliation difference 0.00').toBe(0);
    expect(lines.map((l) => l['account'] as string).filter((a) => /^GL-[4-9]/.test(a)), 'no P&L accounts').toEqual([]);

    // A trial balance that does not reconcile, and an income account, are refused with codes (dry run only: nothing is written).
    const bad = await createBatch(request, maker, 'GR-1 bad opening balances', [balanceItem(`TB-${tag}-X`, dayAt(t0, 0), undefined, 999.99), balanceItem(`TB-${tag}-Y`, dayAt(t0, 0), 'GL-4210')]);
    const dry = await call(request, maker, 'POST', '/api/mig/v1/batches/start', { batchId: bad, dryRun: true });
    expect(dry.status, dry.text).toBe(200);
    expect((dry.body['outcomes'] as Json[]).map((o) => o['errorCode'])).toEqual(['MIG-ERR-RECONCILIATION', 'MIG-ERR-PNL-ACCOUNT']);
  });

  await test.step('2. policy, receivable and mandate imported twice: skipped, one of each', async () => {
    const repeat = await runBatch(request, maker, checker, 'GR-1 wave 1 again', [policyItem(legacyA, 'OPENING', 'ikx-5001'), receivableItem(invoiceA, legacyA), mandateItem(mandateRef)], 'SKIPPED_EXISTING');
    expect(outcomeNames(repeat.approved)).toEqual(['SKIPPED_EXISTING', 'SKIPPED_EXISTING', 'SKIPPED_EXISTING']);
    expect(repeat.outcomes.map((o) => o['coreId'])).toEqual([policyA, wave1.outcomes[1]!['coreId'], wave1.outcomes[2]!['coreId']]);
    expect(sql(`SELECT count(*) FROM pol.migration_import WHERE legacy_number = '${legacyA}'`)).toBe('1');
    expect(sql(`SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = '${invoiceA}'`)).toBe('1');
  });

  // ---- Wave 2: the open claim on the imported policy (core policyId through the POL cross-reference), treaty year, bureau and fiscal records.
  const legacyClaim = `GR1-CLM-${tag}`;
  const treatyId = `${GR1['treaty']['id']}-${tag}`;
  const series = `GR1-S-${tag}`;
  const lossAt = noonAt(t0, GR1['claim']['lossOffsetDays']);
  const notifiedOn = dayAt(t0, GR1['claim']['notifiedOffsetDays']);
  const policyXref = await call(request, maker, 'POST', '/api/mig/v1/xrefs/resolve', { objectType: 'Policy', source: 'POL', legacyKey: legacyA });
  expect(policyXref.body['coreId'], 'the POL cross-reference resolves the core policy id').toBe(policyA);
  const wave2Items: Json[] = [
    {
      module: 'CLM', objectType: 'Claim', legacyId: legacyClaim,
      payload: {
        legacyNumber: legacyClaim, policyId: policyXref.body['coreId'], lossAt, notifiedOn, lossCause: GR1['claim']['lossCause'], lossLocation: 'Synthetic location',
        description: 'Synthetic legacy bodily injury claim', coverageCode: GR1['claim']['coverageCode'], reserve: { amount: GR1['claim']['reserve'], currency: 'EUR' },
      },
    },
    { module: 'RI', objectType: 'TreatyYear', legacyId: `${treatyId}/${GR1['treaty']['contractYear']}`, payload: { treatyId, contractYear: GR1['treaty']['contractYear'], openingAggregate: GR1['treaty']['openingAggregate'] } },
    { module: 'CMP', objectType: 'BureauHandover', legacyId: `HO-${tag}`, payload: { vehicleRef: `IKY-${tag.slice(-4)}`, handoverDate: '2026-03-01' } },
    { module: 'CMP', objectType: 'FiscalContinuity', legacyId: series, payload: { seriesId: series, lastLegacyNumber: 4120, marks: [{ number: 4120, mark: `MARK-${tag}-4120` }] } },
  ];
  const wave2 = await runBatch(request, maker, checker, 'GR-1 wave 2: claim, treaty year, bureau, fiscal', wave2Items);
  const claimId = wave2.outcomes[0]!['coreId'] as string;

  const readClock = async (): Promise<Json> => {
    const clocks = await call(request, claims, 'GET', `/api/cmp/v1/clocks/query?code=CLM_OPEN_CLAIM_RESPONSE&limit=200`);
    expect(clocks.status, clocks.text).toBe(200);
    const items = (clocks.body['items'] as Json[]).filter((c) => c['subjectRef']['id'] === claimId);
    expect(items.length, 'one imported clock').toBe(1);
    return items[0]!;
  };
  const athensDay = (iso: string): string => new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Athens' }).format(new Date(iso));
  let clockBefore: Json = {};
  await test.step('5. claim deadline unchanged: the clock keeps the original notified date and runs from it', async () => {
    const claim = (await call(request, claims, 'GET', `/api/clm/v1/claims/${claimId}`)).body['claim']['summary'] as Json;
    expect(claim['status']).toBe('OPEN');
    expect(claim['noticeOn']).toBe(notifiedOn);
    expect(claim['policyId']).toBe(policyA);
    expect(sql(`SELECT origin || '/' || opening_reserve::text FROM clm.migration_import WHERE claim_id = '${claimId}'`)).toBe('MIGRATION/18000.0000');
    clockBefore = await readClock();
    expect(clockBefore['clockCode']).toBe('CLM_OPEN_CLAIM_RESPONSE');
    expect(clockBefore['status']).toBe('RUNNING');
    expect(athensDay(clockBefore['startedAt'] as string), 'starts at the original notified date, not at import').toBe(notifiedOn);
    expect(athensDay(clockBefore['deadline'] as string), 'illustrative 30-day response clock from the notified date').toBe(dayAt(new Date(`${notifiedOn}T12:00:00Z`), GR1['claim']['clockDays']));
  });

  await test.step('6. one RI tracker for the treaty year, a second import changes nothing; bureau and fiscal continuity recorded', async () => {
    expect(sql(`SELECT count(*) || '/' || max(opening_aggregate)::text FROM ri.tracker WHERE treaty_id = '${treatyId}' AND contract_year = ${GR1['treaty']['contractYear']}`)).toBe('1/125000.5000');
    const repeat = await runBatch(request, maker, checker, 'GR-1 wave 2 again', [wave2Items[0]!, wave2Items[1]!], 'SKIPPED_EXISTING');
    expect(outcomeNames(repeat.approved)).toEqual(['SKIPPED_EXISTING', 'SKIPPED_EXISTING']);
    expect(sql(`SELECT count(*) FROM ri.tracker WHERE treaty_id = '${treatyId}'`)).toBe('1');
    expect(sql(`SELECT count(*) FROM clm.migration_import WHERE legacy_number = '${legacyClaim}'`)).toBe('1');
    expect(sql(`SELECT handover_date::text FROM cmp.bureau_handover WHERE vehicle_ref = 'IKY${tag.slice(-4)}' ORDER BY handover_date DESC LIMIT 1`)).toBe('2026-03-01');
    expect(sql(`SELECT last_number FROM cmp.fiscal_series WHERE series_id = '${series}'`)).toBe('4120');
    expect(sql(`SELECT mark FROM cmp.legacy_fiscal_mark WHERE series_id = '${series}' AND legacy_number = 4120`)).toBe(`MARK-${tag}-4120`);
  });

  await test.step('8. routing: one master per date (Transitioning from T0-17d, CoreMaster from T0+31d); overlap refused', async () => {
    const override = (masterState: string, from: string, to?: string): Promise<{ status: number; body: Json; text: string }> =>
      call(request, maker, 'POST', '/api/mig/v1/routing/override', { objectType: 'Policy', key: legacyA, masterState, from, ...(to ? { to } : {}), reason: 'GR-1 cut-over' });
    const transitioningFrom = dayAt(t0, GR1['routing']['transitioningFromOffsetDays']);
    const coreFrom = dayAt(t0, GR1['routing']['coreMasterFromOffsetDays']);
    expect((await override('TRANSITIONING', transitioningFrom, coreFrom)).status).toBe(200);
    const overlap = await override('CORE', dayAt(t0, 0));
    expect(overlap.status).toBe(422);
    expect(overlap.body['code']).toContain('ROUTING-OVERLAP');
    expect((await override('CORE', coreFrom)).status).toBe(200);
    const master = async (date: string): Promise<string> => {
      const resolved = await call(request, maker, 'POST', '/api/mig/v1/routing/resolve', { objectType: 'Policy', key: legacyA, date });
      expect(resolved.status, resolved.text).toBe(200);
      return resolved.body['masterState'] as string;
    };
    expect(await master(dayAt(t0, GR1['routing']['transitioningFromOffsetDays'] - 1))).toBe('LegacyMaster');
    expect(await master(transitioningFrom)).toBe('Transitioning');
    expect(await master(dayAt(t0, 0))).toBe('Transitioning');
    expect(await master(dayAt(t0, GR1['routing']['coreMasterFromOffsetDays'] - 1))).toBe('Transitioning');
    expect(await master(coreFrom)).toBe('CoreMaster');
    expect(await master(dayAt(t0, 120))).toBe('CoreMaster');
  });

  // ---- 10a. OI-MIG-12 default (D-SL6-05): an untouched copy of the fixture reverses; nothing is left behind.
  await test.step('10a. OI-MIG-12 default: the reversal of an untouched batch (second copy of GR-1) succeeds', async () => {
    const invoiceCopy = `INV-${tag}-COPY`;
    const copy = await runBatch(request, maker, checker, 'GR-1 second copy', [policyItem(legacyCopy, 'OPENING', 'ikx-5002'), receivableItem(invoiceCopy, legacyCopy), mandateItem(`UMR${tag}C`)]);
    const copyPolicy = copy.outcomes[0]!['coreId'] as string;
    expect((await readPolicy(copyPolicy))['policy']['status']).toBe('IN_FORCE');
    const reversed = await reverse(request, maker, copy.batchId);
    expect(outcomeNames(reversed)).toEqual(['REVERSED', 'REVERSED', 'REVERSED']);
    expect(sql(`SELECT count(*) FROM pol.policy WHERE policy_id = '${copyPolicy}'`)).toBe('0');
    expect(sql(`SELECT count(*) FROM bil.migration_receivable WHERE legacy_invoice_number = '${invoiceCopy}'`)).toBe('0');
    expect((await call(request, underwriter, 'GET', `/api/pol/v1/policies/${copyPolicy}`)).status).toBe(404);
  });

  // ---- 10b (part). A batch holding CMP/RI objects has no reverser for them: NOT_REVERSIBLE, nothing removed.
  await test.step('10b. OI-MIG-12 default: a batch holding RI or CMP objects is NOT_REVERSIBLE', async () => {
    const reversed = await reverse(request, maker, wave2.batchId);
    expect(reversed['batch']['status']).toBe('NOT_REVERSIBLE');
    expect(sql(`SELECT count(*) FROM clm.claim WHERE claim_id = '${claimId}'`)).toBe('1');
    expect(sql(`SELECT count(*) FROM ri.tracker WHERE treaty_id = '${treatyId}'`)).toBe('1');
  });

  // ---- (b) Renewal-based conversion in the last month of the term, on the dev clock.
  const toRenewal = GR1['renewalBasedClockOffsetDays'] as number;
  await advanceClock(request, { days: toRenewal });
  const t1 = await serverNow(request);
  expect(dayAt(t1, 0), 'the dev clock moved to the renewal conversion day').toBe(dayAt(t0, toRenewal));

  let policyB = '';
  let batchB = '';
  await test.step('(b) renewal-based conversion: the renewal job is created on the imported term and reaches OFFERED', async () => {
    const run = await runBatch(request, maker, checker, 'GR-1 renewal-based', [policyItem(legacyB, 'RENEWAL_BASED', 'ikx-5003'), receivableItem(`INV-${tag}-B`, legacyB)]);
    batchB = run.batchId;
    policyB = run.outcomes[0]!['coreId'] as string;
    const termB = (await readPolicy(policyB))['term']['termId'] as string;
    const created = await call(request, underwriter, 'POST', '/api/pol/v1/renewals', { termId: termB });
    expect(created.status, created.text).toBe(201);
    const jobId = created.body['jobId'] as string;
    const offered = await call(request, underwriter, 'POST', '/api/pol/v1/renewals/offer', { jobId, termId: termB });
    expect(offered.status, offered.text).toBe(200);
    expect(offered.body['state']).toBe('QUOTED');
    expect(sql(`SELECT sub_state FROM pol.job WHERE job_id = '${jobId}'`)).toBe('OFFERED');
  });

  await test.step('5. (after the clock moved) the imported claim clock still has its original start and deadline', async () => {
    const after = await readClock();
    expect(after['startedAt']).toBe(clockBefore['startedAt']);
    expect(after['deadline']).toBe(clockBefore['deadline']);
  });

  await test.step('10b. OI-MIG-12 default: the converted batch (renewal job on the imported term) is refused with MIG-ERR-REVERSAL-BLOCKED', async () => {
    const reversed = await reverse(request, maker, batchB);
    expect(reversed['batch']['status']).toBe('REVERSAL_BLOCKED');
    const blocked = (reversed['outcomes'] as Json[]).find((o) => o['module'] === 'POL')!;
    expect(blocked['outcome']).toBe('REVERSAL_BLOCKED');
    expect(blocked['errorCode']).toBe('MIG-ERR-REVERSAL-BLOCKED');
    expect(sql(`SELECT count(*) FROM pol.policy WHERE policy_id = '${policyB}'`), 'the policy with activity is kept').toBe('1');
  });
});
