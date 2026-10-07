import { screen, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import i18n from '../../i18n';
import { expectNoA11yViolations } from '../../test/axe';
import * as fx from '../../test/fixtures';
import { renderWithDs } from '../../test/render';
import { QuoteResult } from './QuoteResult';

const names = new Map([
  ['MTPL', 'Αστική ευθύνη αυτοκινήτου'],
  ['OWN-DAMAGE', 'Ίδιες ζημιές'],
]);

describe('QuoteResult', () => {
  it('shows the illustrative-tariff and provisional-tax warnings prominently, above the figures', async () => {
    const { container } = renderWithDs(
      <QuoteResult quote={fx.quote()} coverNames={names} productIsIllustrative />,
    );
    const illustrative = screen.getByText('Ενδεικτικό τιμολόγιο').closest('[role]');
    const provisional = screen.getByText('Προσωρινοί φόροι και εισφορές').closest('[role]');
    expect(illustrative).toHaveAttribute('role', 'status');
    expect(provisional).toHaveAttribute('role', 'status');
    expect(screen.getByText(/RAT-WARN-ILLUSTRATIVE-TARIFF/)).toBeInTheDocument();
    expect(screen.getByText(/RAT-WARN-PROVISIONAL-TAX/)).toBeInTheDocument();
    // Warnings come before the breakdown in document order.
    const breakdown = screen.getByRole('heading', { name: 'Ανάλυση ασφαλίστρου ανά κάλυψη' });
    expect(illustrative?.compareDocumentPosition(breakdown)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    await expectNoA11yViolations(container);
  });

  it('breaks the premium down per cover with its tax lines, marks provisional tax and shows the totals', () => {
    renderWithDs(
      <QuoteResult quote={fx.quote()} coverNames={names} productIsIllustrative={false} />,
    );
    const mtpl = screen.getByRole('grid', { name: 'Χρεώσεις κάλυψης Αστική ευθύνη αυτοκινήτου' });
    expect(within(mtpl).getByText('PREM-MTPL')).toBeInTheDocument();
    expect(within(mtpl).getByText('GR-IPT')).toBeInTheDocument();
    expect(within(mtpl).getByText('312,35 €')).toBeInTheDocument();
    expect(within(mtpl).getByText('46,85 €')).toBeInTheDocument();
    expect(within(mtpl).getByText(/Προσωρινό/)).toBeInTheDocument();
    const od = screen.getByRole('grid', { name: 'Χρεώσεις κάλυψης Ίδιες ζημιές' });
    expect(within(od).getByText('120,00 €')).toBeInTheDocument();
    const totals = screen.getByLabelText('Σύνολα προσφοράς');
    expect(within(totals).getByText('432,35 €')).toBeInTheDocument();
    expect(within(totals).getByText('46,85 €')).toBeInTheDocument();
    expect(within(totals).getByText('479,20 €')).toBeInTheDocument();
  });

  it('formats money by the region format and renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<QuoteResult quote={fx.quote()} coverNames={names} productIsIllustrative />, {
      preferences: { regionFormat: 'en-GB' },
    });
    expect(screen.getByText('Illustrative tariff')).toBeInTheDocument();
    expect(within(screen.getByLabelText('Quote totals')).getByText('€479.20')).toBeInTheDocument();
  });

  it('shows the underwriting outcome: accept, refer with reasons, decline', async () => {
    const { rerender, container } = renderWithDs(
      <QuoteResult quote={fx.quote()} coverNames={names} productIsIllustrative={false} />,
    );
    expect(screen.getByText('Αποδοχή')).toBeInTheDocument();
    expect(
      screen.getByText('Δεν υπάρχουν ανοιχτά ζητήματα ανάληψης κινδύνου.'),
    ).toBeInTheDocument();

    const issue = {
      issueId: 'i-1',
      issueType: 'REFERRAL',
      blockingPoint: 'PRE_BIND' as const,
      issueKey: 'UW-BUSINESS-USE',
      explanationKeys: ['pfc.question.usage.business'],
      approvalStatus: 'OPEN',
    };
    rerender(
      <QuoteResult
        quote={fx.quote({ decision: 'REFER', referred: true, bindable: false, issues: [issue] })}
        coverNames={names}
        productIsIllustrative={false}
      />,
    );
    expect(screen.getByText('Παραπομπή')).toBeInTheDocument();
    const grid = screen.getByRole('grid', { name: 'Ζητήματα ανάληψης κινδύνου' });
    expect(within(grid).getByText('UW-BUSINESS-USE')).toBeInTheDocument();
    expect(within(grid).getByText('Επαγγελματική χρήση του οχήματος')).toBeInTheDocument();
    expect(within(grid).getByText('Πριν τη δέσμευση')).toBeInTheDocument();

    rerender(
      <QuoteResult
        quote={fx.quote({ decision: 'DECLINE', bindable: false })}
        coverNames={names}
        productIsIllustrative={false}
      />,
    );
    expect(screen.getByText('Απόρριψη')).toBeInTheDocument();
    expect(screen.getByText(/Ο κίνδυνος απορρίπτεται/)).toBeInTheDocument();
    await expectNoA11yViolations(container);
  });
});
