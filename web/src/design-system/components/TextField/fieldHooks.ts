import { useCallback, useEffect, useId, useRef, useState, type Ref, type RefObject } from 'react';
import { useTranslation } from 'react-i18next';

import { announce } from '../../a11y/announce';

/** Ids of the parts that can describe a field control, in `aria-describedby` order (error first). */
export interface FieldIds {
  error: string;
  warning: string;
  helper: string;
  transient: string;
  count: string;
  reason: string;
  prefix: string;
  suffix: string;
  masked: string;
}

export function useFieldIds(): FieldIds {
  const base = useId();
  return {
    error: `${base}-error`,
    warning: `${base}-warning`,
    helper: `${base}-helper`,
    transient: `${base}-transient`,
    count: `${base}-count`,
    reason: `${base}-reason`,
    prefix: `${base}-prefix`,
    suffix: `${base}-suffix`,
    masked: `${base}-masked`,
  };
}

/** Joins ids for `aria-describedby`, skipping empty entries; `undefined` when nothing is left. */
export function joinIds(...ids: (string | false | null | undefined)[]): string | undefined {
  const joined = ids.filter(Boolean).join(' ');
  return joined === '' ? undefined : joined;
}

export function hasText(value: string | undefined | null): value is string {
  return value !== undefined && value !== null && value !== '';
}

/** Ids of the visible messages, error first (DESIGN-B E.2: «error id first in aria-describedby»). */
export function messageIds(
  ids: FieldIds,
  messages: {
    errorMessage?: string | undefined;
    warningMessage?: string | undefined;
    helperText?: string | undefined;
    transientMessage?: string | undefined;
    disabledReason?: string | undefined;
  },
): (string | undefined)[] {
  const error = hasText(messages.errorMessage);
  return [
    error ? ids.error : undefined,
    !error && hasText(messages.warningMessage) ? ids.warning : undefined,
    !error && hasText(messages.helperText) ? ids.helper : undefined,
    hasText(messages.transientMessage) ? ids.transient : undefined,
    hasText(messages.disabledReason) ? ids.reason : undefined,
  ];
}

/**
 * Character count announcement (Part 2 §4.2): polite, only when fewer than 20 characters remain, and at most
 * once per second (the latest value wins).
 */
export function useCharacterCountAnnouncement(length: number, max: number | undefined): string {
  const { t } = useTranslation('ds');
  const [message, setMessage] = useState('');
  const lastAt = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const remaining = max === undefined ? Infinity : max - length;

  useEffect(() => {
    if (remaining >= 20 || max === undefined) return;
    const text = t('formField.charactersRemaining', { count: Math.max(remaining, 0) });
    const wait = Math.max(0, lastAt.current + 1000 - Date.now());
    clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      lastAt.current = Date.now();
      setMessage(text);
    }, wait);
  }, [remaining, max, t]);

  useEffect(
    () => () => {
      clearTimeout(timer.current);
    },
    [],
  );
  return remaining < 20 ? message : '';
}

/** True after `delay` ms while `active` stays true (spinners after 400 ms, Part 2 §4.24). */
export function useDelayedFlag(active: boolean, delay: number): boolean {
  const [shown, setShown] = useState(false);
  useEffect(() => {
    if (!active) return;
    const id = setTimeout(() => {
      setShown(true);
    }, delay);
    return () => {
      clearTimeout(id);
      setShown(false);
    };
  }, [active, delay]);
  return active && shown;
}

/**
 * MI-10 copy confirm: copies `text`, flips `copied` for 1,500 ms and announces «Αντιγράφηκε».
 */
export function useCopy(): { copied: boolean; copy: (text: string) => void } {
  const { t } = useTranslation('ds');
  const [copied, setCopied] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(
    () => () => {
      clearTimeout(timer.current);
    },
    [],
  );

  const copy = useCallback(
    (text: string) => {
      const done = () => {
        setCopied(true);
        announce(t('textField.copied'));
        clearTimeout(timer.current);
        timer.current = setTimeout(() => {
          setCopied(false);
        }, 1500);
      };
      const clipboard = typeof navigator === 'undefined' ? undefined : navigator.clipboard;
      if (clipboard && typeof clipboard.writeText === 'function') {
        clipboard.writeText(text).then(done, () => undefined);
      } else {
        done();
      }
    },
    [t],
  );
  return { copied, copy };
}

/**
 * Pointer hover and focus-within state for an element, for reason tooltips on non-button controls. Esc
 * dismisses the tooltip until the pointer leaves or focus moves (WCAG 1.4.13).
 */
export function useHoverFocus(): {
  active: boolean;
  handlers: {
    onPointerEnter: () => void;
    onPointerLeave: () => void;
    onFocus: () => void;
    onBlur: () => void;
    onKeyDown: (event: { key: string }) => void;
  };
} {
  const [hovered, setHovered] = useState(false);
  const [focused, setFocused] = useState(false);
  const [dismissed, setDismissed] = useState(false);
  return {
    active: (hovered || focused) && !dismissed,
    handlers: {
      onPointerEnter: () => {
        setHovered(true);
      },
      onPointerLeave: () => {
        setHovered(false);
        setDismissed(false);
      },
      onFocus: () => {
        setFocused(true);
      },
      onBlur: () => {
        setFocused(false);
        setDismissed(false);
      },
      onKeyDown: (event) => {
        if (event.key === 'Escape') setDismissed(true);
      },
    },
  };
}

/**
 * Sets or removes an ARIA attribute on an element that a React Aria primitive renders but does not let us
 * pass the attribute to (for example `aria-busy` on a Select trigger or `aria-disabled` on the hidden input
 * of a switch with a disabled reason).
 */
export function useAriaAttribute(
  ref: RefObject<Element | null>,
  name: `aria-${string}`,
  value: string | undefined,
): void {
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (value === undefined) el.removeAttribute(name);
    else el.setAttribute(name, value);
  });
}

type AnyRef<T> = Ref<T> | undefined;

function assignRef<T>(ref: AnyRef<T>, value: T | null): void {
  if (typeof ref === 'function') ref(value);
  else if (ref) (ref as { current: T | null }).current = value;
}

/** A stable callback ref that sets both refs (our own and the consumer's, for example RHF's). */
export function useMergedRef<T>(a: AnyRef<T>, b: AnyRef<T>): (value: T | null) => void {
  return useCallback(
    (value: T | null) => {
      assignRef(a, value);
      assignRef(b, value);
    },
    [a, b],
  );
}
