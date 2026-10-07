import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Avatar, FieldLock, PresenceStack, type PresencePerson } from './Avatar';
import { avatarHue, initialsOf } from './avatarColor';

const people: PresencePerson[] = [
  { id: '1', name: 'Μαρία Παπαδοπούλου', presence: 'editing', section: 'Καλύψεις', since: '14:02' },
  { id: '2', name: 'Κώστας Νικολάου', presence: 'viewing', since: '13:48' },
  { id: '3', name: 'Ελένη Γεωργίου', presence: 'viewing', since: '13:55' },
  { id: '4', name: 'Νίκος Δημητρίου', presence: 'idle', since: '12:10' },
  { id: '5', name: 'Άννα Σταυροπούλου', presence: 'viewing', since: '14:05' },
  { id: '6', name: 'Παύλος Ιωάννου', presence: 'viewing', since: '14:06' },
  { id: '7', name: 'Σοφία Κωνσταντίνου', presence: 'idle', since: '11:30' },
];

describe('avatar helpers', () => {
  it('takes two initials without tonos', () => {
    expect(initialsOf('Μαρία Παπαδοπούλου')).toBe('ΜΠ');
    expect(initialsOf('Άννα Σταυροπούλου')).toBe('ΑΣ');
    expect(initialsOf('Μ. Παπαδοπούλου')).toBe('ΜΠ');
    expect(initialsOf('Ελένη')).toBe('Ε');
  });

  it('picks a stable hue between 1 and 8', () => {
    const hue = avatarHue('Μαρία Παπαδοπούλου');
    expect(hue).toBeGreaterThanOrEqual(1);
    expect(hue).toBeLessThanOrEqual(8);
    expect(avatarHue('Μαρία Παπαδοπούλου')).toBe(hue);
  });
});

describe('Avatar', () => {
  it('names the initials avatar with the person and presence', () => {
    renderWithDs(<Avatar name="Μαρία Παπαδοπούλου" presence="editing" />);
    const avatar = screen.getByRole('img', { name: 'Μαρία Παπαδοπούλου, επεξεργάζεται' });
    expect(avatar).toHaveTextContent('ΜΠ');
    expect(avatar.parentElement).toHaveAttribute('data-presence', 'editing');
  });

  it('uses alt text for photos and falls back to initials on error', () => {
    renderWithDs(<Avatar name="Κώστας Νικολάου" src="/missing.png" />);
    const img = screen.getByRole('img', { name: 'Κώστας Νικολάου' });
    expect(img.tagName).toBe('IMG');
    fireEvent.error(img);
    expect(screen.getByRole('img', { name: 'Κώστας Νικολάου' })).toHaveTextContent('ΚΝ');
  });

  it('is hidden when decorative', () => {
    renderWithDs(<Avatar name="Κώστας Νικολάου" decorative />);
    expect(screen.queryByRole('img')).not.toBeInTheDocument();
  });

  it('squares organisations', () => {
    renderWithDs(<Avatar name="Ασφαλιστική Αιγαίου" kind="organisation" size="lg" />);
    expect(screen.getByRole('img').parentElement).toHaveAttribute('data-kind', 'organisation');
  });
});

describe('PresenceStack', () => {
  it('shows four avatars and a «+3» button naming the hidden count', async () => {
    const { user } = renderWithDs(<PresenceStack people={people} />);
    const group = screen.getByRole('group', { name: 'Άτομα σε αυτή την εγγραφή' });
    expect(within(group).getAllByRole('img')).toHaveLength(4);
    const more = screen.getByRole('button', {
      name: '3 ακόμη άτομα βλέπουν αυτή την εγγραφή',
    });
    expect(more).toHaveTextContent('+3');
    expect(more).toHaveAttribute('aria-expanded', 'false');

    await user.click(more);
    const dialog = await screen.findByRole('dialog', { name: 'Σε αυτή την εγγραφή' });
    expect(dialog).toHaveTextContent('Μαρία Παπαδοπούλου · επεξεργάζεται: Καλύψεις · από 14:02');
    expect(dialog).toHaveTextContent('Κώστας Νικολάου · βλέπει · από 13:48');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(more).toHaveFocus();
    });
  });

  it('uses the singular for one hidden person', () => {
    renderWithDs(<PresenceStack people={people.slice(0, 5)} />);
    expect(
      screen.getByRole('button', { name: '1 ακόμη άτομο βλέπει αυτή την εγγραφή' }),
    ).toBeInTheDocument();
  });

  it('translates the stack button', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(<PresenceStack people={people} />);
    expect(
      screen.getByRole('button', { name: '3 more people are viewing this record' }),
    ).toBeInTheDocument();
  });

  it('has no axe violations', async () => {
    const { container } = renderWithDs(
      <div>
        <PresenceStack people={people} />
        <Avatar name="Νίκος Δημητρίου" status="success" statusLabel="Διαθέσιμος" />
        <FieldLock name="Μ. Παπαδοπούλου" gender="female" />
      </div>,
    );
    await expectNoA11yViolations(container);
  });
});

describe('FieldLock', () => {
  it('announces who is editing with the right article', () => {
    renderWithDs(<FieldLock name="Μ. Παπαδοπούλου" gender="female" />);
    expect(screen.getByRole('status')).toHaveTextContent('Επεξεργάζεται η Μ. Παπαδοπούλου');
  });
});
