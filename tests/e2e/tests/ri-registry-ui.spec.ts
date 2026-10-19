import { randomUUID } from 'node:crypto';
import { expect, test } from '@playwright/test';
import { call, signIn } from './support/api.js';
import { importProducts, serverNow } from './support/servicing.js';
import { alive, go, signInAs } from './support/ui.js';

// Real catalogue, party lookup, treaty commands and distinct maker/checker sessions.
// This registry acceptance does not claim the unbuilt recovery posting journey.
test.use({ actionTimeout: 30_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

test('RI staff create and edit a treaty, then a different manager approves it', async ({ page, request }) => {
  test.setTimeout(240_000);
  await importProducts(request);
  await page.clock.setFixedTime(await serverNow(request));
  const underwriter = await signIn(request, 'underwriter');
  const partyName = `Synthetic RI Browser ${randomUUID().slice(0, 8)}`;
  const party = await call(request, underwriter, 'POST', '/api/pty/v1/parties', {
    partyType: 'ORGANISATION', organisation: { legalName: partyName, tradeName: partyName },
  });
  expect(party.status, party.text).toBe(201);
  const failures: string[] = [];
  let recoveryEndpointUnavailable = false;
  page.on('pageerror', error => failures.push(error.message));
  page.on('response', response => {
    const path = new URL(response.url()).pathname;
    // SL4-RI-RECOVERY is a separate, unbuilt package. Validate its visible unavailable
    // state explicitly; every other failed API request still fails registry acceptance.
    if (path === '/api/ri/v1/recoveries/list-by-contract' && response.status() === 404)
      recoveryEndpointUnavailable = true;
    else if (path.startsWith('/api/') && response.status() >= 400)
      failures.push(`${response.status()} ${path}`);
  });

  await signInAs(page, 'Dev Reinsurance Accountant (synthetic)');
  await go(page, '/reinsurance/contracts/new');
  await page.getByRole('checkbox', { name: 'Ίδιες Ζημιές Οχήματος', exact: true }).focus();
  await page.getByRole('checkbox', { name: 'Ίδιες Ζημιές Οχήματος', exact: true }).press('Space');
  for (const [label, value] of [
    ['Συγκράτηση (σημείο έναρξης)', '500000'], ['Όριο ανά κίνδυνο', '500000'],
  ]) {
    const field = page.getByRole('textbox', { name: label! });
    await field.fill(value!);
    await field.press('Tab');
  }
  const picker = page.getByRole('searchbox', { name: 'Αναζήτηση αντασφαλιστή', exact: true });
  await picker.fill(partyName);
  await picker.press('Enter');
  await page.getByRole('gridcell', { name: partyName, exact: true }).click();
  await page.getByRole('textbox', { name: 'Υπογεγραμμένο ποσοστό' }).fill('100');
  await page.getByRole('button', { name: 'Αποθήκευση προχείρου', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Αποθήκευση προχείρου', exact: true }).click();
  await expect(page).toHaveURL(/\/reinsurance\/contracts\/[0-9a-f-]{36}$/);
  const contractId = page.url().split('/').pop()!;
  const accountant = await signIn(request, 'riacct');
  let read = await call(request, accountant, 'GET', `/api/ri/v1/contracts/${contractId}`);
  expect(read.status, read.text).toBe(200);
  expect(read.body['contract']['scope']['productCodes']).toEqual(['MOTOR-GR']);
  expect(read.body['contract']['scope']['coverageCodes']).toEqual(['OWN-DAMAGE']);
  expect(read.body['contract']['maker']).toBe('USER:dev:riacct');

  await page.getByRole('button', { name: 'Επεξεργασία προχείρου', exact: true }).click();
  const attachment = page.getByRole('textbox', { name: 'Συγκράτηση (σημείο έναρξης)' });
  await attachment.fill('400000');
  await attachment.press('Tab');
  await page.getByRole('button', { name: 'Αποθήκευση προχείρου', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Αποθήκευση προχείρου', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/reinsurance/contracts/${contractId}$`));
  read = await call(request, accountant, 'GET', `/api/ri/v1/contracts/${contractId}`);
  expect(Number(read.body['contract']['layers'][0]['attachment']['amount'])).toBe(400000);
  await page.getByRole('button', { name: 'Υποβολή για έγκριση…', exact: true }).click();
  await page.getByRole('button', { name: 'Υποβολή', exact: true }).click();
  await expect.poll(async () => {
    const response = await call(request, accountant, 'GET', `/api/ri/v1/contracts/${contractId}`);
    expect(response.status, response.text).toBe(200);
    return response.body['contract']['status'];
  }).toBe('PENDING_APPROVAL');
  await expect(page.getByRole('button', { name: 'Έγκριση…', exact: true })).toHaveCount(0);

  await signInAs(page, 'Dev Reinsurance Manager (synthetic)');
  await go(page, `/reinsurance/contracts/${contractId}`);
  await page.getByRole('button', { name: 'Έγκριση…', exact: true }).click();
  await page.getByRole('button', { name: 'Επιβεβαίωση έγκρισης', exact: true }).click();
  await expect.poll(async () => {
    const response = await call(request, accountant, 'GET', `/api/ri/v1/contracts/${contractId}`);
    expect(response.status, response.text).toBe(200);
    return response.body['contract']['status'];
  }).toBe('ACTIVE');
  await alive(page);
  expect(failures).toEqual([]);
  if (recoveryEndpointUnavailable) {
    await expect(page.getByText('Οι ανακτήσεις δεν φορτώθηκαν', { exact: true })).toBeVisible();
    await expect(page.getByRole('grid', { name: 'Ανά επίπεδο', exact: true })).toHaveCount(0);
    await expect(page.getByText('Ανακτήσιμο (επιβαρύνσεις)', { exact: true })).toHaveCount(0);
    await expect(page.getByText('Δεν υπάρχουν ανακτήσεις ακόμη', { exact: true })).toHaveCount(0);
  }
  await page.screenshot({ path: '../../.logs/ri-active-desktop.png', fullPage: true });
  await page.setViewportSize({ width: 840, height: 1000 });
  expect(await page.evaluate<boolean>('document.documentElement.scrollWidth <= document.documentElement.clientWidth')).toBe(true);
  await page.screenshot({ path: '../../.logs/ri-active-840.png', fullPage: true });
});

test('RI maker with both accountant and manager roles sees the treaty read-only for approval', async ({ page, request }) => {
  test.setTimeout(120_000);
  await page.clock.setFixedTime(await serverNow(request));
  const signed = await request.post('/api/plt/v1/dev/sign-in', { data: { userId: 'superuser' } });
  expect(signed.status()).toBe(200);
  const session = await signed.json();
  expect(session.user.actorKey).toBe('USER:dev:superuser');
  const token = session.accessToken as string;
  const party = await call(request, token, 'POST', '/api/pty/v1/parties', {
    partyType: 'ORGANISATION', organisation: { legalName: `Synthetic RI Maker ${randomUUID().slice(0, 8)}` },
  });
  expect(party.status, party.text).toBe(201);
  const money = (amount: string) => ({ amount, currency: 'EUR' });
  const created = await call(request, token, 'POST', '/api/ri/v1/contracts', {
    legalEntity: 'GR-TEST', contractType: 'XOL_PER_RISK', contractYear: 2035, currency: 'EUR',
    period: { from: '2035-01-01', to: '2036-01-01' },
    scope: { productCodes: ['MOTOR-GR'], coverageCodes: ['OWN-DAMAGE'] },
    clause: { alaeIncluded: true, statutoryInterestIncluded: false, recoveriesInure: 'REALISED_ONLY' },
    layers: [{ layerNo: 1, attachment: money('500000.00'), limit: money('500000.00'), aad: money('0.00') }],
    participations: [{ reinsurerPartyId: party.body['party']['partyId'], signedLinePct: '100', lead: true }], placedPct: '100',
  });
  expect(created.status, created.text).toBe(201);
  const contract = created.body['contract'];
  const submitted = await call(request, token, 'POST', '/api/ri/v1/contracts/submit', {
    contractId: contract.contractId, expectedRecordVersion: contract.recordVersion,
  });
  expect(submitted.status, submitted.text).toBe(200);
  const refused = await call(request, token, 'POST', '/api/ri/v1/contracts/approve', {
    contractId: contract.contractId, expectedRecordVersion: submitted.body['contract']['recordVersion'], decision: 'APPROVE',
  });
  expect(refused.status, refused.text).toBe(403);
  await signInAs(page, 'Dev Super User — all roles (synthetic)');
  await go(page, `/reinsurance/contracts/${contract.contractId}`);
  await expect(page.getByText('Δεν μπορείτε να εγκρίνετε τη δική σας καταχώριση', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Έγκριση…', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Επιστροφή για διόρθωση…', exact: true })).toHaveCount(0);
  const unchanged = await call(request, token, 'GET', `/api/ri/v1/contracts/${contract.contractId}`);
  expect(unchanged.body['contract']['status']).toBe('PENDING_APPROVAL');
  await alive(page);
});
