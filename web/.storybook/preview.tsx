import type { Decorator, Preview } from '@storybook/react-vite';
import { useEffect, type ReactNode } from 'react';

import { DesignSystemProvider } from '../src/design-system/DesignSystemProvider';
import type { Density, ThemePreference } from '../src/design-system/preferences';
import '../src/design-system/styles.css';
import i18n from '../src/i18n';

interface FrameProps {
  locale: 'el' | 'en';
  theme: ThemePreference;
  density: Density;
  children: ReactNode;
}

function StoryFrame({ locale, theme, density, children }: FrameProps) {
  useEffect(() => {
    void i18n.changeLanguage(locale);
  }, [locale]);

  return (
    <DesignSystemProvider key={`${locale}-${theme}-${density}`} preferences={{ theme, density }}>
      <div
        style={{
          padding: 'var(--space-6)',
          background: 'var(--color-surface-sheet)',
          minHeight: '100vh',
        }}
      >
        {children}
      </div>
    </DesignSystemProvider>
  );
}

/** Every story renders in light/dark, el/en and compact/comfortable through the toolbar. */
const withDesignSystem: Decorator = (Story, context) => (
  <StoryFrame
    locale={context.globals.locale === 'en' ? 'en' : 'el'}
    theme={(context.globals.theme ?? 'light') as ThemePreference}
    density={(context.globals.density ?? 'compact') as Density}
  >
    <Story />
  </StoryFrame>
);

const preview: Preview = {
  decorators: [withDesignSystem],
  globalTypes: {
    locale: {
      description: 'Language',
      toolbar: {
        title: 'Language',
        icon: 'globe',
        items: [
          { value: 'el', title: 'Ελληνικά' },
          { value: 'en', title: 'English' },
        ],
        dynamicTitle: true,
      },
    },
    theme: {
      description: 'Theme',
      toolbar: {
        title: 'Theme',
        icon: 'mirror',
        items: [
          { value: 'light', title: 'Light' },
          { value: 'dark', title: 'Dark' },
        ],
        dynamicTitle: true,
      },
    },
    density: {
      description: 'Density',
      toolbar: {
        title: 'Density',
        icon: 'component',
        items: [
          { value: 'compact', title: 'Compact' },
          { value: 'comfortable', title: 'Comfortable' },
        ],
        dynamicTitle: true,
      },
    },
  },
  initialGlobals: { locale: 'el', theme: 'light', density: 'compact' },
  parameters: {
    layout: 'fullscreen',
    a11y: { test: 'error' },
    controls: { expanded: true },
  },
};

export default preview;
