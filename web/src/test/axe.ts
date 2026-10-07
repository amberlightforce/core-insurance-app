import axe from 'axe-core';
import { expect } from 'vitest';

/**
 * Runs axe-core on a rendered tree and fails on any violation. Colour contrast is excluded because
 * jsdom does not compute styles (contrast is verified by the guide's contrast.py and by
 * @axe-core/playwright in the e2e suite).
 */
export async function expectNoA11yViolations(container: Element = document.body): Promise<void> {
  const results = await axe.run(container, {
    rules: {
      'color-contrast': { enabled: false },
      // Fragments rendered in isolation have no landmarks; the shell test covers them.
      region: { enabled: false },
    },
  });
  const summary = results.violations.map(
    (v) => `${v.id}: ${v.help} → ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`,
  );
  expect(summary).toEqual([]);
}
