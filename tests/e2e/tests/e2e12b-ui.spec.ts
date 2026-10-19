import { expect, test } from '@playwright/test';
import { alive, go, signInAs } from './support/ui.js';

// E2E-12 UI check (short). Runs after e2e12.spec.ts on the same stack (file order, one worker): by then GR has been rolled back and
// re-activated, and one rollback exception is still OPEN. The release manager sees the pack registry; the senior underwriter
// opens the exception queue and records a review.
test.use({ actionTimeout: 30_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

test('E2E-12 pack registry and exception queue review', async ({ page }) => {
  test.setTimeout(240_000);
  await signInAs(page, 'Dev Release Manager (synthetic)');
  await go(page, '/admin/packs');
  await expect(page.getByRole('heading', { name: 'Μητρώο πακέτων' })).toBeVisible();
  await page.getByRole('button', { name: /Άνοιγμα πακέτου/ }).first().click();
  await expect(page.getByRole('list', { name: 'Ιστορικό ενεργοποιήσεων' }).getByText(/Επαναφορά/).first()).toBeVisible();
  await alive(page);

  await signInAs(page, 'Dev Senior Underwriter (synthetic)');
  await go(page, '/policies/pack-rollback');
  await expect(page.getByRole('heading', { name: 'Εξαιρέσεις επαναφοράς πακέτου' })).toBeVisible();
  const rows = page.getByTestId('exception-row');
  await expect(rows.first()).toBeVisible();
  await rows.first().getByRole('link', { name: 'Άνοιγμα' }).or(rows.first().getByRole('button', { name: 'Άνοιγμα' })).click();
  await page.getByRole('button', { name: 'Εξέταση…' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: /Έκβαση/ }).click();
  await page.getByRole('option', { name: 'Καμία ενέργεια', exact: true }).click();
  await dialog.getByLabel('Λόγος').fill('Ελέγχθηκε από το UI: δεν απαιτείται ενέργεια.');
  await dialog.getByRole('button', { name: 'Καταγραφή εξέτασης', exact: true }).click();
  await expect(page.getByText('Η εξέταση καταγράφηκε.').first()).toBeVisible();
  await alive(page);
});
