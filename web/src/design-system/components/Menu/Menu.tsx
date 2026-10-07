import { Check, ChevronDown, ChevronRight, Ellipsis, type LucideIcon } from 'lucide-react';
import {
  useLayoutEffect,
  useRef,
  useState,
  type ReactElement,
  type ReactNode,
  type Ref,
  type RefObject,
} from 'react';
import { Overlay, useOverlayPosition } from 'react-aria';
import {
  Header,
  Menu as AriaMenu,
  MenuItem as AriaMenuItem,
  MenuSection as AriaMenuSection,
  MenuTrigger,
  Popover as AriaPopover,
  Separator,
  SubmenuTrigger,
  Text,
  type Key,
  type MenuProps as AriaMenuProps,
  type PopoverProps as AriaPopoverProps,
  type Selection,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';

import { Icon } from '../../icons';
import { cx, defined } from '../../utils/cx';
import { Button, type ButtonVariant } from '../Button';
import { useModalDepth } from '../Dialog/modalContext';
import { Kbd, matchesShortcut } from '../Kbd';
import styles from './Menu.module.css';

interface MenuListOptions {
  /** Accessible name of the menu (defaults to the trigger's label through React Aria). */
  'aria-label'?: string;
  onAction?: (key: Key) => void;
  /** `single`/`multiple` show a check in the leading slot of selected items. */
  selectionMode?: AriaMenuProps<object>['selectionMode'];
  selectedKeys?: Iterable<Key>;
  onSelectionChange?: (keys: Selection) => void;
}

/** Menu-level `onAction` must ignore disabled-with-reason items, which React Aria treats as enabled. */
function guard(onAction?: (key: Key) => void): ((key: Key) => void) | undefined {
  if (!onAction) return undefined;
  return (key) => {
    const selector = `[data-key="${CSS.escape(String(key))}"][data-soft-disabled]`;
    if (document.querySelector(selector)) return;
    onAction(key);
  };
}

/** The popover surface for menus and submenus: `material.popover`, solid inside a modal. */
export function MenuPopover({
  children,
  placement,
  ...rest
}: {
  children: ReactNode;
  placement?: AriaPopoverProps['placement'];
  triggerRef?: AriaPopoverProps['triggerRef'];
  isOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
  'aria-label'?: string;
}) {
  const inModal = useModalDepth() > 0;
  return (
    <AriaPopover
      className={cx(styles.popover)}
      offset={4}
      data-material="popover"
      data-solid={inModal || undefined}
      {...defined({ placement, ...rest })}
    >
      {children}
    </AriaPopover>
  );
}

export interface MenuProps extends MenuListOptions {
  /** A pressable trigger (`Button`); receives `aria-haspopup="menu"` and `aria-expanded`. */
  trigger: ReactElement;
  /** `MenuItem`, `MenuSection`, `MenuSeparator`, `Submenu`. Put the destructive group last. */
  children: ReactNode;
  placement?: AriaPopoverProps['placement'];
  isOpen?: boolean;
  onOpenChange?: (isOpen: boolean) => void;
}

/**
 * Dropdown menu (Part 2 §4.17) on RAC `MenuTrigger` + `Menu`: ↑↓, Home/End, typeahead, → opens a submenu,
 * ← closes it, Enter, Esc. Items highlight with a pill (MI-19); popover opens with MI-28.
 */
export function Menu({
  trigger,
  children,
  placement = 'bottom start',
  isOpen,
  onOpenChange,
  'aria-label': ariaLabel,
  onAction,
  selectionMode,
  selectedKeys,
  onSelectionChange,
}: MenuProps) {
  return (
    <MenuTrigger {...defined({ isOpen, onOpenChange })}>
      {trigger}
      <MenuPopover placement={placement}>
        <AriaMenu
          className={cx(styles.menu)}
          {...defined({
            'aria-label': ariaLabel,
            onAction: guard(onAction),
            selectionMode,
            selectedKeys,
            onSelectionChange,
          })}
        >
          {children}
        </AriaMenu>
      </MenuPopover>
    </MenuTrigger>
  );
}

export interface MenuItemProps {
  id?: Key;
  /** Label. Strings are also used for typeahead. */
  children: ReactNode;
  textValue?: string;
  /** 16 px leading icon (replaced by the check when the menu has a selection mode). */
  icon?: LucideIcon;
  /** Second line in `text.secondary`. */
  description?: string;
  /** Shortcut chip, e.g. `Mod+D`. */
  shortcut?: string;
  /** Destructive items use `danger.fg` and belong in the last group. */
  isDestructive?: boolean;
  /**
   * Disabled with a reason: the item stays reachable with the arrow keys, is `aria-disabled`, shows the
   * reason in a tooltip and as its description, and ignores activation.
   */
  disabledReason?: string;
  /** Disabled without a reason (skipped by the arrow keys). */
  isDisabled?: boolean;
  onAction?: () => void;
  href?: string;
  ref?: Ref<HTMLDivElement>;
}

function ReasonTooltip({
  targetRef,
  children,
}: {
  targetRef: RefObject<Element | null>;
  children: ReactNode;
}) {
  const overlayRef = useRef<HTMLDivElement | null>(null);
  const { overlayProps } = useOverlayPosition({
    targetRef,
    overlayRef,
    placement: 'end',
    offset: 8,
    isOpen: true,
  });
  return (
    <Overlay disableFocusManagement>
      <div {...overlayProps} ref={overlayRef} role="tooltip" className={styles.tooltip}>
        {children}
      </div>
    </Overlay>
  );
}

/**
 * Disabled-with-reason marker rendered inside the item: React Aria sets `aria-disabled` only on items it
 * skips, so the item that must stay reachable gets it here, and the reason shows as a tooltip while the
 * item is highlighted.
 */
function SoftDisabled({ reason, showTooltip }: { reason: string; showTooltip: boolean }) {
  const markerRef = useRef<HTMLSpanElement | null>(null);
  const itemRef = useRef<Element | null>(null);
  useLayoutEffect(() => {
    const item = markerRef.current?.closest('[role^="menuitem"]') ?? null;
    itemRef.current = item;
    item?.setAttribute('aria-disabled', 'true');
    return () => {
      item?.removeAttribute('aria-disabled');
    };
  }, []);
  return (
    <span ref={markerRef} className={styles.marker} aria-hidden="true">
      {showTooltip ? <ReasonTooltip targetRef={itemRef}>{reason}</ReasonTooltip> : null}
    </span>
  );
}

/** Menu item: [16 px icon or check] label [description] [shortcut] [submenu chevron]. */
export function MenuItem({
  id,
  children,
  textValue,
  icon,
  description,
  shortcut,
  isDestructive = false,
  disabledReason,
  isDisabled,
  onAction,
  href,
  ref,
}: MenuItemProps) {
  const { t } = useTranslation('ds');
  const softDisabled = disabledReason !== undefined && disabledReason !== '';

  const text = textValue ?? (typeof children === 'string' ? children : undefined);

  return (
    <AriaMenuItem
      ref={ref}
      className={cx(styles.item)}
      data-destructive={isDestructive || undefined}
      data-soft-disabled={softDisabled || undefined}
      {...(softDisabled ? { shouldCloseOnSelect: false } : defined({ onAction, href }))}
      {...defined({ id, textValue: text, isDisabled })}
    >
      {({ isSelected, hasSubmenu, selectionMode, isFocused, isHovered }) => (
        <>
          <span className={styles.leading} aria-hidden="true">
            {selectionMode !== 'none' ? (
              isSelected ? (
                <Icon icon={Check} size={16} />
              ) : null
            ) : icon ? (
              <Icon icon={icon} size={16} />
            ) : null}
          </span>
          <span className={styles.text}>
            <Text slot="label" className={cx(styles.label)}>
              {children}
            </Text>
            {description ? (
              softDisabled ? (
                <span className={styles.description}>{description}</span>
              ) : (
                <Text slot="description" className={cx(styles.description)}>
                  {description}
                </Text>
              )
            ) : null}
            {softDisabled ? (
              <Text slot="description" className="ds-visually-hidden">
                {t('menu.disabledBecause', { reason: disabledReason })}
              </Text>
            ) : null}
          </span>
          {shortcut ? <Kbd shortcut={shortcut} /> : null}
          {hasSubmenu ? (
            <span className={styles.chevron} aria-hidden="true">
              <Icon icon={ChevronRight} size={16} />
            </span>
          ) : null}
          {softDisabled ? (
            <SoftDisabled reason={disabledReason} showTooltip={isFocused || isHovered} />
          ) : null}
        </>
      )}
    </AriaMenuItem>
  );
}

export interface MenuSectionProps {
  /** Optional group header. Without it pass `aria-label`. */
  title?: string;
  'aria-label'?: string;
  children: ReactNode;
}

/** A group of items, divided from the next by a 1 px `border.subtle` line. */
export function MenuSection({ title, 'aria-label': ariaLabel, children }: MenuSectionProps) {
  return (
    <AriaMenuSection className={cx(styles.section)} {...defined({ 'aria-label': ariaLabel })}>
      {title ? <Header className={cx(styles.header)}>{title}</Header> : null}
      {children}
    </AriaMenuSection>
  );
}

export function MenuSeparator() {
  return <Separator className={cx(styles.separator)} />;
}

export interface SubmenuProps {
  label: string;
  icon?: LucideIcon;
  children: ReactNode;
  onAction?: (key: Key) => void;
}

/** Submenu: → opens it (after 120 ms of hover intent with React Aria's safe triangle), ← closes it. */
export function Submenu({ label, icon, children, onAction }: SubmenuProps) {
  return (
    <SubmenuTrigger delay={120}>
      <MenuItem {...defined({ icon })}>{label}</MenuItem>
      <MenuPopover>
        <AriaMenu className={cx(styles.menu)} {...defined({ onAction: guard(onAction) })}>
          {children}
        </AriaMenu>
      </MenuPopover>
    </SubmenuTrigger>
  );
}

export interface OverflowMenuProps extends MenuListOptions {
  children: ReactNode;
  /** Defaults to «Περισσότερες ενέργειες». */
  label?: string;
  size?: 'sm' | 'md';
}

/** Row and toolbar overflow (⋯): an icon-only ghost button «Περισσότερες ενέργειες» with a menu. */
export function OverflowMenu({ children, label, size = 'md', ...menu }: OverflowMenuProps) {
  const { t } = useTranslation('ds');
  return (
    <Menu
      trigger={
        <Button variant="ghost" size={size} icon={Ellipsis} label={label ?? t('menu.overflow')} />
      }
      placement="bottom end"
      {...menu}
    >
      {children}
    </Menu>
  );
}

export interface SplitButtonProps {
  label: string;
  onPress: () => void;
  variant?: Extract<ButtonVariant, 'primary' | 'secondary'>;
  icon?: LucideIcon;
  /** Accessible name of the menu button (default «Περισσότερες επιλογές»). */
  menuLabel?: string;
  /** The alternative actions. */
  children: ReactNode;
  onAction?: (key: Key) => void;
  isLoading?: boolean;
  disabledReason?: string;
}

/**
 * Split button (Part 2 §4.1): two focus stops — the main action and a chevron menu button — joined by a
 * `--color-border-strong` divider (D-FE-06). Alt+↓ on the main action opens the menu.
 */
export function SplitButton({
  label,
  onPress,
  variant = 'secondary',
  icon,
  menuLabel,
  children,
  onAction,
  isLoading,
  disabledReason,
}: SplitButtonProps) {
  const { t } = useTranslation('ds');
  const [isOpen, setOpen] = useState(false);
  const [byKeyboard, setByKeyboard] = useState(false);
  return (
    <div className={styles.split} role="group" aria-label={label} data-variant={variant}>
      <Button
        variant={variant}
        onPress={onPress}
        {...defined({ icon, isLoading, disabledReason })}
        onKeyDown={(event) => {
          if (event.altKey && event.key === 'ArrowDown') {
            event.preventDefault();
            setByKeyboard(true);
            setOpen(true);
          } else {
            event.continuePropagation();
          }
        }}
      >
        {label}
      </Button>
      <MenuTrigger
        isOpen={isOpen}
        onOpenChange={(open) => {
          setOpen(open);
          if (!open) setByKeyboard(false);
        }}
      >
        <Button variant={variant} icon={ChevronDown} label={menuLabel ?? t('menu.splitMore')} />
        <MenuPopover placement="bottom end">
          <AriaMenu
            className={cx(styles.menu)}
            {...(byKeyboard ? { autoFocus: 'first' as const } : {})}
            {...defined({ onAction: guard(onAction) })}
          >
            {children}
          </AriaMenu>
        </MenuPopover>
      </MenuTrigger>
    </div>
  );
}

export interface ContextMenuProps {
  /** The target (a row, card or canvas). */
  children: ReactNode;
  /** The same items as the row's ⋯ menu. */
  items: ReactNode;
  'aria-label'?: string;
  onAction?: (key: Key) => void;
  className?: string;
}

/**
 * Context menu (Part 2 §4.17, D-FE-08): right-click opens the menu at the pointer; Shift+F10 or the
 * ContextMenu key on the focused target opens it at the element. Built on `onContextMenu` + a RAC
 * `Popover` with `triggerRef`; focus returns to the target on close.
 */
export function ContextMenu({
  children,
  items,
  'aria-label': ariaLabel,
  onAction,
  className,
}: ContextMenuProps) {
  const { t } = useTranslation('ds');
  const targetRef = useRef<HTMLDivElement | null>(null);
  const pointRef = useRef<HTMLSpanElement | null>(null);
  const [state, setState] = useState<{ open: boolean; x: number; y: number; atPointer: boolean }>({
    open: false,
    x: 0,
    y: 0,
    atPointer: false,
  });

  return (
    <div
      ref={targetRef}
      className={cx(styles.contextTarget, className)}
      onContextMenu={(event) => {
        event.preventDefault();
        setState({ open: true, x: event.clientX, y: event.clientY, atPointer: true });
      }}
      onKeyDown={(event) => {
        if (event.key === 'ContextMenu' || matchesShortcut(event, 'Shift+F10')) {
          event.preventDefault();
          setState({ open: true, x: 0, y: 0, atPointer: false });
        }
      }}
    >
      {children}
      <span
        ref={pointRef}
        className={styles.point}
        style={{ insetInlineStart: state.x, insetBlockStart: state.y }}
        aria-hidden="true"
      />
      <MenuPopover
        triggerRef={state.atPointer ? pointRef : targetRef}
        isOpen={state.open}
        onOpenChange={(open) => {
          setState((current) => ({ ...current, open }));
        }}
        placement={state.atPointer ? 'bottom start' : 'bottom end'}
        aria-label={ariaLabel ?? t('menu.contextLabel')}
      >
        <AriaMenu
          className={cx(styles.menu)}
          aria-label={ariaLabel ?? t('menu.contextLabel')}
          autoFocus="first"
          onClose={() => {
            setState((current) => ({ ...current, open: false }));
          }}
          {...defined({ onAction: guard(onAction) })}
        >
          {items}
        </AriaMenu>
      </MenuPopover>
    </div>
  );
}
