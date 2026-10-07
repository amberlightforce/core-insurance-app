import { screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { ChoiceCard, Radio, RadioGroup } from './RadioGroup';

function PaymentPlans(props: { onChange?: (value: string) => void; isReadOnly?: boolean }) {
  return (
    <RadioGroup label="Πρόγραμμα πληρωμής" defaultValue="annual" {...props}>
      <Radio value="annual">Εφάπαξ</Radio>
      <Radio value="semi" description="2 δόσεις">
        Εξαμηνιαία
      </Radio>
      <Radio value="monthly" isDisabled>
        Μηνιαία
      </Radio>
      <Radio value="quarterly">Τριμηνιαία</Radio>
    </RadioGroup>
  );
}

describe('RadioGroup', () => {
  it('is a labelled radiogroup; Tab lands on the selected option', async () => {
    const { user } = renderWithDs(
      <>
        <button type="button">πριν</button>
        <PaymentPlans />
      </>,
    );
    expect(screen.getByRole('radiogroup', { name: 'Πρόγραμμα πληρωμής' })).toHaveAttribute(
      'aria-orientation',
      'vertical',
    );
    await user.tab();
    await user.tab();
    expect(screen.getByRole('radio', { name: 'Εφάπαξ' })).toHaveFocus();
  });

  it('moves and selects with the arrows, skipping disabled options', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<PaymentPlans onChange={onChange} />);
    await user.tab();
    await user.keyboard('{ArrowDown}');
    const semi = screen.getByRole('radio', { name: 'Εξαμηνιαία' });
    expect(semi).toHaveFocus();
    expect(semi).toBeChecked();
    expect(onChange).toHaveBeenLastCalledWith('semi');
    expect(semi).toHaveAccessibleDescription('2 δόσεις');
    await user.keyboard('{ArrowDown}');
    expect(screen.getByRole('radio', { name: 'Τριμηνιαία' })).toBeChecked();
    await user.keyboard('{ArrowUp}');
    expect(semi).toBeChecked();
  });

  it('shows a group error', () => {
    renderWithDs(
      <RadioGroup label="Χρήση" errorMessage="Επιλέξτε τη χρήση." isRequired>
        <Radio value="a">Ιδιωτική</Radio>
        <Radio value="b">Επαγγελματική</Radio>
      </RadioGroup>,
    );
    const group = screen.getByRole('radiogroup', { name: 'Χρήση υποχρεωτικό' });
    expect(group).toHaveAttribute('aria-invalid', 'true');
    expect(group).toHaveAccessibleDescription('Επιλέξτε τη χρήση.');
  });

  it('shows only the selected label when read-only', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<PaymentPlans isReadOnly onChange={onChange} />);
    const group = screen.getByRole('radiogroup');
    expect(group).toHaveAttribute('data-readonly', 'true');
    await user.tab();
    await user.keyboard('{ArrowDown}');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('renders horizontally for short options', () => {
    renderWithDs(
      <RadioGroup label="Κάλυψη" orientation="horizontal">
        <Radio value="y">Ναι</Radio>
        <Radio value="n">Όχι</Radio>
      </RadioGroup>,
    );
    expect(screen.getByRole('radiogroup')).toHaveAttribute('aria-orientation', 'horizontal');
  });

  it('renders in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <RadioGroup label="Plan" isRequired>
        <Radio value="a">Annual</Radio>
      </RadioGroup>,
    );
    expect(screen.getByRole('radiogroup', { name: 'Plan required' })).toBeInTheDocument();
  });
});

describe('ChoiceCard', () => {
  function Offerings({ onChange }: { onChange?: (value: string) => void }) {
    return (
      <RadioGroup label="Πακέτο" variant="cards" {...(onChange ? { onChange } : {})}>
        <ChoiceCard
          value="basic"
          title="Βασικό"
          price="286,40 €"
          priceCaption="ετησίως"
          bullets={['Αστική ευθύνη', 'Νομική προστασία', 'Οδική βοήθεια']}
        />
        <ChoiceCard
          value="comfort"
          title="Άνετο"
          price="412,38 €"
          tag="Συνιστάται"
          bullets={['Όλα του Βασικού', 'Θραύση κρυστάλλων', 'Πυρκαγιά']}
        />
        <ChoiceCard value="full" title="Πλήρες" price="689,10 €" bullets={['Μικτή ασφάλιση']} />
      </RadioGroup>
    );
  }

  it('names a card by title and price and describes it by its bullets', () => {
    renderWithDs(<Offerings />);
    const basic = screen.getByRole('radio', { name: 'Βασικό 286,40 €' });
    expect(basic).toHaveAccessibleDescription(/ετησίως.*Αστική ευθύνη/);
  });

  it('uses the radio keyboard: arrows move and select', async () => {
    const onChange = vi.fn();
    const { user } = renderWithDs(<Offerings onChange={onChange} />);
    await user.tab();
    expect(screen.getByRole('radio', { name: 'Βασικό 286,40 €' })).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    const comfort = screen.getByRole('radio', { name: 'Άνετο 412,38 €' });
    expect(comfort).toBeChecked();
    expect(comfort.closest('label')).toHaveAttribute('data-selected', 'true');
    expect(onChange).toHaveBeenLastCalledWith('comfort');
  });

  it('selects on click', async () => {
    const { user } = renderWithDs(<Offerings />);
    await user.click(screen.getByText('Πλήρες'));
    expect(screen.getByRole('radio', { name: 'Πλήρες 689,10 €' })).toBeChecked();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <PaymentPlans />
        <Offerings />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
