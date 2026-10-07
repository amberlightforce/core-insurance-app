import { renderHook, screen } from '@testing-library/react';
import { Input, TextField } from 'react-aria-components';
import { describe, expect, it } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { FieldChromeLabel, FieldMessageList } from './FieldChrome';
import { useFieldMessages, type FieldChromeMessagesInput } from './useFieldMessages';

function Field(props: FieldChromeMessagesInput & { isRequired?: boolean }) {
  const messages = useFieldMessages(props);
  return (
    <TextField
      isRequired={props.isRequired ?? false}
      isInvalid={messages.showError}
      validationBehavior="aria"
      {...(messages.describedBy ? { 'aria-describedby': messages.describedBy } : {})}
    >
      <FieldChromeLabel isRequired={props.isRequired}>Αριθμός πλαισίου</FieldChromeLabel>
      <Input />
      <FieldMessageList messages={messages} {...props} />
    </TextField>
  );
}

describe('useFieldMessages', () => {
  it('orders the description ids: error, warning, info, reason, helper, extra', () => {
    const { result } = renderHook(() =>
      useFieldMessages({
        errorMessage: 'Λάθος',
        info: 'Πληροφορία',
        disabledReason: 'Κλειδωμένο',
        extraDescribedBy: 'unit',
      }),
    );
    const ids = result.current.describedBy?.split(' ') ?? [];
    expect(ids).toEqual([
      result.current.errorId,
      result.current.infoId,
      result.current.reasonId,
      'unit',
    ]);
  });

  it('shows the helper OR the error, and the warning only without an error', () => {
    const initialProps: FieldChromeMessagesInput = { description: 'Βοήθεια', warning: 'Προσοχή' };
    const { result, rerender } = renderHook(
      (input: FieldChromeMessagesInput) => useFieldMessages(input),
      { initialProps },
    );
    expect(result.current.showHelper).toBe(false);
    expect(result.current.showWarning).toBe(true);
    rerender({ description: 'Βοήθεια', warning: 'Προσοχή', errorMessage: 'Λάθος' });
    expect(result.current.showWarning).toBe(false);
    expect(result.current.showError).toBe(true);
    rerender({ description: 'Βοήθεια' });
    expect(result.current.showHelper).toBe(true);
    expect(result.current.describedBy).toBe(result.current.helperId);
  });
});

describe('FieldChromeLabel / FieldMessageList', () => {
  it('names the control with «υποχρεωτικό» and hides the asterisk', () => {
    renderWithDs(<Field isRequired description="17 χαρακτήρες" />);
    const input = screen.getByRole('textbox', { name: 'Αριθμός πλαισίου υποχρεωτικό' });
    expect(input).toHaveAttribute('aria-required', 'true');
    expect(input).toHaveAccessibleDescription('17 χαρακτήρες');
    expect(screen.getByText('*')).toHaveAttribute('aria-hidden', 'true');
  });

  it('describes an error first and marks the control invalid', () => {
    renderWithDs(
      <Field
        description="17 χαρακτήρες"
        errorMessage="Ο αριθμός πλαισίου έχει 16 χαρακτήρες. Συμπληρώστε 17."
        info="Από gov.gr Wallet"
      />,
    );
    const input = screen.getByRole('textbox');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription(
      'Ο αριθμός πλαισίου έχει 16 χαρακτήρες. Συμπληρώστε 17. Από gov.gr Wallet',
    );
  });

  it('renders a plain label with an id for read-only displays', () => {
    renderWithDs(<FieldChromeLabel id="lbl">Ημερομηνία</FieldChromeLabel>);
    expect(document.getElementById('lbl')).toHaveTextContent('Ημερομηνία');
  });

  it('uses English text for «required»', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Field isRequired />);
    expect(screen.getByRole('textbox', { name: 'Αριθμός πλαισίου required' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <Field isRequired warning="Ο αριθμός δεν βρέθηκε στο μητρώο" description="17 χαρακτήρες" />,
    );
    await expectNoA11yViolations(container);
  });
});
