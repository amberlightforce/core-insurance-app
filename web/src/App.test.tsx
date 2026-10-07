import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';

import { App } from './App';
import './i18n';

describe('App', () => {
  it('renders the Greek portal title by default', () => {
    render(<App />);

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Πύλη ασφάλισης');
  });
});
