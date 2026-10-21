import { expect, test, type APIRequestContext } from '@playwright/test';
import { call, cents, eur, eventually, signIn, type Json } from './support/api.js';
import {
  applySet, bindMotorPolicy, createOrganisation, postFsNotification, reserveLine, submitFnol, tokenFor, uiSignInAs, type BoundPolicy,
} from './support/claims4/index.js';
import { alive, go } from './support/ui.js';

// E2E-06 Friendly Settlement statements through the claims screens (SLICE-PLAN-4 §3.2, variant 7: net RECEIVABLE). API setup builds the two legs
// (own leg 3,000 with an FS recovery reserve, at-fault leg 1,200 notified by the clearing house); the claims manager then imports the month's
// statement on /claims/fs-statements, sees both lines matched and the net 1,800 we collect, approves the net and sees the hand-over to billing.
// Runs against the stack of tests/e2e/run-e2e06.sh (its e2e06 filter picks this file up). Greek UI, role/label locators from the claims i18n.

const K = 100n;
test.use({ actionTimeout: 20_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

function currentMonth(): { from: string; to: string; label: string } {
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Europe/Athens' }).format(new Date());
  const [year, month] = today.split('-').map(Number) as [number, number];
  const next = month === 12 ? `${year + 1}-01-01` : `${year}-${String(month + 1).padStart(2, '0')}-01`;
  const label = `${year}-${String(month).padStart(2, '0')}`;
  return { from: `${label}-01`, to: next, label };
}

const ofCounterparty = (statement: Json, code: string): { lines: Json[]; net: Json } => ({
  lines: (statement['lines'] as Json[]).filter((l) => l['counterpartyCode'] === code),
  net: (statement['nets'] as Json[]).find((n) => n['counterpartyCode'] === code)!,
});

/** Own leg (FS recovery reserve at the clearing value, unpaid) and at-fault leg (CLEARING payment) against `insurer`. */
async function setUpLegs(request: APIRequestContext, insurer: string, ownAmount: bigint, atFaultAmount: bigint, plates: [string, string]): Promise<void> {
  const claims = await tokenFor(request, 'claims');
  const manager = await tokenFor(request, 'claimsmgr');
  const [ownPolicy, atFaultPolicy] = (await Promise.all([bindMotorPolicy(request, plates[0]), bindMotorPolicy(request, plates[1])])) as [BoundPolicy, BoundPolicy];
  const own = await submitFnol(request, claims, ownPolicy, {
    description: 'E2E-06 UI: own leg of a Friendly Settlement (synthetic)',
    exposures: [{ kind: 'OWN_DAMAGE', coverageCode: 'OWN-DAMAGE' }],
    liabilityFacts: { insuredFaultPct: '0', faultSource: 'JOINT_REPORT', counterpartyInsurerPartyId: insurer, jointReportFlag: true, vehicleCount: 2, accidentInGreece: true },
  });
  await applySet(request, claims, manager, own.claimId, [reserveLine(own.exposures[0]!['exposureId'], ownAmount)]);
  const evaluated = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/evaluate', { claimId: own.claimId });
  expect(evaluated.status, evaluated.text).toBe(200);
  expect(evaluated.body['eligibility']['result']).toBe('ELIGIBLE');
  const caseId = evaluated.body['fsCase']['fsCaseId'] as string;
  const opened = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/open-own-settlement', { fsCaseId: caseId, expectedRecordVersion: evaluated.body['fsCase']['recordVersion'] });
  expect(opened.status, opened.text).toBe(200);
  const reread = await call(request, claims, 'GET', `/api/clm/v1/friendly-settlements/${caseId}`);
  const submitted = await call(request, claims, 'POST', '/api/clm/v1/friendly-settlements/submit', { fsCaseId: caseId, expectedRecordVersion: reread.body['fsCase']['recordVersion'] });
  expect(submitted.status, submitted.text).toBe(200);

  const received = await postFsNotification(request, manager, {
    clearingReference: `STUB-FS-${Math.random().toString(36).slice(2, 14).toUpperCase()}`,
    policyNumber: atFaultPolicy.policyNumber,
    lossDate: new Date().toISOString().slice(0, 10),
    counterpartyInsurerPartyId: insurer,
    amount: eur(atFaultAmount),
    currency: 'EUR',
    damageType: 'MATERIAL_DAMAGE',
  });
  expect(received['fsCase']['role']).toBe('AT_FAULT_INSURER');
}

test('E2E-06 UI: the claims manager imports the FS statement, sees both legs matched and approves the net receivable', async ({ page, request }) => {
  test.setTimeout(600_000);
  const manager = await tokenFor(request, 'claimsmgr');
  const insurer = await createOrganisation(request, await signIn(request, 'underwriter'), 'Synthetic Insurer UI');
  await setUpLegs(request, insurer, 3_000n * K, 1_200n * K, ['IKU-0601', 'IKU-0602']);

  // The legs' cases reach their matchable state through the worker: import by API until every line of the counterparty is MATCHED.
  const period = currentMonth();
  const statement = await eventually('every statement line of the counterparty to be MATCHED', async () => {
    const imported = await call(request, manager, 'POST', '/api/clm/v1/friendly-settlements/import-statement', { period: { from: period.from, to: period.to }, counterpartyCode: insurer });
    expect(imported.status, imported.text).toBe(200);
    const lines = ofCounterparty(imported.body['statement'], insurer).lines;
    return lines.length === 2 && lines.every((l) => l['matchStatus'] === 'MATCHED') ? (imported.body['statement'] as Json) : undefined;
  }, { intervalMs: 2000, timeoutMs: 120_000 });
  const statementId = statement['fsStatementId'] as string;

  // ---- The statement screen: a bad month is refused, the real month imports (idempotent), both lines matched, net receivable 1,800.
  await uiSignInAs(page, 'claimsmgr');
  await go(page, '/claims/fs-statements');
  await expect(page.getByRole('heading', { level: 1, name: 'Καταστάσεις φιλικού διακανονισμού' })).toBeVisible();
  await expect(page.getByText('Εισαγάγετε έναν μήνα για να δείτε την κατάστασή του.')).toBeVisible();
  await page.getByRole('textbox', { name: /^Μήνας/ }).fill('2026-13');
  await page.getByRole('button', { name: 'Εισαγωγή κατάστασης', exact: true }).click();
  await expect(page.getByText('Δώστε μήνα όπως 2026-10')).toBeVisible();
  await page.getByRole('textbox', { name: /^Μήνας/ }).fill(period.label);
  await page.getByRole('button', { name: 'Εισαγωγή κατάστασης', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/claims/fs-statements\\?statement=${statementId}`));
  await alive(page);

  const lines = page.getByRole('grid', { name: 'Γραμμές κατάστασης' }).or(page.getByRole('table', { name: 'Γραμμές κατάστασης' }));
  const mine = lines.getByRole('row').filter({ hasText: insurer });
  await expect(mine).toHaveCount(2);
  for (const row of await mine.all()) await expect(row).toContainText('Αντιστοιχισμένη');

  const card = page.getByTestId(`fs-net-${insurer}`);
  await expect(card).toContainText('Εισπράττουμε');
  await expect(card).toContainText('Σε αναμονή');
  await expect(card).toContainText(/1\.800,00\s?€/);

  // ---- Approve the net: it is handed to billing as a receivable (no disbursement for a net we collect).
  await card.getByRole('button', { name: 'Έγκριση καθαρού', exact: true }).click();
  await expect(card).toContainText('Παραδόθηκε στην τιμολόγηση', { timeout: 30_000 });
  await expect(page.getByTestId(`fs-bil-${insurer}`)).toContainText('Καταχωρίστηκε απαίτηση στην τιμολόγηση');
  await expect(card.getByRole('button', { name: 'Έγκριση καθαρού', exact: true })).toHaveCount(0);
  await expect(card.getByRole('link', { name: 'Άνοιγμα εγκρίσεων πληρωμών' })).toHaveCount(0);

  // The server agrees with the screen: the net is REQUESTED, a RECEIVABLE of 1,800 at BIL.
  const read = await call(request, manager, 'GET', `/api/clm/v1/friendly-settlements/statements/${statementId}`);
  expect(read.status, read.text).toBe(200);
  const net = ofCounterparty(read.body['statement'], insurer).net;
  expect(['REQUESTED', 'SETTLED']).toContain(net['status']);
  expect(net['bilReferenceType']).toBe('RECEIVABLE');
  expect(cents(net['amount'])).toBe(1_800n * K);

  // A reload shows the same state (no second approval is offered) and the page stays responsive.
  await go(page, `/claims/fs-statements?statement=${statementId}`);
  await expect(page.getByTestId(`fs-net-${insurer}`)).toContainText(/1\.800,00\s?€/);
  await expect(page.getByTestId(`fs-net-${insurer}`).getByRole('button', { name: 'Έγκριση καθαρού', exact: true })).toHaveCount(0);
});
