import { render, type RenderOptions, type RenderResult } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactElement, ReactNode } from 'react';

import { DesignSystemProvider } from '../design-system/DesignSystemProvider';
import type { Preferences } from '../design-system/preferences';

export interface RenderWithDsOptions extends Omit<RenderOptions, 'wrapper'> {
  preferences?: Partial<Preferences>;
}

/**
 * Renders inside the design-system providers (preferences, React Aria locale, portal root) and returns a
 * userEvent instance. Change the language with `await i18n.changeLanguage('en')` before rendering.
 */
export function renderWithDs(
  ui: ReactElement,
  { preferences, ...options }: RenderWithDsOptions = {},
): RenderResult & { user: ReturnType<typeof userEvent.setup> } {
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <DesignSystemProvider preferences={preferences ?? {}}>{children}</DesignSystemProvider>
  );
  const user = userEvent.setup();
  return { user, ...render(ui, { wrapper: Wrapper, ...options }) };
}
