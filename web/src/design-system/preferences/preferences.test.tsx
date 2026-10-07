import { act, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { usePreferences } from './context';
import {
  applyPreferences,
  defaultPreferences,
  loadPreferences,
  preferencesStorageKey,
  resolveReducedMotion,
} from './preferences';
import { PreferencesProvider } from './PreferencesProvider';

function Probe() {
  const { preferences, setPreference } = usePreferences();
  return (
    <button
      type="button"
      onClick={() => {
        setPreference('theme', 'dark');
      }}
    >
      {preferences.theme}
    </button>
  );
}

describe('preferences', () => {
  it('writes data attributes on <html>', () => {
    const root = document.createElement('div');
    applyPreferences(
      { ...defaultPreferences, theme: 'dark', density: 'comfortable', reduceMotion: 'on', reduceTransparency: 'off' },
      root,
    );
    expect(root.dataset.theme).toBe('dark');
    expect(root.dataset.density).toBe('comfortable');
    expect(root.dataset.reduceMotion).toBe('true');
    expect(root.dataset.reduceTransparency).toBe('false');
    applyPreferences(defaultPreferences, root);
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(root.hasAttribute('data-reduce-motion')).toBe(false);
  });

  it('persists changes made through the provider', () => {
    render(
      <PreferencesProvider>
        <Probe />
      </PreferencesProvider>,
    );
    act(() => {
      screen.getByRole('button').click();
    });
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(loadPreferences().theme).toBe('dark');
    expect(localStorage.getItem(preferencesStorageKey)).toContain('"theme":"dark"');
  });

  it('does not persist fixed preferences (Storybook, tests)', () => {
    render(
      <PreferencesProvider initial={{ density: 'comfortable' }}>
        <Probe />
      </PreferencesProvider>,
    );
    act(() => {
      screen.getByRole('button').click();
    });
    expect(localStorage.getItem(preferencesStorageKey)).toBeNull();
  });

  it('falls back to defaults on corrupt storage', () => {
    localStorage.setItem(preferencesStorageKey, '{not json');
    expect(loadPreferences()).toEqual(defaultPreferences);
  });

  it('resolves reduced motion from the in-app setting first', () => {
    expect(resolveReducedMotion('on')).toBe(true);
    expect(resolveReducedMotion('off')).toBe(false);
    expect(resolveReducedMotion('auto')).toBe(false);
  });
});
