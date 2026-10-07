import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';

import { App } from './App';
import { DesignSystemProvider } from './design-system/DesignSystemProvider';
import './design-system/styles.css';
import './i18n';

const queryClient = new QueryClient();

const router = createBrowserRouter([{ path: '/', element: <App /> }]);

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
