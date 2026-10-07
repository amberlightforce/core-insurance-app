import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { Copy, Pencil, Trash2 } from 'lucide-react';
import { describe, expect, it, vi } from 'vitest';

import i18n from '../../../i18n';
import { expectNoA11yViolations } from '../../../test/axe';
import { renderWithDs } from '../../../test/render';
import { Button } from '../Button';
import {
  ContextMenu,
  Menu,
  MenuItem,
  MenuSection,
  OverflowMenu,
  SplitButton,
  Submenu,
} from './Menu';

function RowMenu({ onAction }: { onAction?: (key: React.Key) => void }) {
  return (
    <Menu trigger={<Button>Ενέργειες</Button>} {...(onAction ? { onAction } : {})}>
      <MenuSection aria-label="Επεξεργασία">
        <MenuItem id="edit" icon={Pencil} shortcut="Mod+E">
          Επεξεργασία
        </MenuItem>
        <MenuItem id="copy" icon={Copy} description="Νέο πρόχειρο με τα ίδια στοιχεία">
          Αντιγραφή
        </MenuItem>
        <MenuItem id="assign" disabledReason="Η εργασία είναι κλειδωμένη από Μ. Παπαδοπούλου">
          Ανάθεση
        </MenuItem>
        <Submenu label="Μετακίνηση σε">
          <MenuItem id="queue-a">Ουρά Α</MenuItem>
          <MenuItem id="queue-b">Ουρά Β</MenuItem>
        </Submenu>
      </MenuSection>
      <MenuSection aria-label="Διαγραφή">
        <MenuItem id="delete" icon={Trash2} isDestructive>
          Διαγραφή πρόχειρου
        </MenuItem>
      </MenuSection>
    </Menu>
  );
}

describe('Menu', () => {
  it('opens from the trigger with arrow-key navigation, typeahead and Enter', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<RowMenu onAction={onAction} />);
    const trigger = screen.getByRole('button', { name: 'Ενέργειες' });
    expect(trigger).toHaveAttribute('aria-haspopup');
    await user.tab();
    await user.keyboard('{Enter}');
    const menu = await screen.findByRole('menu');
    expect(trigger).toHaveAttribute('aria-expanded', 'true');
    const items = within(menu).getAllByRole('menuitem');
    expect(items[0]).toHaveFocus();
    await user.keyboard('{ArrowDown}');
    expect(items[1]).toHaveFocus();
    await user.keyboard('{End}');
    expect(screen.getByRole('menuitem', { name: 'Διαγραφή πρόχειρου' })).toHaveFocus();
    await user.keyboard('{Home}');
    expect(items[0]).toHaveFocus();
    await user.keyboard('α');
    expect(screen.getByRole('menuitem', { name: /Αντιγραφή/ })).toHaveFocus();
    await user.keyboard('{Enter}');
    expect(onAction.mock.calls[0]?.[0]).toBe('copy');
    await waitFor(() => {
      expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });
  });

  it('describes items, shows shortcuts and marks destructive items', async () => {
    const { user } = renderWithDs(<RowMenu />);
    await user.click(screen.getByRole('button', { name: 'Ενέργειες' }));
    await screen.findByRole('menu');
    expect(screen.getByRole('menuitem', { name: /Αντιγραφή/ })).toHaveAccessibleDescription(
      'Νέο πρόχειρο με τα ίδια στοιχεία',
    );
    expect(screen.getByRole('menuitem', { name: /Επεξεργασία/ })).toHaveTextContent('Ctrl');
    expect(screen.getByRole('menuitem', { name: 'Διαγραφή πρόχειρου' })).toHaveAttribute(
      'data-destructive',
      'true',
    );
  });

  it('keeps a disabled-with-reason item focusable, explains it and ignores it', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<RowMenu onAction={onAction} />);
    await user.click(screen.getByRole('button', { name: 'Ενέργειες' }));
    await screen.findByRole('menu');
    const assign = screen.getByRole('menuitem', { name: 'Ανάθεση' });
    expect(assign).toHaveAttribute('aria-disabled', 'true');
    expect(assign).toHaveAccessibleDescription(
      'Μη διαθέσιμο: Η εργασία είναι κλειδωμένη από Μ. Παπαδοπούλου',
    );
    await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}');
    expect(assign).toHaveFocus();
    // The visual tooltip sits outside the modal popover, so React Aria hides it from AT (the description carries it).
    expect(await screen.findByRole('tooltip', { hidden: true })).toHaveTextContent('κλειδωμένη');
    await user.keyboard('{Enter}');
    expect(onAction).not.toHaveBeenCalled();
    expect(screen.getByRole('menu')).toBeInTheDocument();
  });

  it('opens a submenu with → and closes it with ←', async () => {
    const { user } = renderWithDs(<RowMenu />);
    await user.click(screen.getByRole('button', { name: 'Ενέργειες' }));
    await screen.findByRole('menu');
    const submenuTrigger = screen.getByRole('menuitem', { name: 'Μετακίνηση σε' });
    expect(submenuTrigger).toHaveAttribute('aria-haspopup');
    await user.keyboard('{ArrowDown}{ArrowDown}{ArrowDown}{ArrowDown}');
    expect(submenuTrigger).toHaveFocus();
    await user.keyboard('{ArrowRight}');
    await waitFor(() => {
      expect(screen.getAllByRole('menu')).toHaveLength(2);
    });
    expect(screen.getByRole('menuitem', { name: 'Ουρά Α' })).toHaveFocus();
    await user.keyboard('{ArrowLeft}');
    await waitFor(() => {
      expect(screen.getAllByRole('menu')).toHaveLength(1);
    });
    expect(submenuTrigger).toHaveFocus();
  });

  it('closes with Esc and returns focus to the trigger', async () => {
    const { user } = renderWithDs(<RowMenu />);
    const trigger = screen.getByRole('button', { name: 'Ενέργειες' });
    await user.click(trigger);
    await screen.findByRole('menu');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('shows checks for selected items', async () => {
    const { user } = renderWithDs(
      <Menu trigger={<Button>Πυκνότητα</Button>} selectionMode="single" selectedKeys={['compact']}>
        <MenuItem id="compact">Συμπαγής</MenuItem>
        <MenuItem id="comfortable">Άνετη</MenuItem>
      </Menu>,
    );
    await user.click(screen.getByRole('button', { name: 'Πυκνότητα' }));
    expect(await screen.findByRole('menuitemradio', { name: 'Συμπαγής' })).toHaveAttribute(
      'aria-checked',
      'true',
    );
  });

  it('has no axe violations when open', async () => {
    const { user } = renderWithDs(<RowMenu />);
    await user.click(screen.getByRole('button', { name: 'Ενέργειες' }));
    const menu = await screen.findByRole('menu');
    await expectNoA11yViolations(menu.closest('[data-material]') ?? menu);
  });
});

describe('OverflowMenu', () => {
  it('is an icon button named «Περισσότερες ενέργειες»', async () => {
    const { user } = renderWithDs(
      <OverflowMenu>
        <MenuItem id="open">Άνοιγμα</MenuItem>
      </OverflowMenu>,
    );
    await user.click(screen.getByRole('button', { name: 'Περισσότερες ενέργειες' }));
    expect(await screen.findByRole('menuitem', { name: 'Άνοιγμα' })).toBeInTheDocument();
  });

  it('is named in English', async () => {
    await i18n.changeLanguage('en');
    renderWithDs(
      <OverflowMenu>
        <MenuItem id="open">Open</MenuItem>
      </OverflowMenu>,
    );
    expect(screen.getByRole('button', { name: 'More actions' })).toBeInTheDocument();
  });
});

describe('SplitButton', () => {
  it('has two focus stops and opens its menu with Alt+↓', async () => {
    const onPress = vi.fn();
    const onAction = vi.fn();
    const { user } = renderWithDs(
      <SplitButton label="Αποθήκευση" onPress={onPress} onAction={onAction}>
        <MenuItem id="draft">Αποθήκευση πρόχειρου</MenuItem>
        <MenuItem id="close">Αποθήκευση και κλείσιμο</MenuItem>
      </SplitButton>,
    );
    expect(screen.getByRole('group', { name: 'Αποθήκευση' })).toBeInTheDocument();
    await user.tab();
    const main = screen.getByRole('button', { name: 'Αποθήκευση' });
    expect(main).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Περισσότερες επιλογές' })).toHaveFocus();
    await user.tab({ shift: true });
    await user.keyboard('{Enter}');
    expect(onPress).toHaveBeenCalledTimes(1);

    await user.keyboard('{Alt>}{ArrowDown}{/Alt}');
    await screen.findByRole('menu');
    await waitFor(() => {
      expect(screen.getByRole('menuitem', { name: 'Αποθήκευση πρόχειρου' })).toHaveFocus();
    });
    await user.keyboard('{Enter}');
    expect(onAction.mock.calls[0]?.[0]).toBe('draft');
  });
});

describe('ContextMenu', () => {
  function Row({ onAction }: { onAction: (key: React.Key) => void }) {
    return (
      <ContextMenu
        aria-label="Ενέργειες γραμμής"
        onAction={onAction}
        items={
          <>
            <MenuItem id="open">Άνοιγμα</MenuItem>
            <MenuItem id="assign">Ανάθεση</MenuItem>
          </>
        }
      >
        <button type="button">ΑΣΦ-2026-004471</button>
      </ContextMenu>
    );
  }

  it('opens on right-click', async () => {
    const onAction = vi.fn();
    const { user } = renderWithDs(<Row onAction={onAction} />);
    fireEvent.contextMenu(screen.getByRole('button', { name: 'ΑΣΦ-2026-004471' }), {
      clientX: 40,
      clientY: 60,
    });
    const menu = await screen.findByRole('menu', { name: 'Ενέργειες γραμμής' });
    await waitFor(() => {
      expect(within(menu).getAllByRole('menuitem')[0]).toHaveFocus();
    });
    await user.keyboard('{ArrowDown}{Enter}');
    expect(onAction.mock.calls[0]?.[0]).toBe('assign');
  });

  it('opens with Shift+F10 on the focused target and returns focus on Esc', async () => {
    const { user } = renderWithDs(<Row onAction={vi.fn()} />);
    const target = screen.getByRole('button', { name: 'ΑΣΦ-2026-004471' });
    await user.tab();
    expect(target).toHaveFocus();
    await user.keyboard('{Shift>}{F10}{/Shift}');
    await screen.findByRole('menu');
    await user.keyboard('{Escape}');
    await waitFor(() => {
      expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });
    await waitFor(() => {
      expect(target).toHaveFocus();
    });
  });

  it('opens with the ContextMenu key', async () => {
    const { user } = renderWithDs(<Row onAction={vi.fn()} />);
    await user.tab();
    await user.keyboard('{ContextMenu}');
    expect(await screen.findByRole('menu')).toBeInTheDocument();
  });
});
