import { useTranslation } from 'react-i18next';

/** Localised text of a catalogue `{el, en}` pair, following the UI language. */
export function useLocalised() {
  const { i18n } = useTranslation();
  return (text: { el: string; en: string }) => (i18n.language === 'en' ? text.en : text.el);
}
