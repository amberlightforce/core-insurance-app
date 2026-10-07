import { act, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Radio, RadioGroup } from '../RadioGroup';
import { Select } from '../Select';
import { TextField } from '../TextField';
import { ErrorSummary } from './ErrorSummary';

const dateMessage = 'Συμπληρώστε την ημερομηνία ζημίας.';
const afmMessage = 'Ο ΑΦΜ πρέπει να έχει 9 ψηφία. Ελέγξτε τον αριθμό.';
const errors = [
  { fieldId: 'loss-date', message: dateMessage },
  { fieldId: 'afm', message: afmMessage },
];

describe('ErrorSummary', () => {
  it('renders nothing without errors', () => {
    const { container } = renderWithDs(<ErrorSummary errors={[]} />);
    expect(container).toBeEmptyDOMElement();
  });

  it('takes focus when shown and is titled with the error count', () => {
    renderWithDs(<ErrorSummary errors={errors} />);
    const region = screen.getByRole('region', {
      name: 'Διορθώστε 2 σφάλματα για να συνεχίσετε',
    });
    expect(region).toHaveFocus();
    expect(screen.getByRole('heading', { level: 2 })).toHaveTextContent('2 σφάλματα');
  });

  it('uses the singular for one error', () => {
    renderWithDs(<ErrorSummary errors={errors.slice(0, 1)} headingLevel={3} />);
    expect(screen.getByRole('heading', { level: 3 })).toHaveTextContent(
      'Διορθώστε 1 σφάλμα για να συνεχίσετε',
    );
  });

  it('moves focus to the field from each link', async () => {
    const onNavigate = vi.fn();
    const { user } = renderWithDs(
      <>
        <ErrorSummary errors={errors} onNavigate={onNavigate} />
        <TextField id="loss-date" label="Ημερομηνία ζημίας" errorMessage={dateMessage} />
        <TextField id="afm" label="ΑΦΜ" errorMessage={afmMessage} />
      </>,
    );
    const link = screen.getByRole('link', { name: afmMessage });
    expect(link).toHaveAttribute('href', '#afm');
    await user.click(link);
    expect(screen.getByRole('textbox', { name: 'ΑΦΜ' })).toHaveFocus();
    expect(onNavigate).toHaveBeenCalledWith('afm');
    act(() => {
      screen.getByRole('link', { name: dateMessage }).focus();
    });
    await user.keyboard('{Enter}');
    expect(screen.getByRole('textbox', { name: 'Ημερομηνία ζημίας' })).toHaveFocus();
  });

  it('focuses the first control of a group and the trigger of a select', async () => {
    const { user } = renderWithDs(
      <>
        <ErrorSummary
          errors={[
            { fieldId: 'usage', message: 'Επιλέξτε τη χρήση.' },
            { fieldId: 'plan', message: 'Επιλέξτε πρόγραμμα.' },
          ]}
        />
        <Select
          id="usage"
          label="Χρήση"
          options={[{ id: 'a', label: 'Ιδιωτική' }]}
          errorMessage="Επιλέξτε τη χρήση."
        />
        <RadioGroup id="plan" label="Πρόγραμμα" errorMessage="Επιλέξτε πρόγραμμα.">
          <Radio value="a">Εφάπαξ</Radio>
          <Radio value="b">Δόσεις</Radio>
        </RadioGroup>
      </>,
    );
    await user.click(screen.getByRole('link', { name: 'Επιλέξτε πρόγραμμα.' }));
    expect(screen.getByRole('radio', { name: 'Εφάπαξ' })).toHaveFocus();
    await user.click(screen.getByRole('link', { name: 'Επιλέξτε τη χρήση.' }));
    expect(screen.getByRole('button', { name: /Χρήση/ })).toHaveFocus();
  });

  it('refocuses on each failed submit', () => {
    const { rerender } = renderWithDs(<ErrorSummary errors={errors} focusKey={1} />);
    const region = screen.getByRole('region');
    region.blur();
    expect(region).not.toHaveFocus();
    rerender(<ErrorSummary errors={errors} focusKey={2} />);
    expect(region).toHaveFocus();
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<ErrorSummary errors={errors.slice(0, 1)} />);
    expect(screen.getByRole('region', { name: 'Fix 1 error to continue' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <>
        <ErrorSummary errors={errors} />
        <TextField id="loss-date" label="Ημερομηνία ζημίας" />
        <TextField id="afm" label="ΑΦΜ" />
      </>,
    );
    await expectNoA11yViolations(container);
  });
});
