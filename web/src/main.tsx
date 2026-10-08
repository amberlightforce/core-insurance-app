import { QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { createBrowserRouter, RouterProvider } from 'react-router';

import { DesignSystemProvider } from './design-system/DesignSystemProvider';
import './design-system/styles.css';
import { onUnauthorized } from './api/client';
import { createQueryClient } from './api/queryClient';
import './i18n';
import { routes } from './routes';

const queryClient = createQueryClient();

const router = createBrowserRouter(routes);

// A 401 means the dev token is stale (the api restarted with a new key): forget it and ask for a new sign-in.
onUnauthorized(() => {
  queryClient.clear();
  if (!window.location.pathname.startsWith('/dev/sign-in')) {
    void router.navigate('/dev/sign-in?expired=1', { replace: true });
  }
});

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
