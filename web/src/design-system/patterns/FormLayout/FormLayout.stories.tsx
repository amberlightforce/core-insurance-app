import type { Meta, StoryObj } from '@storybook/react-vite';
import { useState, type SyntheticEvent } from 'react';

import { Button } from '../../components/Button';
import { Checkbox, CheckboxGroup } from '../../components/Checkbox';
import { ErrorSummary, type ErrorSummaryItem } from '../../components/ErrorSummary';
import { IdentifierField } from '../../components/IdentifierField';
import { ChoiceCard, RadioGroup } from '../../components/RadioGroup';
import { SegmentedControl } from '../../components/SegmentedControl';
import { Select } from '../../components/Select';
import { TextField } from '../../components/TextField';
import { FormGrid, FormGridItem, FormSection } from './FormLayout';

const meta = {
  title: 'Patterns/FormLayout',
  component: FormSection,
  parameters: { layout: 'padded' },
} satisfies Meta<typeof FormSection>;

export default meta;
type Story = StoryObj<typeof meta>;

interface QuoteForm {
  afm: string;
  name: string;
  mobile: string;
  email: string;
  plate: string;
  vin: string;
  usage: string | null;
  fuel: string;
  cc: string;
  extras: string[];
  offering: string | null;
}

const initial: QuoteForm = {
  afm: '',
  name: '',
  mobile: '',
  email: '',
  plate: '',
  vin: '',
  usage: null,
  fuel: 'petrol',
  cc: '',
  extras: ['road'],
  offering: null,
};

/** Required-field checks for the story (format checks run inside the identifier fields). */
function validate(form: QuoteForm): Record<string, string> {
  const errors: Record<string, string> = {};
  if (form.afm === '') errors.afm = 'Συμπληρώστε τον ΑΦΜ του ασφαλισμένου.';
  if (form.name.trim() === '') errors.name = 'Συμπληρώστε το ονοματεπώνυμο του ασφαλισμένου.';
  if (form.plate === '') errors.plate = 'Συμπληρώστε τον αριθμό κυκλοφορίας του οχήματος.';
  if (form.usage === null) errors.usage = 'Επιλέξτε τη χρήση του οχήματος.';
  if (form.offering === null) errors.offering = 'Επιλέξτε πακέτο κάλυψης.';
  return errors;
}

const fieldOrder = ['afm', 'name', 'plate', 'usage', 'offering'] as const;

function MotorQuoteForm() {
  const [form, setForm] = useState<QuoteForm>(initial);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [attempt, setAttempt] = useState(0);
  const set = <K extends keyof QuoteForm>(key: K) => {
    return (value: QuoteForm[K]) => {
      setForm((previous) => ({ ...previous, [key]: value }));
      // Validation timing (Part 2 §4.2): once an error shows, re-check on change.
      setErrors((previous) => {
        if (!(key in previous)) return previous;
        return Object.fromEntries(Object.entries(previous).filter(([name]) => name !== key));
      });
    };
  };

  const onSubmit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    setErrors(validate(form));
    setAttempt((n) => n + 1);
  };

  const summary: ErrorSummaryItem[] = fieldOrder.flatMap((key) => {
    const message = errors[key];
    return message ? [{ fieldId: `quote-${key}`, message }] : [];
  });

  return (
    <form
      noValidate
      onSubmit={onSubmit}
      style={{ display: 'grid', gap: 'var(--space-stack-section)', maxInlineSize: '1240px' }}
    >
      <ErrorSummary errors={summary} focusKey={attempt} />

      <FormSection
        title="Ασφαλισμένος"
        description="Τα στοιχεία συμπληρώνονται και από το gov.gr Wallet."
      >
        <FormGrid>
          <FormGridItem width="sm">
            <IdentifierField
              id="quote-afm"
              kind="afm"
              label="ΑΦΜ"
              isRequired
              value={form.afm}
              onChange={set('afm')}
              errorMessage={errors.afm}
            />
          </FormGridItem>
          <FormGridItem width="md">
            <TextField
              id="quote-name"
              label="Ονοματεπώνυμο"
              autoComplete="name"
              isRequired
              value={form.name}
              onChange={set('name')}
              errorMessage={errors.name}
            />
          </FormGridItem>
          <FormGridItem width="md">
            <TextField
              label="Κινητό τηλέφωνο"
              type="tel"
              value={form.mobile}
              onChange={set('mobile')}
            />
          </FormGridItem>
          <FormGridItem width="lg">
            <TextField
              label="Email"
              type="email"
              autoComplete="email"
              helperText="Θα στείλουμε εδώ την προσφορά και το IPID."
              value={form.email}
              onChange={set('email')}
            />
          </FormGridItem>
        </FormGrid>
      </FormSection>

      <FormSection title="Όχημα">
        <FormGrid>
          <FormGridItem width="sm">
            <IdentifierField
              id="quote-plate"
              kind="plate"
              label="Αριθμός κυκλοφορίας"
              isRequired
              value={form.plate}
              onChange={set('plate')}
              errorMessage={errors.plate}
            />
          </FormGridItem>
          <FormGridItem width="lg">
            <IdentifierField
              kind="vin"
              label="Αριθμός πλαισίου (VIN)"
              help="Τον βρίσκετε στην άδεια κυκλοφορίας, πεδίο E."
              value={form.vin}
              onChange={set('vin')}
            />
          </FormGridItem>
          <FormGridItem width="md" newRow>
            <Select
              id="quote-usage"
              label="Χρήση οχήματος"
              isRequired
              options={[
                { id: 'private', label: 'Ιδιωτική χρήση' },
                { id: 'professional', label: 'Επαγγελματική χρήση' },
                { id: 'rental', label: 'Ενοικίαση' },
              ]}
              value={form.usage}
              onChange={set('usage')}
              errorMessage={errors.usage}
            />
          </FormGridItem>
          <FormGridItem width="md">
            <SegmentedControl
              label="Καύσιμο"
              options={[
                { id: 'petrol', label: 'Βενζίνη' },
                { id: 'diesel', label: 'Πετρέλαιο' },
                { id: 'electric', label: 'Ηλεκτρικό' },
              ]}
              value={form.fuel}
              onChange={set('fuel')}
            />
          </FormGridItem>
          <FormGridItem width="xs">
            <TextField
              label="Κυβικά"
              suffix="cc"
              inputMode="numeric"
              value={form.cc}
              onChange={set('cc')}
            />
          </FormGridItem>
          <FormGridItem width="full">
            <CheckboxGroup
              label="Πρόσθετες καλύψεις"
              orientation="horizontal"
              value={form.extras}
              onChange={set('extras')}
            >
              <Checkbox value="road">Οδική βοήθεια</Checkbox>
              <Checkbox value="glass">Θραύση κρυστάλλων</Checkbox>
              <Checkbox value="legal">Νομική προστασία</Checkbox>
            </CheckboxGroup>
          </FormGridItem>
        </FormGrid>
      </FormSection>

      <FormSection title="Πακέτο κάλυψης">
        <RadioGroup
          id="quote-offering"
          label="Επιλέξτε πακέτο"
          variant="cards"
          isRequired
          value={form.offering}
          onChange={set('offering')}
          errorMessage={errors.offering}
        >
          <ChoiceCard
            value="basic"
            title="Βασικό"
            price="286,40 €"
            priceCaption="ετησίως, με φόρους"
            bullets={['Αστική ευθύνη', 'Νομική προστασία', 'Οδική βοήθεια 24/7']}
          />
          <ChoiceCard
            value="comfort"
            title="Άνετο"
            price="412,38 €"
            priceCaption="ετησίως, με φόρους"
            tag="Συνιστάται"
            bullets={['Όλα του Βασικού', 'Θραύση κρυστάλλων', 'Πυρκαγιά', 'Κλοπή']}
          />
          <ChoiceCard
            value="full"
            title="Πλήρες"
            price="689,10 €"
            priceCaption="ετησίως, με φόρους"
            bullets={['Όλα του Άνετου', 'Ίδιες ζημίες', 'Φυσικά φαινόμενα']}
          />
        </RadioGroup>
      </FormSection>

      <div style={{ display: 'flex', gap: 'var(--space-2)', justifyContent: 'flex-end' }}>
        <Button variant="secondary">Αποθήκευση πρόχειρου</Button>
        <Button variant="primary" type="submit" shortcut="Mod+Enter">
          Επόμενο: Καλύψεις
        </Button>
      </div>
    </form>
  );
}

export const MotorQuote: Story = {
  args: { title: 'Ασφαλισμένος', children: null },
  render: () => <MotorQuoteForm />,
};
