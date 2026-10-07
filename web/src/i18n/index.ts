import i18n from 'i18next';
import ICU from 'i18next-icu';
import { initReactI18next } from 'react-i18next';

import el from './locales/el.json';
import en from './locales/en.json';

/** Greek is the default language and the language of record; English is secondary (PLAN §4.6). */
export const defaultLanguage = 'el';
export const supportedLanguages = ['el', 'en'] as const;

void i18n
  .use(ICU)
  .use(initReactI18next)
  .init({
    lng: defaultLanguage,
    fallbackLng: defaultLanguage,
    supportedLngs: supportedLanguages,
    resources: {
      el: { translation: el },
      en: { translation: en },
    },
    interpolation: { escapeValue: false },
    initAsync: false,
  });

export default i18n;
