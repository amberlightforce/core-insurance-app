import type { ReactNode } from 'react';
import { I18nProvider, UNSAFE_PortalProvider } from 'react-aria';
import { useTranslation } from 'react-i18next';

import { ariaLocaleFor } from './locale';
import { PreferencesProvider, type Preferences } from './preferences';

/** All overlays portal into one `#layer-root` (Part 1 §2.9); falls back to <body> in tests. */
function layerRoot(): HTMLElement {
  return document.getElementById('layer-root') ?? document.body;
}

export interface DesignSystemProviderProps {
  children: ReactNode;
  /** Storybook and tests pass fixed preferences; nothing is persisted then. */
  preferences?: Partial<Preferences>;
}

/** Preferences, React Aria locale (follows the UI language) and the overlay portal root. */
export function DesignSystemProvider({ children, preferences }: DesignSystemProviderProps) {
  const { i18n } = useTranslation();
  return (
    <PreferencesProvider {...(preferences ? { initial: preferences } : {})}>
      <I18nProvider locale={ariaLocaleFor(i18n.language)}>
        <UNSAFE_PortalProvider getContainer={layerRoot}>{children}</UNSAFE_PortalProvider>
      </I18nProvider>
    </PreferencesProvider>
  );
}
