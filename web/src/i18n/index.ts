import i18n from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';

/**
 * i18n (D-FE-02): react-i18next + i18next-icu, ICU plural/select messages.
 *
 * Namespaces are one per module. A namespace is the merge of every JSON file found at
 *   src/i18n/locales/<lng>/<ns>.json                 (app-wide: common, shell)
 *   src/<area>/i18n/<lng>/<part>.json                (merged into namespace <area> after the alias below)
 * The design system's parts merge into the `ds` namespace. Feature modules add
 * `src/modules/<module>/i18n/<lng>/*.json`, which merge into the `<module>` namespace.
 * `npm run lint:i18n` (scripts/check-i18n.mjs) applies the same rules and fails on a missing key.
 *
 * Greek is the default language and the language of record; English falls back to Greek, never to the key
 * (Part 4 §8.6). Formats follow the region-format preference, never the language (see src/format).
 */
export const defaultLanguage = 'el';
export const supportedLanguages = ['el', 'en'] as const;
export type Language = (typeof supportedLanguages)[number];

export const languageStorageKey = 'coreins.language';

type Messages = Record<string, unknown>;

const areaAliases: Record<string, string> = { 'design-system': 'ds', 'app-shell': 'shell' };

const appFiles = import.meta.glob<Messages>('./locales/*/*.json', {
  eager: true,
  import: 'default',
});
const areaFiles = import.meta.glob<Messages>(['../*/i18n/*/*.json', '../modules/*/i18n/*/*.json'], {
  eager: true,
  import: 'default',
});

function isPlainObject(value: unknown): value is Messages {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** Deep merge; a key defined twice is a bug, so the later file wins only for distinct keys. */
export function mergeMessages(target: Messages, source: Messages): Messages {
  for (const [key, value] of Object.entries(source)) {
    const existing = target[key];
    if (isPlainObject(existing) && isPlainObject(value)) {
      mergeMessages(existing, value);
    } else {
      target[key] = isPlainObject(value) ? mergeMessages({}, value) : value;
    }
  }
  return target;
}

function buildResources(): Record<Language, Record<string, Messages>> {
  const resources: Record<Language, Record<string, Messages>> = { el: {}, en: {} };
  const add = (lng: string | undefined, ns: string | undefined, messages: Messages) => {
    if (!lng || !ns || !isLanguage(lng)) return;
    const bucket = resources[lng];
    bucket[ns] = mergeMessages(bucket[ns] ?? {}, messages);
  };

  for (const [path, messages] of Object.entries(appFiles)) {
    const match = /\.\/locales\/([^/]+)\/([^/]+)\.json$/.exec(path);
    add(match?.[1], match?.[2], messages);
  }
  for (const [path, messages] of Object.entries(areaFiles)) {
    const match = /\.\.\/(?:modules\/)?([^/]+)\/i18n\/([^/]+)\/[^/]+\.json$/.exec(path);
    const area = match?.[1];
    add(match?.[2], area ? (areaAliases[area] ?? area) : undefined, messages);
  }
  return resources;
}

export const resources = buildResources();
export const namespaces = Object.keys(resources.el);

type MissingKeyReporter = (lngs: readonly string[], ns: string, key: string) => void;

let reportMissingKey: MissingKeyReporter = (lngs, ns, key) => {
  if (import.meta.env.DEV) {
    console.warn(`i18n.missing ${ns}:${key} [${lngs.join(',')}]`);
  }
};

/** Telemetry hook: the PLT client replaces this to send `i18n.missing` events (Part 4 §8.6). */
export function setMissingKeyReporter(reporter: MissingKeyReporter): void {
  reportMissingKey = reporter;
}

function readStoredLanguage(): Language | null {
  try {
    const stored = localStorage.getItem(languageStorageKey);
    return stored === 'el' || stored === 'en' ? stored : null;
  } catch {
    return null;
  }
}

export function isLanguage(value: string): value is Language {
  return (supportedLanguages as readonly string[]).includes(value);
}

/** Keeps `<html lang>` in step with the UI language (WCAG 3.1.1, R-101). */
function applyDocumentLanguage(lng: string): void {
  if (typeof document !== 'undefined') {
    document.documentElement.lang = isLanguage(lng) ? lng : defaultLanguage;
  }
}

void i18n
  .use(ICU)
  .use(initReactI18next)
  .init({
    lng: readStoredLanguage() ?? defaultLanguage,
    fallbackLng: defaultLanguage,
    supportedLngs: supportedLanguages,
    ns: namespaces,
    defaultNS: 'common',
    resources,
    interpolation: { escapeValue: false },
    initAsync: false,
    returnNull: false,
    saveMissing: true,
    missingKeyHandler: (lngs, ns, key) => {
      reportMissingKey(lngs, ns, key);
    },
    react: { useSuspense: false },
  });

applyDocumentLanguage(i18n.language);
i18n.on('languageChanged', applyDocumentLanguage);

/**
 * Switches the UI language and persists it (R-101). Today the choice lives in localStorage;
 * when the PLT user profile exists, the profile is the source and this becomes its cache.
 */
export async function changeLanguage(lng: Language): Promise<void> {
  try {
    localStorage.setItem(languageStorageKey, lng);
  } catch {
    // Storage can be unavailable (private mode); the switch still applies for this session.
  }
  await i18n.changeLanguage(lng);
}

export default i18n;
