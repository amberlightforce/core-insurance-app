import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import { defineConfig, globalIgnores } from 'eslint/config';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default defineConfig([
  globalIgnores([
    'dist',
    'coverage',
    'storybook-static',
    'src/api/schema.d.ts',
    'src/api/generated',
  ]),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
      reactHooks.configs.flat['recommended-latest'],
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2023,
      globals: globals.browser,
      parserOptions: {
        project: ['./tsconfig.json', './tsconfig.node.json'],
        tsconfigRootDir: import.meta.dirname,
      },
    },
  },
  {
    // Customer portal (BO-CHN-12, README open decision #1): same Vite app, own shell, NO staff imports. Portal code may
    // use the design system, api client, i18n, format, dev session and role list; never staff modules, the staff shell
    // or the staff route tree. Tests may import the shared test helpers.
    files: ['src/portal/**/*.{ts,tsx}'],
    rules: {
      'no-restricted-imports': [
        'error',
        {
          patterns: [
            {
              group: [
                '**/modules/**',
                '**/app-shell/**',
                '**/App',
                '**/App.tsx',
                '**/routes',
                '**/routes.tsx',
                '**/auth/RequireRole',
                '**/dev-auth/DevSignIn',
              ],
              message:
                'Portal code must not import staff modules, the staff shell or the staff routes (BO-CHN-12). Use src/portal/ui or the design system.',
            },
          ],
        },
      ],
    },
  },
  {
    // Stories and the Storybook preview are not hot-reloaded app modules.
    files: ['.storybook/**/*.{ts,tsx}', 'src/**/*.stories.tsx'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    files: ['**/*.{js,mjs}'],
    extends: [js.configs.recommended],
    languageOptions: { globals: globals.node },
  },
  prettier,
]);
