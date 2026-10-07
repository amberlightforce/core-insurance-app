import type { StorybookConfig } from '@storybook/react-vite';

// Component workbench for the Aegean design system (D-FE-01). The a11y addon runs axe on every story.
const config: StorybookConfig = {
  stories: ['../src/**/*.stories.@(ts|tsx)'],
  addons: ['@storybook/addon-a11y'],
  framework: { name: '@storybook/react-vite', options: {} },
  core: { disableTelemetry: true },
};

export default config;
