import { act, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { clearAnnouncements } from '../../a11y/announce';
import { Badge } from './Badge';
import { StatusPill } from './StatusPill';
import { allStatusDefinitions, getStatusDefinition, toEntityStatus } from './statusMap';

afterEach(() => {
  clearAnnouncements();
  vi.useRealTimers();
});

describe('status map', () => {
  it('has a Greek and an English label for every state', () => {
    for (const { id, definition } of allStatusDefinitions()) {
      for (const lng of ['el', 'en']) {
        expect(
          i18n.exists(definition.labelKey, { ns: 'ds', lng, fallbackLng: false }),
          `${lng} ${id}`,
        ).toBe(true);
      }
    }
  });

  it('reserves the solid pill for breached, conflict and screening true match', () => {
    const solid = allStatusDefinitions()
      .filter((s) => s.definition.solid)
      .map((s) => s.id)
      .sort();
    expect(solid).toEqual([
      'clockInstance.breached',
      'screeningResult.trueMatch',
      'semantic.breached',
      'semantic.conflict',
    ]);
    for (const s of allStatusDefinitions().filter((d) => d.definition.solid)) {
      expect(s.definition.family).toBe('danger');
    }
  });

  it('maps the brief shorthand families', () => {
    expect(getStatusDefinition({ entity: 'job', state: 'quoted' }).family).toBe('info');
    expect(getStatusDefinition({ entity: 'job', state: 'bound' }).family).toBe('success');
    expect(getStatusDefinition({ entity: 'policyTerm', state: 'lapsed' }).family).toBe('ochre');
    expect(getStatusDefinition({ entity: 'policyTerm', state: 'cancelled' }).family).toBe('danger');
    expect(getStatusDefinition({ entity: 'job', state: 'referred' }).family).toBe('plum');
  });

  it('narrows untyped API values', () => {
    expect(toEntityStatus('invoice', 'paid')).toEqual({ entity: 'invoice', state: 'paid' });
    expect(toEntityStatus('invoice', 'inForce')).toBeNull();
  });
});

describe('StatusPill', () => {
  it('renders colour family, icon and Greek label', () => {
    renderWithDs(<StatusPill entity="policyTerm" state="inForce" />);
    const label = screen.getByText('Σε ισχύ');
    const pill = label.closest('[data-family]');
    expect(pill).toHaveAttribute('data-family', 'success');
    expect(pill).toHaveAttribute('data-variant', 'status');
    expect(pill?.querySelector('svg')).toHaveAttribute('aria-hidden', 'true');
  });

  it('renders the English label', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<StatusPill entity="policyTerm" state="pendingCancellation" />);
    expect(screen.getByText('Pending cancellation')).toBeInTheDocument();
  });

  it('renders solid only for the critical states', () => {
    renderWithDs(
      <>
        <StatusPill semantic="breached" />
        <StatusPill entity="invoice" state="overdue" variant="status-solid" />
        <StatusPill entity="screeningResult" state="trueMatch" />
        <StatusPill semantic="ai-generated" />
      </>,
    );
    expect(screen.getByText('Παραβίαση προθεσμίας').closest('[data-variant]')).toHaveAttribute(
      'data-variant',
      'status-solid',
    );
    // Overdue is not allowed to be solid: falls back to the family pill.
    expect(screen.getByText('Ληξιπρόθεσμο').closest('[data-variant]')).toHaveAttribute(
      'data-variant',
      'status',
    );
    expect(
      screen.getByText('Επιβεβαιωμένη αντιστοίχιση').closest('[data-variant]'),
    ).toHaveAttribute('data-variant', 'status-solid');
    expect(screen.getByText('Δημιουργήθηκε με ΤΝ').closest('[data-variant]')).toHaveAttribute(
      'data-variant',
      'status-outline',
    );
  });

  it('shows a sub-label and a countdown with a full accessible text', () => {
    renderWithDs(
      <StatusPill semantic="pending-approval" subLabel="Ομάδα Β" countdownDays={6} size="sm" />,
    );
    expect(screen.getByText('Αναμένει έγκριση')).toBeInTheDocument();
    expect(screen.getByText('· Ομάδα Β')).toBeInTheDocument();
    expect(screen.getByText('· 6 ημ.')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('· 6 ημέρες')).toHaveClass('ds-visually-hidden');
  });

  it('`text` replaces the status word and keeps the status family', () => {
    renderWithDs(<StatusPill semantic="success" text="Νέα παραγωγή" />);
    expect(screen.getByText('Νέα παραγωγή').closest('[data-family]')).toHaveAttribute(
      'data-family',
      'success',
    );
    expect(screen.queryByText('Επιτυχία')).not.toBeInTheDocument();
  });

  it('dot variant keeps the label in the accessible name and a tooltip', async () => {
    const { user } = renderWithDs(<StatusPill entity="job" state="referred" variant="dot" />);
    const dot = screen.getByRole('img', { name: 'Κατάσταση: Σε παραπομπή' });
    await user.tab();
    expect(dot).toHaveFocus();
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Σε παραπομπή');
  });

  it('dot variant with a visible label', () => {
    renderWithDs(<StatusPill entity="job" state="bound" variant="dot" showLabel />);
    expect(screen.getByText('Συνάφθηκε')).toBeInTheDocument();
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('is never interactive', () => {
    renderWithDs(<StatusPill entity="claim" state="open" />);
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(screen.getByText('Ανοιχτή').closest('[tabindex]')).toBeNull();
  });

  it('announces a change politely and animates it, but not on first render', () => {
    vi.useFakeTimers();
    const { rerender } = renderWithDs(<StatusPill entity="policyTerm" state="scheduled" />);
    act(() => {
      vi.advanceTimersByTime(100);
    });
    const region = document.getElementById('ds-live-polite');
    expect(region?.textContent ?? '').toBe('');

    rerender(<StatusPill entity="policyTerm" state="inForce" />);
    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(document.getElementById('ds-live-polite')).toHaveTextContent('Κατάσταση: Σε ισχύ');
    expect(screen.getByText('Σε ισχύ').closest('[data-changed]')).not.toBeNull();
  });

  it('shows a spinner while the status is changing', () => {
    renderWithDs(<StatusPill entity="outboundDocument" state="rendering" isPending />);
    const pill = screen.getByText('Δημιουργείται').closest('[data-variant]');
    expect(pill).toHaveAttribute('aria-busy', 'true');
    expect(pill?.querySelector('.ds-spin')).not.toBeNull();
  });

  it('has no axe violations', async () => {
    renderWithDs(
      <div>
        <StatusPill entity="policyTerm" state="inForce" />
        <StatusPill semantic="conflict" />
        <StatusPill entity="clockInstance" state="warned" countdownDays={2} />
        <StatusPill entity="job" state="quoted" variant="dot" />
        <Badge count={3} />
      </div>,
    );
    await expectNoA11yViolations();
  });
});

describe('Badge', () => {
  it('shows the count with tabular grouping', () => {
    renderWithDs(<Badge count={7} />);
    expect(screen.getByText('7').closest('[data-tone]')).toHaveAttribute('data-tone', 'neutral');
  });

  it('caps at 99+ and keeps a readable name', () => {
    renderWithDs(<Badge count={240} tone="attention" />);
    expect(screen.getByText('99+')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('Περισσότερα από 99')).toBeInTheDocument();
    expect(screen.getByText('99+').closest('[data-tone]')).toHaveAttribute(
      'data-tone',
      'attention',
    );
  });

  it('uses the given accessible label', () => {
    renderWithDs(<Badge count={3} label="3 μη αναγνωσμένες ειδοποιήσεις" />);
    expect(screen.getByText('3')).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByText('3 μη αναγνωσμένες ειδοποιήσεις')).toBeInTheDocument();
  });
});
