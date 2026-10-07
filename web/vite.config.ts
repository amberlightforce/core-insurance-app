import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

import react from '@vitejs/plugin-react';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';

const fontsDir = join(import.meta.dirname, 'src', 'design-system', 'fonts');

/**
 * The self-hosted fonts are SIL OFL 1.1, which requires the licence to travel with the font files: copy the
 * licence texts next to the WOFF2 files that Vite emits into dist/assets.
 */
function fontLicences(): Plugin {
  return {
    name: 'coreins-font-licences',
    apply: 'build',
    generateBundle() {
      for (const name of readdirSync(fontsDir).filter((file) => /^LICENSE-.*\.txt$/.test(file))) {
        this.emitFile({
          type: 'asset',
          fileName: `assets/${name}`,
          source: readFileSync(join(fontsDir, name)),
        });
      }
    },
  };
}

// Dev server on 5173 proxies API calls to the Host on 5000 (INFRASTRUCTURE §3.2).
// `npm run build` writes dist/, which the Dockerfile copies into the Host's wwwroot.
export default defineConfig({
  plugins: [react(), fontLicences()],
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
    // axe-core on open overlays in jsdom can exceed the 5 s default when all files run in parallel.
    testTimeout: 15_000,
  },
});
