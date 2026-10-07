import { useCallback, useLayoutEffect, useMemo, useState, type ReactNode } from 'react';

import { PreferencesContext, type PreferencesContextValue } from './context';
import {
  applyPreferences,
  defaultPreferences,
  loadPreferences,
  resolveReducedMotion,
  resolveReducedTransparency,
  savePreferences,
  type Preferences,
} from './preferences';

export interface PreferencesProviderProps {
  children: ReactNode;
  /** Overrides for Storybook and tests; nothing is persisted when given. */
  initial?: Partial<Preferences>;
  /** Element that receives the data-* attributes (default `<html>`). */
  root?: HTMLElement;
}

export function PreferencesProvider({ children, initial, root }: PreferencesProviderProps) {
  const [preferences, setPreferences] = useState<Preferences>(() =>
    initial ? { ...defaultPreferences, ...initial } : loadPreferences(),
  );
  const persist = initial === undefined;

  useLayoutEffect(() => {
    applyPreferences(preferences, root ?? document.documentElement);
  }, [preferences, root]);

  const setPreference = useCallback(
    <K extends keyof Preferences>(key: K, value: Preferences[K]) => {
      setPreferences((current) => {
        const next = { ...current, [key]: value };
        if (persist) savePreferences(next);
        return next;
      });
    },
    [persist],
  );

  const value = useMemo<PreferencesContextValue>(
    () => ({
      preferences,
      setPreference,
      reducedMotion: resolveReducedMotion(preferences.reduceMotion),
      reducedTransparency: resolveReducedTransparency(preferences.reduceTransparency),
    }),
    [preferences, setPreference],
  );

  return <PreferencesContext value={value}>{children}</PreferencesContext>;
}
