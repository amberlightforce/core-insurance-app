import { expect, test } from '@playwright/test';
import { call, eventually, signIn } from './support/api.js';
import { alive, go, signInAs } from './support/ui.js';

// E2E-10 AI kill switch through the platform AI control screen (SLICE-PLAN-6): the admin sees the five pilot features blocked, cannot enable one
// that the CMP register has not approved, activates the GLOBAL kill switch (reason required), sees the AI-stopped banner and the gate answer
// killSwitchActive, then clears it. The spec leaves the stack exactly as it found it (kill switch cleared, no feature enabled, nothing approved):
// tests/e2e/tests/e2e10.spec.ts expects an unapproved, switched-off AI plane at its start, and the file name sorts before it. Greek UI, labels
// from platform ai.json. Runs against the stack of tests/e2e/run-e2e10.sh (its e2e10 filter picks this file up).

const FEATURES = ['AI-BIL-01', 'AI-CHN-02', 'AI-CLM-01', 'AI-PTY-01', 'AI-UW-01'];

test.use({ actionTimeout: 20_000, screenshot: 'only-on-failure', trace: 'retain-on-failure' });

test('E2E-10 UI: AI control screen, unapproved feature refused, global kill switch on and off', async ({ page, request }) => {
  test.setTimeout(300_000);
  const admin = await signIn(request, 'admin');
  const resolveFeature = async (feature: string) => {
    const r = await call(request, admin, 'POST', '/api/plt/v1/ai-toggle/resolve', { featureId: feature });
    expect(r.status, r.text).toBe(200);
    return r.body;
  };
  const before = await resolveFeature('AI-CLM-01');
  test.skip(before['killSwitchActive'] === true || (before['registerStatus'] != null && String(before['registerStatus']).toUpperCase() === 'APPROVED'),
    'the AI plane was already driven by another journey (the API spec ran first); this UI journey needs the pristine state');

  await signInAs(page, 'Dev Platform Admin (synthetic)');
  await go(page, '/admin/ai');
  await expect(page.getByRole('heading', { level: 1, name: 'Λειτουργίες ΤΝ' })).toBeVisible();

  // ---- 1. All five pilot features are listed, switched off and blocked; the global switch is inactive.
  const features = page.getByRole('table', { name: 'Λειτουργίες' });
  for (const feature of FEATURES) {
    const row = features.getByRole('row').filter({ has: page.getByRole('rowheader', { name: feature, exact: true }) });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Αποκλεισμένη');
    await expect(row.getByRole('switch', { name: `Ενεργοποίηση ${feature}` })).not.toBeChecked();
  }
  await expect(page.getByText('Ο καθολικός διακόπτης είναι ανενεργός.')).toBeVisible();
  await expect(page.getByText('Καμία αλλαγή σε αυτή τη συνεδρία.')).toBeVisible();

  // ---- 2. A feature the register has not approved cannot be enabled: the screen says so and the switch springs back.
  const clm = page.getByRole('switch', { name: 'Ενεργοποίηση AI-CLM-01' });
  await expect(clm).toBeEnabled();
  await clm.focus();
  await clm.press('Space');
  await expect(page.getByRole('status').filter({ hasText: /Η AI-CLM-01 δεν άλλαξε/ })).toBeVisible();
  await expect(clm).not.toBeChecked();
  const log = page.getByRole('table', { name: 'Αρχείο ενεργειών (αυτή η συνεδρία)' });
  await expect(log.getByRole('row').filter({ hasText: 'AI-CLM-01' })).toContainText('Απορρίφθηκε');
  expect((await resolveFeature('AI-CLM-01'))['allowed']).toBe(false);

  // A malformed feature id is refused before anything is added.
  await page.getByRole('textbox', { name: 'Προσθήκη κωδικού λειτουργίας' }).fill('bad');
  await page.getByRole('button', { name: 'Προσθήκη', exact: true }).click();
  await expect(page.getByText('Χρησιμοποιήστε τη μορφή AI-XXX-NN.')).toBeVisible();

  // ---- 3. The global kill switch needs a reason; then every feature reports it.
  await page.getByRole('button', { name: 'Διακοπή όλης της ΤΝ', exact: true }).click();
  const dialog = page.getByRole('alertdialog').or(page.getByRole('dialog'));
  await expect(dialog.getByText('Διακοπή όλων των λειτουργιών ΤΝ;')).toBeVisible();
  await dialog.getByRole('button', { name: 'Διακοπή όλης της ΤΝ', exact: true }).click();
  await expect(dialog.getByText('Συμπληρώστε αιτιολογία.')).toBeVisible();
  await dialog.getByRole('textbox', { name: /^Αιτιολογία/ }).fill('E2E-10 UI: δοκιμή διακόπτη (synthetic)');
  await dialog.getByRole('button', { name: 'Διακοπή όλης της ΤΝ', exact: true }).click();
  await expect(page.getByText('Όλη η ΤΝ έχει σταματήσει')).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Ο διακόπτης ενημερώθηκε.' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Άρση διακόπτη', exact: true })).toBeVisible();
  await expect(log.getByRole('row').filter({ hasText: 'Διακόπτης ενεργός' })).toContainText('Ολοκληρώθηκε');
  for (const feature of FEATURES) expect((await resolveFeature(feature))['killSwitchActive'], `${feature} sees the kill switch`).toBe(true);

  // ---- 4. Clearing it (reason again) brings the screen and the gate back.
  await page.getByRole('button', { name: 'Άρση διακόπτη', exact: true }).click();
  await expect(dialog.getByText('Επανεκκίνηση λειτουργιών ΤΝ;')).toBeVisible();
  await dialog.getByRole('textbox', { name: /^Αιτιολογία/ }).fill('E2E-10 UI: άρση διακόπτη (synthetic)');
  await dialog.getByRole('button', { name: 'Άρση διακόπτη', exact: true }).click();
  await expect(page.getByText('Όλη η ΤΝ έχει σταματήσει')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Διακοπή όλης της ΤΝ', exact: true })).toBeVisible();
  await expect(log.getByRole('row').filter({ hasText: 'Άρση διακόπτη' })).toContainText('Ολοκληρώθηκε');
  await eventually('the gate reports the kill switch cleared', async () => {
    const states = await Promise.all(FEATURES.map((feature) => resolveFeature(feature)));
    return states.every((s) => s['killSwitchActive'] === false) ? true : undefined;
  });

  // The screen is still responsive and reflects the cleared state after a reload.
  await go(page, '/admin/ai');
  await alive(page);
  await expect(page.getByText('Ο καθολικός διακόπτης είναι ανενεργός.')).toBeVisible();
});
