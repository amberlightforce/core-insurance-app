// Generates the typed API surface of the staff screens from the OpenAPI contracts in ../contracts/openapi.
// Output: src/api/generated/<module>.d.ts (committed; regenerate whenever a contract changes).
import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const modules = ['pty', 'pfc', 'pol', 'bil', 'fin', 'clm', 'plt', 'uw', 'ri'];
mkdirSync(join(root, 'src', 'api', 'generated'), { recursive: true });
for (const module of modules) {
  const input = join(root, '..', 'contracts', 'openapi', `${module}.yaml`);
  const output = join(root, 'src', 'api', 'generated', `${module}.d.ts`);
  const result = spawnSync('npx', ['openapi-typescript', input, '-o', output], {
    cwd: root,
    stdio: 'inherit',
    shell: true,
  });
  if (result.status !== 0) process.exit(result.status ?? 1);
}
