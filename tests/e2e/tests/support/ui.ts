import { expect, type Page } from '@playwright/test';

// Shared browser helpers of the UI journeys (SL3-E2E-HARNESS).

/** The page answers within 5 s (no render loop). */
export async function alive(page: Page): Promise<void> {
  const answer = await Promise.race([page.evaluate(() => 'alive'), new Promise<string>((done) => setTimeout(() => done('frozen'), 5000))]);
  expect(answer, 'the page answers within 5 s (no render loop)').toBe('alive');
}

export async function go(page: Page, url: string): Promise<void> {
  await page.goto(url);
  await alive(page);
}

/** Dev sign-in page: picks the user by the EXACT display name (several names contain "Underwriter"; PITFALLS 30). */
export async function signInAs(page: Page, exactDisplayName: string): Promise<void> {
  await go(page, '/dev/sign-in');
  const signOut = page.getByRole('button', { name: 'Αποσύνδεση' });
  const picker = page.getByRole('button', { name: /Χρήστης/ });
  await expect(signOut.or(picker)).toBeVisible();
  if (await signOut.isVisible()) await signOut.click();
  await picker.click();
  await page.getByRole('option', { name: exactDisplayName, exact: true }).click();
  await page.getByRole('button', { name: 'Σύνδεση', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Συνδεθήκατε ως' })).toBeVisible();
}

// An IBAN (two letters, two check digits, then 11-30 alphanumerics) must only ever travel in a request body, never in a URL.
const IBAN_IN_URL = /[A-Z]{2}\d{2}[A-Z0-9]{11,30}/;

/** Starts recording every URL the page requests; call the returned check at the end of the journey. */
export function assertNoIbanInUrls(page: Page): () => void {
  const urls: string[] = [];
  page.on('request', (request) => urls.push(decodeURIComponent(request.url())));
  return () => {
    urls.push(decodeURIComponent(page.url()));
    const leaked = urls.filter((url) => IBAN_IN_URL.test(url));
    expect(leaked, 'no IBAN appears in any URL').toEqual([]);
  };
}
