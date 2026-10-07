import { screen, waitFor } from '@testing-library/react';
import { List, Map as MapIcon, Table } from 'lucide-react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { SegmentedControl, type SegmentOption } from './SegmentedControl';

const views: SegmentOption[] = [
  { id: 'list', label: 'Λίστα', icon: List },
  { id: 'table', label: 'Πίνακας', icon: Table },
  { id: 'map', label: 'Χάρτης', icon: MapIcon, disabledReason: 'Ο χάρτης δεν είναι διαθέσιμος' },
];

describe('SegmentedControl', () => {
  it('has radiogroup semantics with one radio per segment', () => {
    renderWithDs(<SegmentedControl aria-label="Προβολή" options={views} />);
    const group = screen.getByRole('radiogroup', { name: 'Προβολή' });
    expect(group).toHaveAttribute('aria-orientation', 'horizontal');
    expect(screen.getAllByRole('radio')).toHaveLength(3);
    expect(screen.getByRole('radio', { name: 'Λίστα' })).toBeChecked();
  });

  it('moves and selects with the arrows and slides the thumb', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <SegmentedControl aria-label="Προβολή" options={views} onChange={onChange} />,
    );
    await user.tab();
    await user.keyboard('{ArrowRight}');
    const table = screen.getByRole('radio', { name: 'Πίνακας' });
    expect(table).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith('table');
    const track = table.closest<HTMLElement>('[data-size]');
    expect(track?.style.getPropertyValue('--_index')).toBe('1');
  });

  it('reaches a disabled-with-reason segment but never selects it', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(
      <SegmentedControl
        aria-label="Προβολή"
        options={views}
        defaultValue="table"
        onChange={onChange}
      />,
    );
    await user.tab();
    await user.keyboard('{ArrowRight}');
    const map = screen.getByRole('radio', { name: 'Χάρτης' });
    expect(map).toHaveFocus();
    expect(map).toHaveAttribute('aria-disabled', 'true');
    expect(map).not.toBeChecked();
    expect(map).toHaveAccessibleDescription('Ο χάρτης δεν είναι διαθέσιμος');
    expect(await screen.findByRole('tooltip')).toHaveTextContent('Ο χάρτης δεν είναι διαθέσιμος');
    expect(screen.getByRole('radio', { name: 'Πίνακας' })).toBeChecked();
    expect(onChange).not.toHaveBeenCalled();
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('tooltip')).not.toBeInTheDocument();
    });
  });

  it('shows a visible label and the read-only state as text', () => {
    const { rerender } = renderWithDs(<SegmentedControl label="Περίοδος" options={views} />);
    expect(screen.getByRole('radiogroup', { name: 'Περίοδος' })).toBeInTheDocument();
    rerender(<SegmentedControl label="Περίοδος" options={views} value="table" isReadOnly />);
    expect(screen.queryByRole('radiogroup')).not.toBeInTheDocument();
    expect(screen.getByText('Πίνακας')).toBeInTheDocument();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <SegmentedControl
        aria-label="View"
        options={[
          { id: 'm', label: 'Monthly' },
          { id: 'q', label: 'Quarterly' },
        ]}
      />,
    );
    expect(screen.getByRole('radio', { name: 'Monthly' })).toBeChecked();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <SegmentedControl label="Προβολή" options={views} size="sm" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
