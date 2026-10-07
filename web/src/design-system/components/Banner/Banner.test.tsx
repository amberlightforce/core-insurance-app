import { screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import { Banner } from './Banner';

describe('Banner', () => {
  it('uses role status for info and alert for danger, labelled by the title', () => {
    renderWithDs(
      <div>
        <Banner variant="info" title="Η ανανέωση ξεκινά σε 30 ημέρες" />
        <Banner variant="danger" title="Διορθώστε 3 πεδία για να συνεχίσετε" />
      </div>,
    );
    expect(screen.getByRole('status', { name: 'Η ανανέωση ξεκινά σε 30 ημέρες' })).toHaveAttribute(
      'data-variant',
      'info',
    );
    expect(
      screen.getByRole('alert', { name: 'Διορθώστε 3 πεδία για να συνεχίσετε' }),
    ).toBeVisible();
  });

  it('can opt out of the live region', () => {
    renderWithDs(<Banner variant="warning" title="Υπάρχουν νεότερα δεδομένα" live="none" />);
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });

  it('dismisses with the × button after the exit fade', async () => {
    const onDismiss = vi.fn();
    const { user } = renderWithDs(
      <Banner variant="info" title="Νέα λειτουργία" onDismiss={onDismiss} />,
    );
    await user.click(screen.getByRole('button', { name: 'Απόκρυψη' }));
    expect(screen.getByRole('status')).toHaveAttribute('data-closing', 'true');
    await waitFor(() => {
      expect(onDismiss).toHaveBeenCalledTimes(1);
    });
  });

  it('never offers dismissal for danger banners', () => {
    renderWithDs(<Banner variant="danger" title="Σύγκρουση αλλαγών" onDismiss={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'Απόκρυψη' })).not.toBeInTheDocument();
  });

  it('receives focus when used as an error summary', () => {
    renderWithDs(
      <Banner
        variant="danger"
        title="Διορθώστε 2 πεδία για να συνεχίσετε"
        autoFocus
        actions={<Button variant="link">Ημερομηνία ζημίας</Button>}
      />,
    );
    expect(screen.getByRole('alert')).toHaveFocus();
  });

  it('translates the dismiss label', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<Banner title="New feature" onDismiss={() => undefined} />);
    expect(screen.getByRole('button', { name: 'Dismiss' })).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <Banner variant="success" title="Αποθηκεύτηκε" onDismiss={() => undefined} />
        <Banner
          variant="warning"
          layout="page"
          title="Υπάρχουν νεότερα δεδομένα"
          actions={<Button variant="link">Ανανέωση</Button>}
        />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});
