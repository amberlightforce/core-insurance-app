import { expect, test } from '@playwright/test';
import { call, eventually, signIn, type Json } from './support/api.js';
import { advanceTo, importProducts, issuePolicy, registerRefundAccount, serverNow } from './support/servicing.js';
import { alive, assertNoIbanInUrls, go, signInAs } from './support/ui.js';

// Real browser interactions against the isolated shiftable stack. API setup creates each paid policy;
// servicing, explicit confirmation and refund proposal are performed through staff screens.
test.use({ actionTimeout: 30_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

test('E2E-03 staff cancellation and refund proposal reach the paid refund', async ({ page, request }) => {
  test.setTimeout(420_000);
  await importProducts(request);
  const policy = await issuePolicy(request, 'ui-cancel');
  const iban = 'GR1601101250000000012300695';
  await registerRefundAccount(request, policy.partyId, iban, 'Μαρία Παπαδοπούλου');
  await advanceTo(request, policy, 120);
  await page.clock.setFixedTime(await serverNow(request));
  const noIban = assertNoIbanInUrls(page);
  await signInAs(page, 'Dev Underwriter (synthetic)');
  await go(page, `/policies/${policy.policyId}/cancel`);
  await page.getByRole('button', { name: /Αιτία/ }).click();
  await page.getByRole('option', { name: 'Αίτημα πελάτη', exact: true }).click();
  await page.getByRole('button', { name: 'Προεπισκόπηση επιστροφής', exact: true }).click();
  await expect(page.getByText('Επιστροφή προς τον πελάτη', { exact: true })).toBeVisible();
  await expect(page.getByText('ΦΑΑ: δεν επιστρέφεται', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου', exact: true }).click();
  const dialog = page.getByRole('alertdialog');
  await expect(dialog.getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου', exact: true })).toBeDisabled();
  await dialog.getByRole('checkbox').focus();
  await dialog.getByRole('checkbox').press('Space');
  await expect(dialog.getByRole('checkbox')).toBeChecked();
  await dialog.getByRole('button', { name: 'Ακύρωση ασφαλιστηρίου', exact: true }).click();
  await expect(page.getByText('Το ασφαλιστήριο ακυρώθηκε', { exact: true }).first()).toBeVisible();
  await alive(page);

  const billing = await signIn(request, 'billing');
  await eventually('UI cancellation credit note', async () => {
    const response = await call(request, billing, 'GET', `/api/bil/v1/invoices?policyId=${policy.policyId}`);
    expect(response.status, response.text).toBe(200);
    return (response.body['items'] as Json[]).find(item => item['invoice']['kind'] === 'CREDIT_NOTE');
  });
  await signInAs(page, 'Dev Billing Clerk (synthetic)');
  await go(page, `/billing/accounts/${policy.accountId}`);
  const form = page.getByRole('form', { name: 'Επιστροφή πιστωτικού υπολοίπου' });
  await form.getByRole('button', { name: /Αιτία/ }).click();
  await page.getByRole('option', { name: 'Ακύρωση', exact: true }).click();
  await form.getByRole('button', { name: 'Προεπισκόπηση επιστροφής', exact: true }).click();
  await expect(form.getByText('Δεν έχει αποθηκευτεί τίποτα ακόμη.', { exact: true })).toBeVisible();
  await form.getByRole('button', { name: 'Πρόταση επιστροφής', exact: true }).click();
  await expect(page).toHaveURL(/\/billing\/refunds\/[0-9a-f-]{36}$/);
  await alive(page);
  const refundId = page.url().split('/').pop()!;
  await eventually('UI proposed refund to be paid', async () => {
    const response = await call(request, billing, 'GET', `/api/bil/v1/refunds/${refundId}`);
    expect(response.status, response.text).toBe(200);
    return response.body['refund']['state'] === 'PAID' ? response.body['refund'] : undefined;
  });
  await expect(page.getByText(/0695/).first()).toBeVisible();
  await expect(page.locator('body')).not.toContainText(iban);
  noIban();
});

test('E2E-04 staff renewal requires confirmation and binds the next term', async ({ page, request }) => {
  test.setTimeout(360_000);
  await importProducts(request);
  const policy = await issuePolicy(request, 'ui-renew');
  await advanceTo(request, policy, 335);
  await page.clock.setFixedTime(await serverNow(request));
  await signInAs(page, 'Dev Underwriter (synthetic)');
  await go(page, `/policies/${policy.policyId}/renew`);
  await page.getByRole('button', { name: 'Ανανέωση τώρα', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Έκδοση προσφοράς', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'Έκδοση προσφοράς', exact: true }).click();
  await expect(page.getByText('Η προσφορά εκδόθηκε', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Καταχώριση αποδοχής', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('button', { name: 'Καταχώριση αποδοχής', exact: true })).toBeDisabled();
  await dialog.getByRole('checkbox').focus();
  await dialog.getByRole('checkbox').press('Space');
  await expect(dialog.getByRole('checkbox')).toBeChecked();
  await dialog.getByRole('button', { name: 'Καταχώριση αποδοχής', exact: true }).click();
  await expect(page.getByText('Η ανανέωση έγινε αποδεκτή', { exact: true }).first()).toBeVisible();
  await alive(page);
  const underwriter = await signIn(request, 'underwriter');
  const read = await call(request, underwriter, 'GET', `/api/pol/v1/policies/${policy.policyId}`);
  expect(read.status, read.text).toBe(200);
  expect((read.body['terms'] as Json[]).some(term => term['termNumber'] === 2)).toBe(true);
});

test('staff change edits the vehicle, previews and explicitly applies the change', async ({ page, request }) => {
  test.setTimeout(360_000);
  await importProducts(request);
  const policy = await issuePolicy(request, 'ui-change');
  await advanceTo(request, policy, 30);
  await page.clock.setFixedTime(await serverNow(request));
  await signInAs(page, 'Dev Underwriter (synthetic)');
  await go(page, `/policies/${policy.policyId}/change`);
  await page.getByRole('button', { name: /Επόμενο/ }).click();
  await page.getByRole('textbox', { name: /Αξία οχήματος/ }).fill('20000');
  await page.getByRole('textbox', { name: /Αξία οχήματος/ }).press('Tab');

  await page.getByRole('button', { name: /Επόμενο/ }).click();
  await page.getByRole('button', { name: 'Υπολογισμός προεπισκόπησης ασφαλίστρου', exact: true }).click();
  await expect(page.getByText('Πρόσθετο ποσό προς είσπραξη από τον πελάτη', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /Επόμενο/ }).click();
  await page.getByRole('button', { name: 'Εφαρμογή αλλαγής', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('checkbox').focus();
  await dialog.getByRole('checkbox').press('Space');
  await expect(dialog.getByRole('checkbox')).toBeChecked();
  await dialog.getByRole('button', { name: 'Εφαρμογή αλλαγής', exact: true }).click();
  await expect(page.getByText('Η αλλαγή εφαρμόστηκε', { exact: true }).first()).toBeVisible();
  await alive(page);
});
