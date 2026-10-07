import { tokenNames } from './token-names';

export { tokenNames };

/** Every CSS custom property defined by aegean.css or aegean-extensions.css, without the leading `--`. */
export type TokenName = (typeof tokenNames)[number];

/** Components must not reference primitive palette tokens (Part 1 §2.0). */
export type SemanticTokenName = Exclude<TokenName, `palette-${string}`>;

/** `var(--name)` for inline styles that must stay on tokens (for example a width computed from a token). */
export function cssVar(name: SemanticTokenName): string {
  return `var(--${name})`;
}

export function isTokenName(value: string): value is TokenName {
  return (tokenNames as readonly string[]).includes(value);
}

/** Reads the resolved value of a token on an element (defaults to `<html>`). */
export function readToken(
  name: SemanticTokenName,
  element: Element = document.documentElement,
): string {
  return getComputedStyle(element).getPropertyValue(`--${name}`).trim();
}

/**
 * Breakpoints `bp.*` (Part 1 §3.2). CSS custom properties cannot be used in media queries, so the shell
 * CSS repeats these numbers with a `bp.*` comment; JS uses this table.
 */
export const breakpoints = {
  xs: 0,
  sm: 600,
  md: 905,
  lg: 1240,
  xl: 1440,
  '2xl': 1920,
} as const;

export type Breakpoint = keyof typeof breakpoints;

export function minWidthQuery(bp: Breakpoint): string {
  return `(min-width: ${String(breakpoints[bp])}px)`;
}

/** Springs for Motion (DESIGN-A §5.10); each has a CSS fallback duration and easing token. */
export const springs = {
  snappy: { type: 'spring', stiffness: 520, damping: 42, mass: 1 },
  smooth: { type: 'spring', stiffness: 280, damping: 32, mass: 1 },
  gentle: { type: 'spring', stiffness: 180, damping: 24, mass: 1 },
  expressive: { type: 'spring', stiffness: 320, damping: 18, mass: 1 },
} as const;

export type SpringName = keyof typeof springs;

/** Motion durations in milliseconds for JS timers (mirrors the `--motion-duration-*` tokens). */
export const durations = {
  instant: 0,
  feedbackXs: 70,
  feedbackSm: 100,
  feedbackMd: 120,
  feedbackLg: 150,
  transitionSm: 200,
  transitionMd: 240,
  transitionLg: 320,
  transitionXl: 400,
  signatureBeat: 600,
  signatureMax: 1600,
  delayTooltip: 400,
  tooltipWarmWindow: 1500,
  delaySkeleton: 150,
  delaySpinner: 400,
  minDisplaySkeleton: 300,
  staggerList: 16,
  /** MI-67 commit-ready sweep. */
  sweep: 700,
  /** Toast durations (Part 2 §4.21). Error toasts persist. */
  toastSuccess: 4000,
  toastWithAction: 6000,
  toastInfo: 5000,
  toastWarning: 8000,
} as const;

/** Semantic form field widths in grid columns at ≥1240 / 905–1239 / <905 (Part 1 §3.1). */
export const fieldWidths = {
  xs: [2, 3, 6],
  sm: [3, 4, 12],
  md: [4, 6, 12],
  lg: [6, 12, 12],
  full: [12, 12, 12],
} as const;

export type FieldWidth = keyof typeof fieldWidths;

/** D-FE-07 queue row heights in px, for virtualiser estimates. */
export const queueRowHeights = { compact: 60, comfortable: 64, touch: 72 } as const;
export const tableRowHeights = { compact: 32, comfortable: 40, touch: 48 } as const;
export const tableHeaderHeight = 38;

/** The 10 status families (`color.status.<family>.*`). */
export const statusFamilies = [
  'neutral',
  'success',
  'warning',
  'danger',
  'info',
  'brand',
  'teal',
  'plum',
  'ai',
  'ochre',
] as const;

export type StatusFamily = (typeof statusFamilies)[number];
