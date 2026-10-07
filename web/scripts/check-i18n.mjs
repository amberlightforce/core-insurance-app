// i18n lint (F-1d): fails when a translation key used in code is missing from Greek or English,
// when the two languages do not have the same keys, or when a message is not valid ICU.
//
// Key discovery: string-literal first arguments of t(...), i18n.t(...) and <Trans i18nKey="...">.
// Namespaces: `ns:key` wins; otherwise the namespaces passed to useTranslation(...) in the same file
// (any of them may hold the key, as i18next searches them in order); otherwise `common`.
// Dynamic keys (template literals with ${}) are skipped: keep them rare and list them in a const map.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import IntlMessageFormat from 'intl-messageformat';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const src = join(root, 'src');
const languages = ['el', 'en'];
const areaAliases = { 'design-system': 'ds', 'app-shell': 'shell' };

function walk(dir, filter, out = []) {
  for (const name of readdirSync(dir)) {
    const full = join(dir, name);
    if (statSync(full).isDirectory()) {
      if (name !== 'node_modules') walk(full, filter, out);
    } else if (filter(full)) {
      out.push(full);
    }
  }
  return out;
}

function merge(target, source) {
  for (const [key, value] of Object.entries(source)) {
    if (value && typeof value === 'object' && !Array.isArray(value)) {
      target[key] = merge(target[key] && typeof target[key] === 'object' ? target[key] : {}, value);
    } else {
      target[key] = value;
    }
  }
  return target;
}

function flatten(obj, prefix = '', out = new Map()) {
  for (const [key, value] of Object.entries(obj)) {
    const path = prefix ? `${prefix}.${key}` : key;
    if (value && typeof value === 'object') flatten(value, path, out);
    else out.set(path, value);
  }
  return out;
}

export function loadResources() {
  const resources = Object.fromEntries(languages.map((l) => [l, {}]));
  for (const file of walk(src, (f) => f.endsWith('.json'))) {
    const rel = relative(src, file).split(sep).join('/');
    let match = /^i18n\/locales\/([^/]+)\/([^/]+)\.json$/.exec(rel);
    let lng;
    let ns;
    if (match) {
      [, lng, ns] = match;
    } else {
      match = /^(?:modules\/)?([^/]+)\/i18n\/([^/]+)\/[^/]+\.json$/.exec(rel);
      if (!match) continue;
      lng = match[2];
      ns = areaAliases[match[1]] ?? match[1];
    }
    if (!resources[lng]) continue;
    resources[lng][ns] = merge(resources[lng][ns] ?? {}, JSON.parse(readFileSync(file, 'utf8')));
  }
  return resources;
}

function namespacesIn(source) {
  const found = [];
  for (const m of source.matchAll(/useTranslation\(\s*(\[[^\]]*\]|['"][^'"]+['"])?/g)) {
    if (!m[1]) {
      found.push('common');
      continue;
    }
    for (const n of m[1].matchAll(/['"]([^'"]+)['"]/g)) found.push(n[1]);
  }
  return found.length ? [...new Set(found)] : ['common'];
}

export function findKeys(source) {
  const keys = [];
  for (const m of source.matchAll(/(?<![\w$])t\(\s*(['"`])((?:(?!\1).)+)\1/g)) {
    if (m[1] === '`' && m[2].includes('${')) continue;
    keys.push(m[2]);
  }
  for (const m of source.matchAll(/i18nKey=["']([^"']+)["']/g)) keys.push(m[1]);
  return keys;
}

export function check() {
  const problems = [];
  const resources = loadResources();
  const flat = Object.fromEntries(
    languages.map((l) => [
      l,
      Object.fromEntries(Object.entries(resources[l]).map(([ns, msgs]) => [ns, flatten(msgs)])),
    ]),
  );

  // Parity and ICU validity.
  const allNs = new Set(languages.flatMap((l) => Object.keys(flat[l])));
  for (const ns of allNs) {
    for (const lng of languages) {
      const other = languages.find((l) => l !== lng);
      for (const [key, message] of flat[lng][ns] ?? new Map()) {
        if (!flat[other][ns]?.has(key))
          problems.push(`${ns}:${key} exists in ${lng} but not in ${other}`);
        if (typeof message !== 'string') {
          problems.push(`${lng} ${ns}:${key} is not a string`);
          continue;
        }
        try {
          new IntlMessageFormat(message, lng === 'el' ? 'el-GR' : 'en-GB');
        } catch (error) {
          problems.push(`${lng} ${ns}:${key} is not valid ICU: ${error.message}`);
        }
      }
    }
  }

  // Keys used in code.
  const files = walk(src, (f) => /\.(ts|tsx)$/.test(f) && !f.endsWith('.d.ts'));
  for (const file of files) {
    const source = readFileSync(file, 'utf8');
    const keys = findKeys(source);
    if (!keys.length) continue;
    const fileNs = namespacesIn(source);
    for (const raw of keys) {
      const sepIndex = raw.indexOf(':');
      const candidates = sepIndex > 0 ? [raw.slice(0, sepIndex)] : fileNs;
      const key = sepIndex > 0 ? raw.slice(sepIndex + 1) : raw;
      for (const lng of languages) {
        const found = candidates.some((ns) => {
          const map = flat[lng][ns];
          if (!map) return false;
          if (map.has(key)) return true;
          // i18next plural/context suffixes.
          return [...map.keys()].some((k) => k.startsWith(`${key}_`));
        });
        if (!found) {
          problems.push(
            `${relative(root, file)}: missing ${lng} key "${raw}" (namespaces: ${candidates.join(', ')})`,
          );
        }
      }
    }
  }
  return problems;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const problems = check();
  if (problems.length) {
    console.error(problems.join('\n'));
    console.error(`\ni18n check failed: ${problems.length} problem(s)`);
    process.exit(1);
  }
  console.log('i18n check passed');
}
