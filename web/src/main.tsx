import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';

import { DesignSystemProvider } from './design-system/DesignSystemProvider';
import './design-system/styles.css';
import { createQueryClient } from './api/queryClient';
import './i18n';
import { routes } from './routes';

const queryClient = createQueryClient();

const router = createBrowserRouter(routes);

const rootElement = document.getElementById('root');
if (!rootElement) {
  throw new Error('Root element #root not found');
}

createRoot(rootElement).render(
  <StrictMode>
    <DesignSystemProvider>
      <QueryClientProvider client={queryClient}>
        <RouterProvider router={router} />
      </QueryClientProvider>
    </DesignSystemProvider>
  </StrictMode>,
);
