interface NavigatorWithUAData extends Navigator {
  userAgentData?: { platform?: string };
}

/** macOS / iPadOS show ⌘, everything else Ctrl (Part 2 §4.34): `userAgentData.platform`, else the UA string. */
export function isApplePlatform(): boolean {
  if (typeof navigator === 'undefined') return false;
  const nav = navigator as NavigatorWithUAData;
  const platform = nav.userAgentData?.platform ?? nav.userAgent;
  return /mac|iphone|ipad|ipod/i.test(platform);
}

/** Splits a shortcut and replaces `Mod` with ⌘ or Ctrl: `formatShortcut('Mod+K')` → `['Ctrl', 'K']`. */
export function formatShortcut(shortcut: string, apple = isApplePlatform()): string[] {
  return shortcut
    .split('+')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => {
      if (part === 'Mod') return apple ? '⌘' : 'Ctrl';
      if (part === 'Enter') return '↵';
      return part;
    });
}

/** True when the event matches `Mod+K`-style shortcuts (`Mod` = ⌘ on Apple, Ctrl elsewhere). */
export function matchesShortcut(
  event: KeyboardEvent | React.KeyboardEvent,
  shortcut: string,
): boolean {
  const parts = shortcut.split('+').map((p) => p.trim().toLowerCase());
  const key = parts[parts.length - 1] ?? '';
  const wantMod = parts.includes('mod');
  const wantShift = parts.includes('shift');
  const wantAlt = parts.includes('alt');
  const modPressed = isApplePlatform() ? event.metaKey : event.ctrlKey;
  return (
    event.key.toLowerCase() === key &&
    modPressed === wantMod &&
    event.shiftKey === wantShift &&
    event.altKey === wantAlt
  );
}
