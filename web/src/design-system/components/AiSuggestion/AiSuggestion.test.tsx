import { act, screen, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { clearAnnouncements } from '../../a11y/announce';
import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { AiCitation, AiSuggestionCard, ApproveTheDiff } from './index';

afterEach(() => {
  vi.useRealTimers();
  clearAnnouncements();
});

const body = (
  <p>
    Η ζημία αφορά υλικές ζημιές οχήματος
    <AiCitation n={1} source="Πραγματογνωμοσύνη" onPress={() => undefined} />.
  </p>
);

describe('AiSuggestionCard', () => {
  it('has «Πρόταση ΤΝ» in its name, a confidence chip and the decision actions', async () => {
    const onAccept = vi.fn();
    const onSources = vi.fn();
    const { user } = renderWithDs(
      <AiSuggestionCard
        state="proposed"
        confidence={0.91}
        onAccept={onAccept}
        onEdit={vi.fn()}
        onReject={vi.fn()}
        onWhy={vi.fn()}
        onSources={onSources}
        sourcesCount={4}
      >
        {body}
      </AiSuggestionCard>,
    );
    const card = screen.getByRole('group', { name: 'Πρόταση ΤΝ: Σύνοψη ΤΝ' });
    expect(card).toHaveTextContent('Υψηλή βεβαιότητα 0,91');
    expect(within(card).getByRole('link', { name: 'Πηγή 1: Πραγματογνωμοσύνη' })).toHaveTextContent(
      '[1]',
    );
    for (const name of ['Αποδοχή', 'Επεξεργασία', 'Απόρριψη', 'Γιατί;', 'Πηγές (4)']) {
      expect(within(card).getByRole('button', { name })).toBeInTheDocument();
    }
    await user.click(within(card).getByRole('button', { name: 'Αποδοχή' }));
    expect(onAccept).toHaveBeenCalledOnce();
    await user.click(within(card).getByRole('button', { name: 'Πηγές (4)' }));
    expect(onSources).toHaveBeenCalledOnce();
  });

  it('uses the warning family for low confidence and only a link until revealed', async () => {
    const { user, container } = renderWithDs(
      <AiSuggestionCard state="low-confidence" confidence={0.41} onAccept={vi.fn()}>
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.queryByRole('group')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Δείτε τη χαμηλής βεβαιότητας πρόταση' }));
    expect(screen.getByRole('group')).toHaveTextContent('Χαμηλή βεβαιότητα 0,41');
    expect(container.querySelector('[data-level="low"]')).not.toBeNull();
  });

  it('shows the generating state with «Διακοπή», then announces readiness', () => {
    vi.useFakeTimers();
    const onStop = vi.fn();
    const { rerender } = renderWithDs(<AiSuggestionCard state="generating" onStop={onStop} />);
    const card = screen.getByRole('group');
    expect(card).toHaveAttribute('aria-busy', 'true');
    expect(card).toHaveTextContent('Δημιουργείται…');
    expect(screen.getByRole('button', { name: 'Διακοπή' })).toBeInTheDocument();
    act(() => {
      vi.advanceTimersByTime(10_100);
    });
    expect(card).toHaveAttribute('data-thinking-stopped', 'true');
    rerender(<AiSuggestionCard state="proposed">{body}</AiSuggestionCard>);
    act(() => {
      vi.advanceTimersByTime(100);
    });
    expect(document.getElementById('ds-live-polite')).toHaveTextContent(
      'Η πρόταση ΤΝ είναι έτοιμη: Σύνοψη ΤΝ',
    );
  });

  it('shows receipts for accepted, edited and rejected suggestions', async () => {
    const onUndo = vi.fn();
    const onRejectReason = vi.fn();
    const receipt = { name: 'Κώστα Νικολάου', time: '14:32' };
    const { rerender, user } = renderWithDs(
      <AiSuggestionCard state="accepted" receipt={receipt} onUndo={onUndo}>
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.getByText('Αποδεκτή από Κώστα Νικολάου · 14:32')).toBeInTheDocument();
    expect(screen.getByText('Δημιουργήθηκε με ΤΝ')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Αναίρεση' }));
    expect(onUndo).toHaveBeenCalledOnce();

    rerender(
      <AiSuggestionCard state="edited" receipt={receipt}>
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.getByText('Τροποποιήθηκε')).toBeInTheDocument();

    rerender(
      <AiSuggestionCard
        state="rejected"
        receipt={receipt}
        rejectReasons={[{ id: 'wrong', label: 'Λάθος δεδομένα' }]}
        onRejectReason={onRejectReason}
      >
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.getByText('Απορρίφθηκε από Κώστα Νικολάου · 14:32')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Προσθήκη αιτίας απόρριψης' }));
    await user.click(await screen.findByRole('menuitem', { name: 'Λάθος δεδομένα' }));
    expect(onRejectReason).toHaveBeenCalledWith('wrong');
  });

  it('shows the expired banner with «Νέα πρόταση»', async () => {
    const onRefresh = vi.fn();
    const { user } = renderWithDs(
      <AiSuggestionCard state="expired" onRefresh={onRefresh}>
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.getByText('Τα δεδομένα άλλαξαν')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Νέα πρόταση' }));
    expect(onRefresh).toHaveBeenCalledOnce();
  });

  it('degrades without a retry loop on error and leaves no hole when disabled', () => {
    const { rerender, container } = renderWithDs(<AiSuggestionCard state="error" />);
    expect(
      screen.getByText('Η πρόταση δεν είναι διαθέσιμη· συνεχίστε χειροκίνητα'),
    ).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
    rerender(<AiSuggestionCard state="disabled">{body}</AiSuggestionCard>);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <AiSuggestionCard state="proposed" confidence={0.91}>
        {body}
      </AiSuggestionCard>,
    );
    expect(screen.getByRole('group', { name: 'AI suggestion: AI summary' })).toHaveTextContent(
      'High confidence 0,91',
    );
  });
});

describe('ApproveTheDiff', () => {
  const rows = [
    {
      id: 'r1',
      label: 'Απόθεμα · Υλικές ζημιές',
      from: '5.220,00 €',
      to: '6.480,00 €',
      delta: '+1.260,00 €',
    },
    { id: 'r2', label: 'Απόθεμα · Έξοδα', from: '300,00 €', to: '350,00 €' },
  ];

  it('selects rows with checkboxes and accepts the selection', async () => {
    const onAccept = vi.fn();
    const { user } = renderWithDs(
      <ApproveTheDiff rows={rows} onAccept={onAccept} onWhy={vi.fn()} />,
    );
    const group = screen.getByRole('group', { name: 'Αλλαγές προς αποδοχή' });
    const first = within(group).getByRole('checkbox', {
      name: 'Απόθεμα · Υλικές ζημιές: από 5.220,00 € σε 6.480,00 €',
    });
    expect(first).toBeChecked();
    expect(screen.getByRole('button', { name: 'Αποδοχή επιλεγμένων (2)' })).toBeInTheDocument();
    await user.click(first);
    expect(first).not.toBeChecked();
    await user.click(screen.getByRole('button', { name: 'Αποδοχή επιλεγμένων (1)' }));
    expect(onAccept).toHaveBeenCalledWith(['r2']);
  });

  it('toggles a row with Space and keeps the accept button focusable with a reason when none is selected', async () => {
    const onAccept = vi.fn();
    const { user } = renderWithDs(
      <ApproveTheDiff rows={rows.slice(0, 1)} onAccept={onAccept} neutral />,
    );
    await user.tab();
    const box = screen.getByRole('checkbox');
    expect(box).toHaveFocus();
    await user.keyboard(' ');
    expect(box).not.toBeChecked();
    const accept = screen.getByRole('button', { name: 'Αποδοχή επιλεγμένων (0)' });
    expect(accept).toHaveAttribute('aria-disabled', 'true');
    expect(accept).toHaveAccessibleDescription('Επιλέξτε τουλάχιστον μία αλλαγή');
    await user.click(accept);
    expect(onAccept).not.toHaveBeenCalled();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <AiSuggestionCard
          state="proposed"
          confidence={0.91}
          model="CLM-RES v4.2"
          onAccept={vi.fn()}
          onWhy={vi.fn()}
        >
          {body}
        </AiSuggestionCard>
        <AiSuggestionCard state="generating" onStop={vi.fn()} />
        <ApproveTheDiff rows={rows} onAccept={vi.fn()} onEdit={vi.fn()} />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
