import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { formatPercent } from './formatPercent';
import { ProgressBar, ProgressRing } from './Progress';

const NARROW_NBSP = String.fromCharCode(0x202f);

describe('formatPercent', () => {
  it('puts a narrow no-break space before % in Greek only', () => {
    expect(formatPercent(0.62, 'el-GR')).toBe(`62${NARROW_NBSP}%`);
    expect(formatPercent(0.62, 'en-GB')).toBe('62%');
  });
});

describe('ProgressBar', () => {
  it('exposes progressbar semantics with value, range and value text', () => {
    renderWithDs(
      <ProgressBar
        label="Πρόοδος ημέρας"
        value={31}
        maxValue={50}
        showCount
        valueText="31 από 50 εργασίες"
      />,
    );
    const bar = screen.getByRole('progressbar', { name: 'Πρόοδος ημέρας' });
    expect(bar).toHaveAttribute('aria-valuenow', '31');
    expect(bar).toHaveAttribute('aria-valuemin', '0');
    expect(bar).toHaveAttribute('aria-valuemax', '50');
    expect(bar).toHaveAttribute('aria-valuetext', '31 από 50 εργασίες');
    expect(bar.textContent).toContain(`62${NARROW_NBSP}%`);
    expect(bar).toHaveTextContent('31 από 50');
  });

  it('marks completion at 100 % and draws the end-cap check', () => {
    const { container } = renderWithDs(<ProgressBar label="Μεταφόρτωση" value={100} />);
    expect(screen.getByRole('progressbar')).toHaveAttribute('data-complete', 'true');
    expect(container.querySelector('svg')).not.toBeNull();
  });

  it('omits aria-valuenow when indeterminate', () => {
    renderWithDs(<ProgressBar aria-label="Φόρτωση αποτελεσμάτων" isIndeterminate />);
    const bar = screen.getByRole('progressbar', { name: 'Φόρτωση αποτελεσμάτων' });
    expect(bar).not.toHaveAttribute('aria-valuenow');
    expect(bar).toHaveAttribute('data-size', 'indeterminate');
  });

  it('renders the count in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<ProgressBar label="Day progress" value={11} maxValue={19} showCount />);
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuetext', '11 of 19');
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <ProgressBar label="Πρόοδος" value={40} />
        <ProgressBar aria-label="Φόρτωση" isIndeterminate />
        <ProgressRing aria-label="Βήματα" value={3} maxValue={4} size={40} />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});

describe('ProgressRing', () => {
  it('shows the value in the centre from 40 px', () => {
    const { rerender } = renderWithDs(<ProgressRing aria-label="Βήματα" value={50} size={24} />);
    expect(screen.getByRole('progressbar', { name: 'Βήματα' })).not.toHaveTextContent('50');
    rerender(<ProgressRing aria-label="Βήματα" value={50} size={64} />);
    expect(screen.getByRole('progressbar', { name: 'Βήματα' }).textContent).toContain(
      `50${NARROW_NBSP}%`,
    );
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
  });
});
