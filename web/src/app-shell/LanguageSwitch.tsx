import { useTranslation } from 'react-i18next';
import { ToggleButton, ToggleButtonGroup, type Key } from 'react-aria-components';

import { cx } from '../design-system/utils/cx';
import { changeLanguage, isLanguage, supportedLanguages } from '../i18n';
import styles from './AppShell.module.css';

const shortNames = { el: 'ΕΛ', en: 'EN' } as const;

/**
 * Language switch (R-101): Ελληνικά / English. Each option is named in its own language and marked with
 * `lang`. The choice is persisted (localStorage now, PLT profile later) and `<html lang>` follows it.
 */
export function LanguageSwitch() {
  const { t, i18n } = useTranslation('shell');
  const current = isLanguage(i18n.language) ? i18n.language : 'el';

  const onChange = (keys: Set<Key>) => {
    const [next] = [...keys];
    if (typeof next === 'string' && isLanguage(next) && next !== current) {
      void changeLanguage(next);
    }
  };

  return (
    <ToggleButtonGroup
      className={cx(styles.language)}
      aria-label={t('language.label')}
      selectionMode="single"
      disallowEmptySelection
      selectedKeys={[current]}
      onSelectionChange={onChange}
    >
      {supportedLanguages.map((lng) => (
        <ToggleButton
          key={lng}
          id={lng}
          className={cx(styles.languageOption)}
          aria-label={t(`language.${lng}`)}
          lang={lng}
        >
          {shortNames[lng]}
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  );
}
