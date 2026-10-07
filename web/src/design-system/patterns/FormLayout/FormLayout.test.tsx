import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { IdentifierField } from '../../components/IdentifierField';
import { Select } from '../../components/Select';
import { TextField } from '../../components/TextField';
import { FormGrid, FormGridItem, FormSection } from './FormLayout';

function VehicleSection() {
  return (
    <FormSection title="Όχημα" description="Στοιχεία από την άδεια κυκλοφορίας">
      <FormGrid>
        <FormGridItem width="sm">
          <IdentifierField kind="plate" label="Αριθμός κυκλοφορίας" isRequired />
        </FormGridItem>
        <FormGridItem width="lg">
          <IdentifierField kind="vin" label="Αριθμός πλαισίου (VIN)" />
        </FormGridItem>
        <FormGridItem width="md" newRow>
          <Select
            label="Χρήση"
            options={[
              { id: 'private', label: 'Ιδιωτική' },
              { id: 'professional', label: 'Επαγγελματική' },
            ]}
          />
        </FormGridItem>
        <FormGridItem width="xs">
          <TextField label="Κυβικά" suffix="cc" />
        </FormGridItem>
      </FormGrid>
    </FormSection>
  );
}

describe('FormLayout', () => {
  it('labels each section by its heading', () => {
    renderWithDs(<VehicleSection />);
    const section = screen.getByRole('region', { name: 'Όχημα' });
    expect(section.tagName).toBe('SECTION');
    expect(screen.getByRole('heading', { level: 3, name: 'Όχημα' })).toBeInTheDocument();
    expect(screen.getByText('Στοιχεία από την άδεια κυκλοφορίας')).toBeInTheDocument();
  });

  it('gives every cell a semantic width and keeps labels above fields', () => {
    renderWithDs(<VehicleSection />);
    const plate = screen.getByRole('textbox', { name: /Αριθμός κυκλοφορίας/ });
    const cell = plate.closest('[data-width]');
    expect(cell).toHaveAttribute('data-width', 'sm');
    const label = cell?.querySelector('label');
    expect(label?.compareDocumentPosition(plate)).toBe(Node.DOCUMENT_POSITION_FOLLOWING);
    expect(screen.getByRole('button', { name: /Χρήση/ }).closest('[data-width]')).toHaveAttribute(
      'data-new-row',
      'true',
    );
  });

  it('supports other heading levels and actions', () => {
    renderWithDs(
      <FormSection
        title="Οδηγοί"
        headingLevel={2}
        actions={<button type="button">Προσθήκη</button>}
      >
        <FormGrid>
          <FormGridItem width="full">
            <TextField label="Ονοματεπώνυμο" />
          </FormGridItem>
        </FormGrid>
      </FormSection>,
    );
    expect(screen.getByRole('heading', { level: 2, name: 'Οδηγοί' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Προσθήκη' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(<VehicleSection />);
    await expectNoA11yViolations(container);
  });
});
