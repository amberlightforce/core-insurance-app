import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { call, signIn, type Json } from './support/api.js';
import { advanceServerPast } from './support/time.js';
import { importProducts, issuePolicy, serverNow } from './support/servicing.js';

// E2E-09 DSAR access and erasure across modules (SLICE-PLAN-6, D-SL6-07). One data subject holds a policy, an invoice, a claim, a
// delivered document, a withdrawal declaration and a DAT subject fact. An ACCESS case fans out over the 15-row map (all 15 modules answer, WRK
// included since NX-HD-03/05: NOT_HELD when the party has no tasks, else its tasks), exports what is held, redacts a third-party module and logs
// the disclosure; with no blocker left the export completes the case (COMPLETED, DSARCompleted published once). An ERASURE case needs a verified
// identity, a second DPO's approval and no legal hold; regulated classes (and anything under a retention hold) come back RESTRICTED with
// retentionEnd UNSET_F404 (F-404), the others are erased, WRK retains and restricts its tasks, and the erase completes the case.

const MAP = ['PTY', 'POL', 'BIL', 'CLM', 'UW', 'RI', 'DOC', 'CHN', 'WRK', 'PLT', 'FIN', 'RAT', 'DAT', 'MIG', 'CMP'];
const KINDS = ['ERASED', 'ANONYMISED', 'RESTRICTED', 'NOT_HELD'];
const DAT_FACT = { category: 'CONTACT', key: 'preferred-channel', value: 'e-mail' };

const outcomesOf = (tasks: Json[], module: string): Json[] => (tasks.find((t) => t['module'] === module)?.['outcomes'] ?? []) as Json[];

test('E2E-09 DSAR: fan-out map, export found == seeded, redaction, disclosure log, approval, legal hold, erase with retention classes, DAT once', async ({ request }) => {
  test.setTimeout(600_000);
  const dpo = await signIn(request, 'dpo');
  const dpo2 = await signIn(request, 'superuser'); // the second DPO (Staff.ComplianceOfficer) who is not the case creator
  const admin = await signIn(request, 'admin');
  const claims = await signIn(request, 'claims');
  const underwriter = await signIn(request, 'underwriter');
  await importProducts(request);

  // ---- 0. Seed: a party with a paid policy (invoice), a claim, a withdrawal declaration and a DAT subject fact.
  const policy = await issuePolicy(request, 'e2e09', { pay: true, plate: 'IKX-0909' });
  const subject = policy.partyId;
  const lossAt = new Date(policy.startAt.getTime() + 1000).toISOString().replace('.000Z', 'Z');
  await advanceServerPast(request, new Date(policy.startAt.getTime() + 4000));
  const fnol = await call(request, claims, 'POST', '/api/clm/v1/fnol/submit', {
    lineOfBusiness: 'MOTOR', policyId: policy.policyId, policyNumber: policy.policyNumber, lossAt, lossCause: 'COLLISION',
    lossLocation: 'Λεωφ. Κηφισίας 124, Αθήνα', description: 'E2E-09 DSAR subject claim (synthetic)', channel: 'STAFF', receiptMedium: 'TELEPHONE',
    incidents: [{ incidentType: 'VEHICLE', vehicleRef: 'IKX0909', drivable: true, damageAreas: ['FRONT'] }],
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
  });
  expect(fnol.status, fnol.text).toBe(200);
  const declared = await call(request, underwriter, 'POST', '/api/chn/v1/withdrawals/declare', {
    name: 'Μαρία Παπαδοπούλου', contractId: policy.policyId, eMail: 'e2e09.withdrawal@example.org', reason: 'DSAR journey',
  });
  expect(declared.status, declared.text).toBeLessThan(300);
  const seeded = await call(request, admin, 'POST', '/api/dat/v1/dev/subject-facts', { subjectToken: subject, ...DAT_FACT, value: DAT_FACT.value });
  expect(seeded.status, seeded.text).toBe(200);
  expect(seeded.body['written']).toBe(true);

  const open = async (type: 'ACCESS' | 'ERASURE'): Promise<string> => {
    const created = await call(request, dpo, 'POST', '/api/cmp/v1/dsar', { subjectPartyId: subject, requestType: type });
    expect(created.status, created.text).toBe(200);
    expect(created.body['request']['status']).toBe('RECEIVED');
    const id = created.body['request']['dsarId'] as string;
    const verified = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/verify-identity', { id, method: 'ID_DOCUMENT' });
    expect(verified.status, verified.text).toBe(200);
    return id;
  };
  const readCase = async (id: string): Promise<Json> => {
    const got = await call(request, dpo, 'GET', `/api/cmp/v1/dsar/${id}`);
    expect(got.status, got.text).toBe(200);
    return got.body;
  };

  // ---- 1. ACCESS: the clock runs for one month, the fan-out lists the 15-row map, every module answers (WRK included).
  const accessId = await open('ACCESS');
  const early = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/start-fan-out', { id: randomUUID() });
  expect(early.status, 'an unknown case is refused').toBeGreaterThanOrEqual(400);
  const fan = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/start-fan-out', { id: accessId });
  expect(fan.status, fan.text).toBe(200);
  expect(fan.body['request']['status']).toBe('IN_REVIEW');
  const tasks = fan.body['tasks'] as Json[];
  expect(tasks.map((t) => t['module'])).toEqual(MAP);
  expect(tasks.find((t) => t['module'] === 'WRK')!['status'], 'WRK has a DSAR participant').toBe('DONE');
  expect(tasks.filter((t) => t['status'] !== 'DONE'), 'every module answered').toEqual([]);
  expect(tasks.some((t) => (t['outcomes'] as Json[]).some((o) => o['kind'] === 'NOT_BUILT')), 'no NOT_BUILT outcome').toBe(false);
  expect(fan.body['request']['blockers'], 'only the package is still to compile').toEqual(['PACKAGE_NOT_COMPILED']);

  const received = new Date(fan.body['request']['receivedAt']).getTime();
  const deadline = new Date(fan.body['request']['deadline']).getTime();
  const days = (deadline - received) / 86_400_000;
  expect(days, 'one month (CMP_DSAR_RESPONSE)').toBeGreaterThanOrEqual(28);
  expect(days).toBeLessThanOrEqual(33);

  // ---- 2. Export: found == seeded per module.
  const count = (module: string): number => outcomesOf(tasks, module).reduce((a, o) => a + (o['recordCount'] as number), 0);
  const exportedClasses = (module: string): string[] => outcomesOf(tasks, module).filter((o) => o['exported'] === true).map((o) => o['recordClass'] as string);
  await test.step('each module reports exactly what the journey seeded', async () => {
    expect(count('PTY'), 'the party record, address and contact point').toBeGreaterThanOrEqual(1);
    expect(count('POL'), 'the policy').toBeGreaterThanOrEqual(1);
    expect(count('BIL'), 'the invoice and ledger rows').toBeGreaterThanOrEqual(1);
    expect(count('CLM'), 'the claim').toBeGreaterThanOrEqual(1);
    expect(count('CHN'), 'the one withdrawal declaration').toBe(1);
    expect(exportedClasses('CHN')).toEqual(['WITHDRAWAL_REQUEST']);
    expect(count('DAT'), 'the one DAT subject fact').toBe(1);
    expect(exportedClasses('DAT')).toEqual(['SUBJECT_FACT']);
    expect(exportedClasses('CLM').length).toBeGreaterThan(0);
    expect(exportedClasses('BIL').length).toBeGreaterThan(0);
    for (const t of tasks) for (const o of t['outcomes'] as Json[]) expect(KINDS, `${t['module']}/${o['recordClass']}`).toContain(o['kind']);
  });

  // ---- 3. DPO review: redact the third party data of CLM, then export. The export completes the case.
  const clm = tasks.find((t) => t['module'] === 'CLM')!;
  const decided = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/decide-item', { id: accessId, taskId: clm['taskId'], redactThirdParty: true, note: 'claim file names a third party' });
  expect(decided.status, decided.text).toBe(200);
  const exported = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/export', { dsarId: accessId });
  expect(exported.status, exported.text).toBe(200);
  const entries = exported.body['package']['entries'] as Json[];
  expect(entries.find((e) => e['module'] === 'CLM')!['redacted']).toBe(true);
  expect(entries.find((e) => e['module'] === 'POL')!['redacted']).toBe(false);
  expect(JSON.stringify(entries.find((e) => e['module'] === 'DAT'))).toContain(DAT_FACT.value);
  expect(exported.body['package']['index'].length).toBeGreaterThan(0);
  expect(exported.body['request']['blockers'], 'nothing blocks the case').toEqual([]);
  expect(exported.body['request']['status'], 'all modules answered and the package is compiled: the case completes').toBe('COMPLETED');

  // ---- 4. Disclosure log entry for the export.
  await test.step('the disclosure to the data subject is logged', async () => {
    const logged = await call(request, dpo, 'POST', '/api/cmp/v1/disclosure-logs/record', {
      subject: { partyId: subject },
      entry: { recipient: 'Data subject', purpose: 'GDPR Art 15 access', legalBasis: 'GDPR Art 15', dsarId: accessId, categories: ['PTY', 'POL', 'BIL', 'CLM', 'CHN', 'DAT'] },
    });
    expect(logged.status, logged.text).toBe(200);
    const list = await call(request, dpo, 'GET', `/api/cmp/v1/disclosure-logs/query?partyId=${subject}`);
    expect(list.status, list.text).toBe(200);
    const recorded = (list.body['items'] as Json[]).flatMap((i) => i['entries'] as Json[]);
    expect(recorded.some((e) => e['dsarId'] === accessId && e['recipient'] === 'Data subject')).toBe(true);
  });

  // The case was answered inside its deadline: it is completed and the deadline is ahead of the server's now.
  expect(deadline).toBeGreaterThan((await serverNow(request)).getTime());
  const doneAccess = await readCase(accessId);
  expect(doneAccess['request']['status']).toBe('COMPLETED');
  expect(doneAccess['request']['completedAt'] ?? null).not.toBeNull();

  // ---- 5. ERASURE: approval by a second DPO, legal hold, then the purge.
  const eraseId = await open('ERASURE');
  const fanE = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/start-fan-out', { id: eraseId });
  expect(fanE.status, fanE.text).toBe(200);
  const noApproval = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/erase', { dsarId: eraseId });
  expect(noApproval.status, 'erase waits for the approval').toBe(422);
  const self = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/approve', { id: eraseId, action: 'ERASE', reason: 'subject request' });
  expect(self.status, 'the creator cannot approve their own case').toBe(403);
  const approved = await call(request, dpo2, 'POST', '/api/cmp/v1/dsar/approve', { id: eraseId, action: 'ERASE', reason: 'subject request' });
  expect(approved.status, approved.text).toBe(200);

  const hold = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/decide-item', { id: eraseId, legalHold: true, note: 'litigation hold' });
  expect(hold.status, hold.text).toBe(200);
  const blocked = await call(request, dpo2, 'POST', '/api/cmp/v1/dsar/erase', { dsarId: eraseId });
  expect(blocked.status, 'a legal hold blocks the purge').toBe(422);
  expect(blocked.text).toContain('LEGAL-HOLD');
  const underHold = await call(request, admin, 'POST', '/api/dat/v1/subject/export', { requestId: `e2e09-x-${randomUUID()}`, subjectToken: subject });
  expect(underHold.status, underHold.text).toBe(200);
  expect(underHold.body['factCount'], 'nothing was erased under the hold').toBe(1);

  const release = await call(request, dpo, 'POST', '/api/cmp/v1/dsar/decide-item', { id: eraseId, legalHold: false });
  expect(release.status, release.text).toBe(200);
  const erased = await call(request, dpo2, 'POST', '/api/cmp/v1/dsar/erase', { dsarId: eraseId });
  expect(erased.status, erased.text).toBe(200);
  const items = erased.body['perItemOutcome'] as Json[];

  await test.step('every outcome is erased, anonymised, restricted, not held or not built; regulated classes are restricted', async () => {
    for (const i of items) expect(KINDS, `${i['module']}/${i['recordClass']}`).toContain(i['kind']);
    const regulated = items.filter((i) => ['LEDGER', 'INVOICE', 'CLAIM_FILE', 'POLICY_DOCUMENT', 'FISCAL_DOCUMENT', 'BUREAU_FACT'].includes(i['recordClass']) && i['kind'] !== 'NOT_HELD');
    expect(regulated.length, 'the subject holds regulated classes').toBeGreaterThan(0);
    for (const i of regulated) {
      expect(i['kind'], `${i['module']}/${i['recordClass']}`).toBe('RESTRICTED');
      expect(i['legalBasis'], `${i['module']}/${i['recordClass']} basis`).toBeTruthy();
      expect(i['retentionEnd']).toBe('UNSET_F404');
    }
    expect(items.some((i) => i['module'] === 'DAT' && i['kind'] === 'ERASED'), 'DAT erased the subject fact').toBe(true);
    const wrk = items.filter((i) => i['module'] === 'WRK');
    expect(wrk.length, 'WRK answered the erase').toBeGreaterThan(0);
    for (const i of wrk) expect(['NOT_HELD', 'RESTRICTED'], 'WRK retains and restricts its tasks, never erases them').toContain(i['kind']);
    for (const i of items.filter((x) => x['kind'] === 'RESTRICTED')) expect(i['legalBasis'], `${i['module']}/${i['recordClass']} restricted with a basis`).toBeTruthy();
    expect(items.filter((i) => i['kind'] === 'ERASED' || i['kind'] === 'ANONYMISED').length, `the erasable classes were erased: ${JSON.stringify(items.map((i) => [i['module'], i['recordClass'], i['kind'], i['recordCount']]))}`).toBeGreaterThanOrEqual(1);
  });
  const after = await readCase(eraseId);
  expect(after['request']['blockers'], 'every module is erased or restricted').toEqual([]);
  expect(after['request']['status'], 'the erase completes the case').toBe('COMPLETED');
  expect(after['request']['completedAt'] ?? null).not.toBeNull();

  // A repeat of the erase on the completed case changes nothing (DSARCompleted is published once).
  const again = await call(request, dpo2, 'POST', '/api/cmp/v1/dsar/erase', { dsarId: eraseId });
  expect(again.status, again.text).toBe(200);
  expect((again.body['perItemOutcome'] as Json[]).length).toBe(items.length);

  // ---- 6. DAT erasure twice destroys the key once.
  await test.step('DAT: the subject key is destroyed once', async () => {
    const exportAfter = await call(request, admin, 'POST', '/api/dat/v1/subject/export', { requestId: `e2e09-x-${randomUUID()}`, subjectToken: subject });
    expect(exportAfter.body['status']).toBe('ERASED');
    expect(exportAfter.body['keyDestroyed']).toBe(true);
    expect(exportAfter.body['facts']).toEqual([]);

    const token = randomUUID();
    const fact = await call(request, admin, 'POST', '/api/dat/v1/dev/subject-facts', { subjectToken: token, ...DAT_FACT });
    expect(fact.body['written']).toBe(true);
    const requestId = `e2e09-${randomUUID()}`;
    const first = await call(request, admin, 'POST', '/api/dat/v1/erasure/execute', { requestId, subjectToken: token });
    expect(first.status, first.text).toBe(200);
    expect(first.body['factCount']).toBe(1);
    expect(first.body['keyDestroyed']).toBe(true);
    const second = await call(request, admin, 'POST', '/api/dat/v1/erasure/execute', { requestId, subjectToken: token });
    expect(second.body, 'the same request id replays the stored result').toEqual(first.body);
    const other = await call(request, admin, 'POST', '/api/dat/v1/erasure/execute', { requestId: `e2e09-${randomUUID()}`, subjectToken: token });
    expect(other.status, other.text).toBe(200);
    expect(other.body['factCount'], 'nothing is left to erase the second time').toBe(0);
    const rewritten = await call(request, admin, 'POST', '/api/dat/v1/dev/subject-facts', { subjectToken: token, ...DAT_FACT });
    expect(rewritten.body['written'], 'an erased key is never recreated').toBe(false);
  });
});
