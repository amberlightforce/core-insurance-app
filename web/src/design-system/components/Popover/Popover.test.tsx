import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { ModalDepthContext } from '../Dialog/modalContext';
import { ExplainWhy } from './ExplainWhy';
import { formatSigned } from './formatSigned';
import { HoverCard } from './HoverCard';
import { Popover } from './Popover';

function FilterPopover(props: { status?: 'ready' | 'loading' | 'error'; onRetry?: () => void }) {
  return (
    <>
      <Popover
        trigger={<Button>Φίλτρα</Button>}
        title="Φίλτρο κατάστασης"
        size="filters"
        {...props}
      >
        <label>
          Κατάσταση
          <input />
        </label>
        <Button>Εφαρμογή</Button>
      </Popover>
      <Button>Επόμενο</Button>
    </>
  );
}

describe('Popover', () => {
  it('opens a labelled non-modal dialog and moves focus to the first field', async () => {
    const { user } = renderWithDs(<FilterPopover />);
    const trigger = screen.getByRole('button', { name: 'Φίλτρα' });
    expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
    expect(trigger).toHaveAttribute('aria-expanded', 'false');

    await user.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Φίλτρο κατάστασης' });
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('textbox', { name: 'Κατάσταση' })).toHaveFocus();
    const surface = dialog.closest('[data-material]');
    expect(surface).toHaveAttribute('data-material', 'popover');
    expect(surface).toHaveAttribute('data-size', 'filters');
    expect(surface).not.toHaveAttribute('data-solid');
  });

  it('closes with Esc and returns focus to the trigger', async () => {
    const { user } = renderWithDs(<FilterPopover />);
    const trigger = screen.getByRole('button', { name: 'Φίλτρα' });
    await user.click(trigger);
    await screen.findByRole('dialog');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('closes when Tab moves past the last element', async () => {
    const { user } = renderWithDs(<FilterPopover />);
    await user.click(screen.getByRole('button', { name: 'Φίλτρα' }));
    await screen.findByRole('dialog');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Εφαρμογή' })).toHaveFocus();
    await user.tab();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
  });

  it('shows a busy skeleton while loading and an error with retry', async () => {
    const onRetry = vi.fn();
    const { user, rerender } = renderWithDs(<FilterPopover status="loading" />);
    await user.click(screen.getByRole('button', { name: 'Φίλτρα' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.querySelector('[aria-busy="true"]')).not.toBeNull();
    expect(screen.getByText('Φόρτωση…')).toBeInTheDocument();

    rerender(<FilterPopover status="error" onRetry={onRetry} />);
    expect(screen.getByRole('alert')).toHaveTextContent('Δεν ήταν δυνατή η φόρτωση');
    await user.click(screen.getByRole('button', { name: 'Επανάληψη' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('uses the solid material inside a modal', async () => {
    const { user } = renderWithDs(
      <ModalDepthContext value={1}>
        <FilterPopover />
      </ModalDepthContext>,
    );
    await user.click(screen.getByRole('button', { name: 'Φίλτρα' }));
    const dialog = await screen.findByRole('dialog');
    expect(dialog.closest('[data-material]')).toHaveAttribute('data-solid', 'true');
  });

  it('labels the close button in English', async () => {
    await i18n.changeLanguage('en');
    const { user } = renderWithDs(
      <Popover trigger={<Button>Filters</Button>} title="Status filter">
        <p>Body</p>
      </Popover>,
    );
    await user.click(screen.getByRole('button', { name: 'Filters' }));
    expect(await screen.findByRole('button', { name: 'Close' })).toBeInTheDocument();
  });

  it('has no axe violations when open', async () => {
    const { user } = renderWithDs(<FilterPopover />);
    await user.click(screen.getByRole('button', { name: 'Φίλτρα' }));
    await screen.findByRole('dialog');
    await expectNoA11yViolations(document.body);
  });
});

describe('HoverCard', () => {
  function Card(props: { onOpenInNewTab?: () => void; onOpenChange?: (open: boolean) => void }) {
    return (
      <HoverCard
        label="ΑΣΦ-2026-004471"
        href="#policy"
        title="Ασφαλιστήριο ΑΣΦ-2026-004471"
        {...props}
      >
        <p>Μαρία Παπαδοπούλου · Αυτοκίνητο ΙΧ</p>
      </HoverCard>
    );
  }

  it('opens with Space on the focused link without taking focus and closes with Esc', async () => {
    const onOpenChange = vi.fn();
    const { user } = renderWithDs(<Card onOpenChange={onOpenChange} />);
    const link = screen.getByRole('link', { name: 'ΑΣΦ-2026-004471' });
    expect(link).toHaveAttribute('aria-haspopup', 'dialog');
    await user.tab();
    expect(link).toHaveFocus();
    await user.keyboard(' ');
    const card = await screen.findByRole('dialog', { name: 'Ασφαλιστήριο ΑΣΦ-2026-004471' });
    expect(link).toHaveAttribute('aria-expanded', 'true');
    expect(link).toHaveAttribute('aria-controls', card.id);
    expect(link).toHaveFocus();
    expect(onOpenChange).toHaveBeenLastCalledWith(true);

    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    });
    expect(link).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens after hovering for 500 ms', async () => {
    const { user } = renderWithDs(<Card />);
    await user.hover(screen.getByRole('link'));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(await screen.findByRole('dialog', {}, { timeout: 1500 })).toBeInTheDocument();
  });

  it('opens in a new tab with Ctrl+Enter', async () => {
    const onOpenInNewTab = vi.fn();
    const { user } = renderWithDs(<Card onOpenInNewTab={onOpenInNewTab} />);
    await user.tab();
    await user.keyboard('{Control>}{Enter}{/Control}');
    expect(onOpenInNewTab).toHaveBeenCalledTimes(1);
  });
});

describe('ExplainWhy', () => {
  const factors = [
    { label: 'Κάλυψη υλικών ζημιών', value: 4200, display: '+4.200,00 €' },
    { label: 'Ιστορικό ζημιών', value: 2600 },
    { label: 'Απαλλαγή', value: -320 },
  ];

  it('formats signed values with a true minus', () => {
    expect(formatSigned(-320, 'el-GR')).toBe(`${String.fromCharCode(0x2212)}320`);
    expect(formatSigned(2600, 'el-GR')).toBe('+2.600');
  });

  it('explains a figure with factor rows, sources and the full sheet link', async () => {
    const onViewFullSheet = vi.fn();
    const { user } = renderWithDs(
      <ExplainWhy
        value="6.480,00 €"
        basis="Μοντέλο CLM-RES v4.2 · βεβαιότητα 0,91"
        factors={factors}
        sources={[{ label: 'Έκθεση πραγματογνώμονα, σελ. 3', href: '#doc' }]}
        onViewFullSheet={onViewFullSheet}
      />,
    );
    const trigger = screen.getByRole('button', { name: 'Γιατί 6.480,00 €;' });
    expect(trigger).toHaveTextContent('Γιατί;');
    await user.click(trigger);
    const dialog = await screen.findByRole('dialog', { name: 'Γιατί 6.480,00 €;' });
    expect(dialog).toHaveTextContent('Μοντέλο CLM-RES v4.2 · βεβαιότητα 0,91');

    const list = screen.getByRole('list', { name: 'Βασικοί παράγοντες' });
    const rows = list.querySelectorAll('li');
    expect(rows).toHaveLength(3);
    expect(rows[0]).toHaveTextContent('+4.200,00 €');
    expect(rows[2]).toHaveTextContent(`${String.fromCharCode(0x2212)}320`);
    expect(rows[2]?.querySelector('[data-sign]')).toHaveAttribute('data-sign', 'negative');

    expect(
      screen.getByRole('link', { name: 'Έκθεση πραγματογνώμονα, σελ. 3' }),
    ).toBeInTheDocument();
    await user.click(screen.getByRole('link', { name: 'Προβολή πλήρους φύλλου υπολογισμού' }));
    expect(onViewFullSheet).toHaveBeenCalledTimes(1);
  });

  it('has no axe violations when open', async () => {
    const { user } = renderWithDs(<ExplainWhy value="412,38 €" factors={factors} />);
    await user.click(screen.getByRole('button', { name: 'Γιατί 412,38 €;' }));
    await screen.findByRole('dialog');
    await expectNoA11yViolations(document.body);
  });
});
