/**
 * User appearance settings (Part 1 §3.5). Today they persist in localStorage; when the PLT user profile
 * exists, the profile becomes the source and this store its cache. They are applied as attributes on
 * <html>, which the token and foundation CSS read: data-theme, data-density, data-reduce-motion,
 * data-reduce-transparency, data-effects.
 */
export type ThemePreference = 'auto' | 'light' | 'dark';
export type Density = 'compact' | 'comfortable';
export type TriState = 'auto' | 'on' | 'off';
export type EffectsPreference = 'auto' | 'full' | 'light';
/** Region format is independent of the language (Part 4 §8.2). */
export type RegionFormat = 'el-GR' | 'en-GB';

export interface Preferences {
  theme: ThemePreference;
  density: Density;
  reduceMotion: TriState;
  reduceTransparency: TriState;
  effects: EffectsPreference;
  regionFormat: RegionFormat;
  celebrations: boolean;
  /** WCAG 2.1.4: single-key shortcuts must be switchable off. */
  singleKeyShortcuts: boolean;
  /** Rail expanded (remembered per user, Part 1 §3.4). */
  railExpanded: boolean;
}

export const defaultPreferences: Preferences = {
  theme: 'auto',
  density: 'compact',
  reduceMotion: 'auto',
  reduceTransparency: 'auto',
  effects: 'auto',
  regionFormat: 'el-GR',
  celebrations: true,
  singleKeyShortcuts: true,
  railExpanded: false,
};

export const preferencesStorageKey = 'coreins.preferences';

export function loadPreferences(): Preferences {
  try {
    const raw = localStorage.getItem(preferencesStorageKey);
    if (!raw) return { ...defaultPreferences };
    const parsed = JSON.parse(raw) as Partial<Preferences>;
    return { ...defaultPreferences, ...parsed };
  } catch {
    return { ...defaultPreferences };
  }
}

export function savePreferences(preferences: Preferences): void {
  try {
    localStorage.setItem(preferencesStorageKey, JSON.stringify(preferences));
  } catch {
    // Storage unavailable: the preferences still apply for this session.
  }
}

function setOrRemove(element: HTMLElement, name: string, value: string | null): void {
  if (value === null) element.removeAttribute(name);
  else element.setAttribute(name, value);
}

function triStateAttribute(value: TriState): string | null {
  if (value === 'auto') return null;
  return value === 'on' ? 'true' : 'false';
}

/** Writes the preferences onto <html> (or the given root, used by Storybook and tests). */
export function applyPreferences(
  preferences: Preferences,
  root: HTMLElement = document.documentElement,
): void {
  setOrRemove(root, 'data-theme', preferences.theme === 'auto' ? null : preferences.theme);
  root.setAttribute('data-density', preferences.density);
  setOrRemove(root, 'data-reduce-motion', triStateAttribute(preferences.reduceMotion));
  setOrRemove(root, 'data-reduce-transparency', triStateAttribute(preferences.reduceTransparency));
  setOrRemove(root, 'data-effects', preferences.effects === 'auto' ? null : preferences.effects);
}

function matches(query: string): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia(query).matches
    : false;
}

/** Resolves «Αυτόματο» against the OS setting. */
export function resolveReducedMotion(preference: TriState): boolean {
  if (preference !== 'auto') return preference === 'on';
  return matches('(prefers-reduced-motion: reduce)');
}

export function resolveReducedTransparency(preference: TriState): boolean {
  if (preference !== 'auto') return preference === 'on';
  return matches('(prefers-reduced-transparency: reduce)');
}
