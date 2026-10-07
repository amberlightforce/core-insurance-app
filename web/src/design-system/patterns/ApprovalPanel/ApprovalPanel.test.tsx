import { screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { ApprovalPanel, type ApprovalPanelProps } from './ApprovalPanel';

const base: ApprovalPanelProps = {
  title: 'Πληρωμή αποζημίωσης · ΖΗΜ-2026-000318',
  maker: { id: 'u-maker', name: 'Μαρία Παπαδοπούλου', role: 'Χειρίστρια ζημιών' },
  submittedAt: '2026-10-07T11:02:00Z',
  justification: 'Συμφωνημένη προσφορά με τον ζημιωθέντα.',
  checkerGroup: 'Ομάδα Πληρωμών Β',
  currentUserId: 'u-checker',
  amount: { value: '1234.56' },
  authorityLimit: '60000',
  changes: [{ id: 'r', label: 'Απόθεμα · Υλικές ζημιές', from: '5.220,00 €', to: '6.480,00 €' }],
  isPayment: true,
  onApprove: vi.fn(),
  onReject: vi.fn(),
  onReturn: vi.fn(),
};

describe('ApprovalPanel', () => {
  it('shows the amount, the amount in words and a neutral diff with from/to for screen readers', () => {
    renderWithDs(<ApprovalPanel {...base} />);
    expect(screen.getByText('1.234,56 €')).toBeInTheDocument();
    expect(
      screen.getByText('Χίλια διακόσια τριάντα τέσσερα ευρώ και πενήντα έξι λεπτά'),
    ).toBeInTheDocument();
    const change = screen.getByText('Απόθεμα · Υλικές ζημιές').closest('li');
    expect(change).toHaveTextContent('από 5.220,00 € σε 6.480,00 €');
    expect(
      screen.getByText(/Αναμένει έγκριση · Ομάδα Πληρωμών Β · από 07\/10\/2026 14:02/),
    ).toBeInTheDocument();
  });

  it('approves with the commit button for payments', async () => {
    const onApprove = vi.fn();
    const { user } = renderWithDs(<ApprovalPanel {...base} onApprove={onApprove} />);
    const bar = screen.getByRole('group', { name: 'Απόφαση' });
    const approve = within(bar).getByRole('button', { name: /Έγκριση/ });
    expect(approve).toHaveAttribute('data-variant', 'commit');
    await user.click(approve);
    expect(onApprove).toHaveBeenCalledTimes(1);
  });

  it('requires a reason to reject', async () => {
    const onReject = vi.fn();
    const { user } = renderWithDs(<ApprovalPanel {...base} onReject={onReject} />);
    await user.click(screen.getByRole('button', { name: 'Απόρριψη…' }));
    const dialog = await screen.findByRole('alertdialog');
    const confirm = within(dialog).getByRole('button', { name: 'Απόρριψη αιτήματος' });
    expect(confirm).toHaveAttribute('aria-disabled', 'true');
    await user.click(confirm);
    expect(onReject).not.toHaveBeenCalled();
    await user.type(
      within(dialog).getByRole('textbox', { name: /Αιτιολογία/ }),
      'Λείπει τιμολόγιο',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Απόρριψη αιτήματος' }));
    expect(onReject).toHaveBeenCalledWith('Λείπει τιμολόγιο');
  });

  it('returns for changes with a reason', async () => {
    const onReturn = vi.fn();
    const { user } = renderWithDs(<ApprovalPanel {...base} onReturn={onReturn} />);
    await user.click(screen.getByRole('button', { name: 'Επιστροφή για διόρθωση…' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(
      within(dialog).getByRole('textbox', { name: /Αιτιολογία/ }),
      'Διορθώστε τον IBAN',
    );
    await user.click(within(dialog).getByRole('button', { name: 'Επιστροφή στον υποβάλλοντα' }));
    expect(onReturn).toHaveBeenCalledWith('Διορθώστε τον IBAN');
  });

  it('never lets the maker approve their own action', () => {
    renderWithDs(<ApprovalPanel {...base} currentUserId="u-maker" />);
    expect(screen.getByText('Δεν μπορείτε να εγκρίνετε δική σας ενέργεια')).toBeInTheDocument();
    expect(screen.queryByRole('group', { name: 'Απόφαση' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Έγκριση/ })).not.toBeInTheDocument();
  });

  it('offers escalation when the amount exceeds the checker authority', async () => {
    const onEscalate = vi.fn();
    const { user } = renderWithDs(
      <ApprovalPanel {...base} amount={{ value: '75000' }} onEscalate={onEscalate} />,
    );
    expect(screen.getByText('Το ποσό υπερβαίνει το όριο εξουσιοδότησής σας')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Έγκριση/ })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Προώθηση σε ανώτερο' }));
    expect(onEscalate).toHaveBeenCalled();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(<ApprovalPanel {...base} />);
    await expectNoA11yViolations(container);
  });
});
