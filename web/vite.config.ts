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
    sourcemap: true,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: false,
  },
});
