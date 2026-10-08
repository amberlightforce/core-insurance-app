import { CircleHelp, LogIn, LogOut, UserRound } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Button as AriaButton } from 'react-aria-components';

import { Avatar } from '../design-system/components/Avatar';
import { Button } from '../design-system/components/Button';
import { Popover } from '../design-system/components/Popover';
import { SegmentedControl } from '../design-system/components/SegmentedControl';
import { Icon } from '../design-system/icons';
import { usePreferences, type Density, type ThemePreference } from '../design-system/preferences';
import { cx } from '../design-system/utils/cx';
import styles from './AppShell.module.css';
import { LanguageSwitch } from './LanguageSwitch';
import { displayName, isKnownRole, roleKeys, type ShellUser } from './user';

export interface UserMenuProps {
  /** The signed-in user; `null` shows the signed-out trigger and a sign-in action. */
  user: ShellUser | null;
  onOpenHelp: () => void;
  /** Sign in, or switch / sign out (dev sign-in page today, MSAL later). */
  onAccount: () => void;
}

const themes: ThemePreference[] = ['auto', 'light', 'dark'];
const densities: Density[] = ['compact', 'comfortable'];
const themeKeys = {
  auto: 'settings.themeAuto',
  light: 'settings.themeLight',
  dark: 'settings.themeDark',
} as const;
const densityKeys = {
  compact: 'settings.densityCompact',
  comfortable: 'settings.densityComfortable',
} as const;

/**
 * Avatar menu (top bar, v3 mockup `.avatar`): who is signed in and in which role, the appearance settings
 * (theme, density, language — Part 1 §3.5) and help, in one place on every screen (WCAG 3.2.6).
 */
export function UserMenu({ user, onOpenHelp, onAccount }: UserMenuProps) {
  const { t } = useTranslation('shell');
  const { preferences, setPreference } = usePreferences();
  const name = user ? displayName(user.name) : null;
  const roles = (user?.roles ?? []).map((role) => (isKnownRole(role) ? t(roleKeys[role]) : role));

  return (
    <Popover
      placement="bottom end"
      size="md"
      aria-label={t('userMenu.label')}
      className={cx(styles.userPopover)}
      trigger={
        <AriaButton
          className={cx(styles.avatarTrigger)}
          aria-label={name ? t('topBar.userMenu', { name }) : t('userMenu.signedOut')}
        >
          {name ? (
            <Avatar name={name} size="sm" decorative />
          ) : (
            <span className={cx(styles.avatarEmpty)}>
              <Icon icon={UserRound} size={16} />
            </span>
          )}
        </AriaButton>
      }
    >
      <div className={cx(styles.userMenu)}>
        <div className={cx(styles.userHead)}>
          {name ? (
            <Avatar name={name} size="md" decorative />
          ) : (
            <span className={cx(styles.avatarEmptyLg)}>
              <Icon icon={UserRound} size={20} />
            </span>
          )}
          <div className={cx(styles.userText)}>
            <span className={cx(styles.userName)}>{name ?? t('userMenu.signedOut')}</span>
            <span className={cx(styles.userRole)}>
              {roles.length > 0 ? roles.join(' · ') : t('userMenu.signInHint')}
            </span>
          </div>
        </div>
        <SegmentedControl
          label={t('settings.theme')}
          size="sm"
          value={preferences.theme}
          onChange={(value) => {
            const next = themes.find((theme) => theme === value);
            if (next) setPreference('theme', next);
          }}
          options={themes.map((id) => ({ id, label: t(themeKeys[id]) }))}
        />
        <SegmentedControl
          label={t('settings.density')}
          size="sm"
          value={preferences.density}
          onChange={(value) => {
            const next = densities.find((density) => density === value);
            if (next) setPreference('density', next);
          }}
          options={densities.map((id) => ({ id, label: t(densityKeys[id]) }))}
        />
        <div className={cx(styles.userField)}>
          <span className={cx(styles.userFieldLabel)} aria-hidden="true">
            {t('language.label')}
          </span>
          <LanguageSwitch />
        </div>
        <div className={cx(styles.userActions)}>
          <Button variant="ghost" icon={CircleHelp} shortcut="?" onPress={onOpenHelp}>
            {t('topBar.help')}
          </Button>
          <Button variant="ghost" icon={user ? LogOut : LogIn} onPress={onAccount}>
            {user ? t('userMenu.switch') : t('userMenu.signIn')}
          </Button>
        </div>
      </div>
    </Popover>
  );
}
