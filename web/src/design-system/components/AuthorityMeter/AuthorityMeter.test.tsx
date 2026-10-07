import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { authorityStatus } from './authority';
import { AuthorityMeter } from './AuthorityMeter';

const NBSP = '\u00a0';

describe('authorityStatus', () => {
  it.each([
    [0, 100, 'success'],
    [79.99, 100, 'success'],
    [80, 100, 'warning'],
    [100, 100, 'warning'],
    [100.01, 100, 'danger'],
    [-9, 10, 'warning'],
    [-12, 10, 'danger'],
    [5, 0, 'danger'],
    [0, 0, 'success'],
  ] as const)('%s of %s → %s', (value, limit, expected) => {
    expect(authorityStatus(value, limit)).toBe(expected);
  });
});

describe('AuthorityMeter', () => {
  it('is a meter with a money value text', () => {
    renderWithDs(<AuthorityMeter label="Όριο εξουσιοδότησης" value="6480.00" limit="60000.00" />);
    const meter = screen.getByRole('meter', { name: 'Όριο εξουσιοδότησης' });
    expect(meter).toHaveAttribute('aria-valuetext', `6.480,00${NBSP}€ από 60.000,00${NBSP}€ όριο`);
    expect(meter).toHaveAttribute('aria-valuemin', '0');
    expect(meter).toHaveAttribute('aria-valuemax', '75000');
    expect(meter).toHaveAttribute('aria-valuenow', '6480');
    expect(meter).toHaveAttribute('data-status', 'success');
  });

  it('turns warning at 80 % and danger beyond the limit', () => {
    const { rerender } = renderWithDs(<AuthorityMeter label="Όριο" value="4800" limit="5000" />);
    expect(screen.getByRole('meter')).toHaveAttribute('data-status', 'warning');
    rerender(<AuthorityMeter label="Όριο" value="5200" limit="5000" />);
    expect(screen.getByRole('meter')).toHaveAttribute('data-status', 'danger');
    expect(screen.getByText('Πάνω από το όριο')).toBeInTheDocument();
  });

  it('formats percentages with U+202F and uses the absolute deviation', () => {
    renderWithDs(
      <AuthorityMeter
        label="Όριο σας ±10 %"
        value={-7.5}
        limit={10}
        format="percent"
        size="inline"
      />,
    );
    const meter = screen.getByRole('meter');
    expect(meter).toHaveAttribute('aria-valuetext', '7,50\u202f% από 10,00\u202f% όριο');
    expect(meter).toHaveAttribute('data-status', 'success');
    expect(meter).toHaveAttribute('data-size', 'inline');
  });

  it('follows the region format and the language', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<AuthorityMeter label="Authority" value="6480" limit="60000" />, {
      preferences: { regionFormat: 'en-GB' },
    });
    expect(screen.getByRole('meter')).toHaveAttribute(
      'aria-valuetext',
      '€6,480.00 of €60,000.00 limit',
    );
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <AuthorityMeter label="Όριο εξουσιοδότησης" value="64800" limit="60000" size="decision" />,
    );
    await expectNoA11yViolations(container);
  });
});
