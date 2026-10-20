import { expect, test } from '@playwright/test';
import { call, eventually, signIn, type Json } from './support/api.js';
import { ENTITY, currentHash, openQuote, rollOrActivate, settledJournalCount } from './support/packs.js';
import { importProducts, issuePolicy, PRODUCT } from './support/servicing.js';

// E2E-12 pack rollback after bound business (SLICE-PLAN-5 §4): three policies are bound under the genesis state (GR pack 0.2.0,
// hash H2, MOTOR-GR 1.1), one full quote stays open; a release manager asks to roll GR back to 0.1.0, a design authority approves;
// the product falls back 1.1 -> 1.2; POL lists exactly the affected bound transactions, marks the open quote stale and re-rates
// nothing; new business pins the new state and product; a 0.2.0 term needs the treatment rules (fail closed, then recovery).
// The referral/workbench leg of step 2 belongs to the workbench spec (SL5-UW-WB). Runs through tests/e2e/run-e2e12.sh.

const ROLLBACK_REASON = 'Restore the previously published GR pack after a defective treatment release';

test('E2E-12 pack rollback after bound business', async ({ request }) => {
  test.setTimeout(900_000);
  const admin = await signIn(request, 'admin');
  const underwriter = await signIn(request, 'underwriter');
  const uwsenior = await signIn(request, 'uwsenior');
  const releaseManager = await signIn(request, 'releasemgr');
  const designAuthority = await signIn(request, 'designauth');
  const superuser = await signIn(request, 'superuser');
  await importProducts(request);

  // The current state is cached for up to a second per host (D-SL5-06): poll until it shows the expected hash.
  const expectHash = async (expected: string): Promise<void> => {
    await eventually(`the current hash ${expected.slice(0, 8)}`, async () => ((await currentHash(request)) === expected ? true : undefined));
  };

  // ---- 1. Genesis state and registry.
  const list = await call(request, releaseManager, 'GET', '/api/mkt/v1/packs');
  expect(list.status, list.text).toBe(200);
  const gr = (list.body['items'] as Json[]).find((p) => String(p['pack']).toUpperCase() === 'GR')!;
  expect(gr, list.text).toBeTruthy();
  const PACK = gr['pack'] as string;
  const versions = Object.fromEntries((gr['versions'] as Json[]).map((v) => [v['version'], v['status']]));
  expect(versions['0.1.0']).toBe('PUBLISHED');
  expect(versions['0.2.0']).toBe('PUBLISHED');
  const active = (gr['activeVersions'] as Json[]).find((a) => a['legalEntity'] === ENTITY)!;
  expect(active['version']).toBe('0.2.0');
  const H2 = await currentHash(request);
  expect(active['configurationHash']).toBe(H2);

  // ---- 2. Bind three policies under H2 and leave one quote open.
  const policies = [];
  for (const n of [1, 2, 3]) policies.push(await issuePolicy(request, `e2e12-${n}`, { pay: false, plate: `IKX-120${n}` }));
  const open = await openQuote(request, 'e2e12-open', 'IKX-1209');
  const termBefore: Json[] = [];
  for (const policy of policies) {
    const term = (await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`)).body['term'] as Json;
    expect(term['configurationHash'], 'the term pins H2').toBe(H2);
    expect(String(term['productVersion'])).toMatch(/^1\.1/);
    termBefore.push(term);
  }
  const pins = (term: Json): unknown[] => [term['configurationHash'], term['artefactHash'], term['ratingArtefactHash'], term['resolutionHash'], term['productVersion']];
  const journalsBefore = await settledJournalCount(request);
  const invoiceCount = async (policyId: string): Promise<number> => {
    const billing = await signIn(request, 'billing');
    return ((await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policyId}`)).body['items'] as Json[]).length;
  };
  const invoicesBefore = await Promise.all(policies.map((p) => invoiceCount(p.policyId)));

  // ---- 3. Rollback request with dry run.
  const request03 = { pack: PACK, legalEntity: ENTITY, toVersion: '0.1.0', reason: ROLLBACK_REASON };
  const dry = await call(request, releaseManager, 'POST', '/api/mkt/v1/packs/rollback?dryRun=true', request03);
  expect(dry.status, dry.text).toBe(200);
  expect(dry.body['activationId']).toBeNull();
  const preview = dry.body['preview'] as Json;
  expect([preview['fromVersion'], preview['toVersion']]).toEqual(['0.2.0', '0.1.0']);
  expect(preview['hashesIssued']).toContain(H2);
  const diff = preview['keyDiff'] as Json[];
  const removed = diff.filter((d) => String(d['key']).startsWith('tax.treatment.rule.'));
  expect(removed.length, 'the treatment rule keys disappear').toBeGreaterThan(0);
  expect(removed.every((d) => d['change'] === 'REMOVED')).toBe(true);
  test.info().annotations.push({ type: 'keyDiff', description: `${removed.length} treatment keys removed, ${diff.length} keys in the diff` });
  expect(await currentHash(request), 'a dry run changes nothing').toBe(H2);

  // ---- 4. Maker cannot approve; the design authority does.
  const own = await call(request, superuser, 'POST', '/api/mkt/v1/packs/rollback', request03);
  expect(own.status, own.text).toBe(200);
  const self = await call(request, superuser, 'POST', '/api/mkt/v1/pack-activations/decide', { activationId: own.body['activationId'], decision: 'APPROVE', reason: 'self' });
  expect(self.status).toBe(403);
  expect(self.text).toMatch(/PLT-ERR-SELF-APPROVAL|MKT-ERR-SOD-VIOLATION/); // the maker is also the maker's principal
  const noRole = await call(request, releaseManager, 'POST', '/api/mkt/v1/pack-activations/decide', { activationId: own.body['activationId'], decision: 'APPROVE', reason: 'no role' });
  expect(noRole.status, 'the release manager lacks Platform.DesignAuthority').toBe(403);
  const rejected = await call(request, designAuthority, 'POST', '/api/mkt/v1/pack-activations/decide', { activationId: own.body['activationId'], decision: 'REJECT', reason: 'Rejected so the real maker can file it' });
  expect(rejected.status, rejected.text).toBe(200);
  expect(rejected.body['activation']['status']).toBe('REJECTED');
  expect(await currentHash(request), 'a rejected request changes nothing').toBe(H2);

  const rollback = await rollOrActivate(request, 'rollback', PACK, '0.1.0', ROLLBACK_REASON);
  const H3 = rollback['resultingHash'] as string;
  expect(H3).toMatch(/^[0-9a-f]{64}$/);
  expect(H3).not.toBe(H2);
  expect(rollback['kind']).toBe('ROLLBACK');
  expect([rollback['from'], rollback['to']]).toEqual(['0.2.0', '0.1.0']);
  await expectHash(H3);

  // ---- 5. Reproduction by hash: the old state is still resolvable. (tax.treatment.* keys are read by the TaxCalculator only, so the
  // treatment rows themselves are proved by step 10.)
  const today = new Date().toISOString().slice(0, 10);
  const resolve = async (hash: string) =>
    call(request, admin, 'POST', '/api/mkt/v1/configuration/resolve', { legalEntity: ENTITY, jurisdiction: 'GR', namespace: 'tax', timeBasisDates: { TAX_POINT_DATE: today }, configurationHash: hash });
  for (const hash of [H2, H3]) {
    const read = await resolve(hash);
    expect(read.status, read.text).toBe(200);
    expect(read.body['configurationHash']).toBe(hash);
  }
  expect((await resolve('a'.repeat(64))).status, 'an unrecorded hash is unknown').toBe(404);

  // ---- 6. Product fall-back 1.1 -> 1.2.
  const fallbackBody = { productCode: PRODUCT, defectiveVersion: '1.1', reason: 'Defective 1.1 withdrawn, restoring the 1.0 rules' };
  const fbDry = await call(request, releaseManager, 'POST', '/api/pfc/v1/product-versions/fallback?dryRun=true', fallbackBody);
  expect(fbDry.status, fbDry.text).toBe(200);
  const fb = await call(request, releaseManager, 'POST', '/api/pfc/v1/product-versions/fallback', fallbackBody);
  expect(fb.status, fb.text).toBe(200);
  const fbDecided = await call(request, designAuthority, 'POST', '/api/pfc/v1/product-versions/decide-fallback', { fallbackId: fb.body['fallbackId'], decision: 'APPROVE', reason: 'Approved by the design authority' });
  expect(fbDecided.status, fbDecided.text).toBe(200);
  expect(fbDecided.body['fallback']['status']).toBe('APPLIED');
  expect(String(fbDecided.body['fallback']['newVersion'])).toMatch(/^1\.2/);

  // ---- 7. Exception queue, stale quote, nothing re-rated.
  const exceptions = await eventually('the three exceptions', async () => {
    const page = await call(request, uwsenior, 'GET', `/api/pol/v1/pack-rollback-exceptions?status=OPEN&activationId=${rollback['activationId']}&limit=100`);
    expect(page.status, page.text).toBe(200);
    const items = page.body['items'] as Json[];
    return items.length >= policies.length ? items : undefined;
  });
  expect(exceptions.map((e) => e['termId']).sort(), 'exactly the bound transactions of the three policies').toEqual(policies.map((p) => p.termId).sort());
  for (const e of exceptions) {
    expect(e['configurationHash']).toBe(H2);
    expect([e['fromVersion'], e['toVersion']]).toEqual(['0.2.0', '0.1.0']);
    expect(e['wrkActivity']).toBe('NOT_CREATED_WRK_NOT_BUILT');
    expect(e['status']).toBe('OPEN');
  }
  const stale = await call(request, underwriter, 'GET', `/api/pol/v1/jobs/${open.jobId}`);
  expect(stale.body['job']['versions'][0]['staleReason']).toBe('PACK_ROLLBACK');
  const staleBind = await call(request, underwriter, 'POST', '/api/pol/v1/jobs/bind', { jobId: open.jobId, versionNo: 1, paymentPlanOption: 'ANNUAL', confirmation: true });
  expect(staleBind.status).toBeGreaterThanOrEqual(400);
  expect(staleBind.text).toContain('QUOTE-STALE');
  for (const [i, policy] of policies.entries()) {
    const term = (await call(request, underwriter, 'GET', `/api/pol/v1/terms/${policy.termId}`)).body['term'] as Json;
    expect(pins(term), 'pinned hashes and product version unchanged').toEqual(pins(termBefore[i]!));
    expect(await invoiceCount(policy.policyId), 'no billing entry').toBe(invoicesBefore[i]);
  }
  expect(await settledJournalCount(request), 'the rollback posts no journal').toBe(journalsBefore);

  // ---- 8. Review outcomes.
  const review = (i: number, outcome: string, reason: string, token = uwsenior) =>
    call(request, token, 'POST', '/api/pol/v1/pack-rollback-exceptions/review', { exceptionId: exceptions[i]!['exceptionId'], outcome, reason });
  const noAction = await review(0, 'NO_ACTION', 'Premium unaffected by the treatment rules');
  expect(noAction.status, noAction.text).toBe(200);
  expect(noAction.body['status']).toBe('REVIEWED');
  const correction = await review(1, 'CORRECTION_REQUIRED', 'Correct under the restored pack with the OOS slice');
  expect(correction.status, correction.text).toBe(200);
  expect(correction.body['review']['outcome']).toBe('CORRECTION_REQUIRED');
  expect((await review(0, 'NO_ACTION', 'twice')).status, 'reviewed once').toBeGreaterThanOrEqual(400);
  expect((await review(2, 'NO_ACTION', 'not a manager', underwriter)).status, 'only the underwriting manager reviews').toBe(403);

  // ---- 9. New business after the rollback pins H3 and the fall-back product.
  const fresh = await eventually('new business under the restored state and the fall-back product', async () => {
    try {
      return await issuePolicy(request, 'e2e12-new', { pay: true, plate: 'IKX-1301' });
    } catch {
      return undefined; // the RAT artefact of the fall-back version activates asynchronously
    }
  });
  const freshTerm = (await call(request, underwriter, 'GET', `/api/pol/v1/terms/${fresh.termId}`)).body['term'] as Json;
  expect(freshTerm['configurationHash']).toBe(H3);
  expect(String(freshTerm['productVersion'])).toMatch(/^1\.2/);

  // ---- 10. An H2 term cannot be cancelled without the treatment rules (fail closed); after re-activating 0.2.0 it can.
  // The terms start 20 s after their creation: poll the dry run until it is refused for the missing rule (not for a term that has not started).
  const cancel = { policyId: policies[0]!.policyId, source: 'Policyholder', reasonCode: 'CUSTOMER_REQUEST', effectiveAt: new Date().toISOString().replace(/\.\d+Z$/, 'Z'), kind: 'STANDARD' };
  const refused = await eventually('the dry-run cancellation to be refused with RULE_MISSING', async () => {
    const attempt = await call(request, underwriter, 'POST', '/api/pol/v1/cancellations?dryRun=true', { ...cancel, effectiveAt: new Date().toISOString().replace(/\.\d+Z$/, 'Z') });
    return attempt.status >= 400 && attempt.text.includes('RULE_MISSING') ? attempt : undefined;
  }, { timeoutMs: 120_000, intervalMs: 2000 });
  expect(refused.status, refused.text).toBeGreaterThanOrEqual(400);
  const reactivated = await rollOrActivate(request, 'schedule-activation', PACK, '0.2.0', 'Reactivate the treatment pack after the acceptance of the fix');
  const H4 = reactivated['resultingHash'] as string;
  await expectHash(H4);
  expect(new Set([H2, H3, H4]).size, 'every activation records a new state').toBeGreaterThanOrEqual(2);
  const recovered = await call(request, underwriter, 'POST', '/api/pol/v1/cancellations?dryRun=true', { ...cancel, effectiveAt: new Date().toISOString().replace(/\.\d+Z$/, 'Z') });
  expect(recovered.status, recovered.text).toBe(200);
  for (const tax of recovered.body['servicingPreview']['taxLines'] as Json[]) expect(tax['treatmentAction']).toBe('KEEP_NOT_REDUCED');

  // ---- 12. Audit: the whole activation history is listed.
  const all = await call(request, releaseManager, 'GET', `/api/mkt/v1/pack-activations?pack=${PACK}`);
  expect(all.status, all.text).toBe(200);
  const statuses = (all.body['items'] as Json[]).map((a) => `${a['kind']}:${a['status']}`);
  expect(statuses).toEqual(expect.arrayContaining(['ROLLBACK:REJECTED', 'ROLLBACK:SUPERSEDED', 'ACTIVATE:ACTIVE']));
});
