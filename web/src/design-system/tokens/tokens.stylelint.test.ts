// @vitest-environment node
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

import stylelint from 'stylelint';
import { describe, expect, it } from 'vitest';

// The no-raw-values gate (rule 1, DESIGN-B G.9) is only as good as its patterns: lint planted violations
// and a token-only file with the project's real Stylelint config.
const web = join(import.meta.dirname, '..', '..', '..');
const fixtures = join(web, 'scripts', 'stylelint-fixtures');
const configFile = join(web, '.stylelintrc.json');

async function lintFixture(name: string) {
  const file = join(fixtures, name);
  const code = readFileSync(file, 'utf8');
  const result = await stylelint.lint({ code, codeFilename: join(web, 'src', name), configFile });
  // Only the token-gate rules count; generic style rules (duplicates, notation) are not what is tested.
  const gateRules = new Set([
    'declaration-property-value-disallowed-list',
    'color-no-hex',
    'color-named',
    'function-disallowed-list',
  ]);
  const warnings = (result.results[0]?.warnings ?? []).filter((w) => gateRules.has(w.rule));
  return { code, warnings };
}

describe('Stylelint token gate', () => {
  it('reports every planted violation', async () => {
    const { code, warnings } = await lintFixture('violations.css');
    const lines = code.split('\n');
    const declarationLines = lines
      .map((line, index) => ({ line: index + 1, text: line.trim() }))
      .filter(({ text }) => /^[a-z-]+\s*:/.test(text));
    expect(declarationLines.length).toBeGreaterThanOrEqual(30);
    const reported = new Set(warnings.map((w) => w.line));
    const missed = declarationLines.filter(({ line }) => !reported.has(line)).map((d) => d.text);
    expect(missed).toEqual([]);
  });

  it('accepts token-only declarations', async () => {
    const { warnings } = await lintFixture('clean.css');
    expect(warnings.map((w) => `${String(w.line)}: ${w.text}`)).toEqual([]);
  });
});
