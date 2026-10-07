import { createContext, use } from 'react';

import { defaultPreferences, type Preferences } from './preferences';

export interface PreferencesContextValue {
  preferences: Preferences;
  setPreference: <K extends keyof Preferences>(key: K, value: Preferences[K]) => void;
  reducedMotion: boolean;
  reducedTransparency: boolean;
}

export const PreferencesContext = createContext<PreferencesContextValue | null>(null);

const fallback: PreferencesContextValue = {
  preferences: defaultPreferences,
  setPreference: () => undefined,
  reducedMotion: false,
  reducedTransparency: false,
};

/** Preferences, or the defaults when rendered outside the provider (isolated tests). */
export function usePreferences(): PreferencesContextValue {
  return use(PreferencesContext) ?? fallback;
}

/** In-app «Μείωση κίνησης» resolved against the OS setting; JS-driven motion must check it. */
export function useReducedMotion(): boolean {
  return usePreferences().reducedMotion;
}

/** Region format for formatters (independent of the UI language). */
export function useRegionFormat(): Preferences['regionFormat'] {
  return usePreferences().preferences.regionFormat;
}
