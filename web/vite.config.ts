import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// Dev server on 5173 proxies API calls to the Host on 5000 (INFRASTRUCTURE §3.2).
// `npm run build` writes dist/, which the Dockerfile copies into the Host's wwwroot.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': 'http://localhost:5000',
    },
  },
  build: {
    outDir: 'dist',
    // Maps are generated for error decoding but not referenced from the bundles; the Dockerfile drops them.
    sourcemap: 'hidden',
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
