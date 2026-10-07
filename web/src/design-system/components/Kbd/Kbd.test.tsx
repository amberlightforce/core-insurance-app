import { describe, expect, it } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Kbd } from './Kbd';

describe('Kbd', () => {
  it('renders one <kbd> per key and maps Mod and Enter', () => {
    const { container } = renderWithDs(<Kbd shortcut="Mod+Shift+Enter" />);
    const keys = Array.from(container.querySelectorAll('kbd kbd, kbd')).filter(
      (el) => el.children.length === 0,
    );
    expect(keys.map((k) => k.textContent)).toEqual(['Ctrl', 'Shift', '↵']);
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(<Kbd shortcut="Mod+K" tone="inverse" />);
    await expectNoA11yViolations(container);
  });
});
