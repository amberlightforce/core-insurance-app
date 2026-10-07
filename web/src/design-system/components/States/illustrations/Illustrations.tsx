/*
 * PLACEHOLDER illustrations ILL-01…ILL-07 (D-FE-09: known stubs until the "Cycladic line" set is drawn).
 * Each is a deliberately simple inline SVG that follows the Part 3 §7.3 construction rules: 240×180 master,
 * 1.5 px round-cap line in a text token, at most three tints from the status bg tokens, one gradient accent,
 * no text, no people, decorative (`aria-hidden`), theme-aware through CSS variables. Motion (the 6 s drift)
 * is not implemented in the placeholders.
 */
import { useId, type ReactNode } from 'react';

import { cx } from '../../../utils/cx';
import styles from './Illustration.module.css';

export type IllustrationSize = 'sm' | 'md' | 'lg';
type Accent = 'tide' | 'iris' | 'laurel';

export interface IllustrationProps {
  /** sm 120×90 (cards, done-empty), md 240×180 (page empties, permission denied), lg 320×240 (errors). */
  size?: IllustrationSize;
}

const dimensions: Record<IllustrationSize, [number, number]> = {
  sm: [120, 90],
  md: [240, 180],
  lg: [320, 240],
};

const accentClasses: Record<Accent, [string | undefined, string | undefined]> = {
  tide: [styles.tideStart, styles.tideEnd],
  iris: [styles.irisStart, styles.irisEnd],
  laurel: [styles.laurelStart, styles.laurelEnd],
};

interface FrameProps extends IllustrationProps {
  id: string;
  accent: Accent;
  children: (accentRef: string) => ReactNode;
}

function Frame({ id, size = 'md', accent, children }: FrameProps) {
  const gradientId = useId();
  const [width, height] = dimensions[size];
  const [start, end] = accentClasses[accent];
  return (
    <svg
      className={styles.illustration}
      data-illustration={id}
      width={width}
      height={height}
      viewBox="0 0 240 180"
      aria-hidden="true"
      focusable="false"
    >
      <defs>
        <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" className={start} />
          <stop offset="1" className={end} />
        </linearGradient>
      </defs>
      {children(`url(#${gradientId})`)}
    </svg>
  );
}

const line = styles.line;

/** ILL-01 · first-use empty: an empty arch with a document on the step. */
export function IllEmptyArch(props: IllustrationProps) {
  return (
    <Frame id="ILL-01" accent="tide" {...props}>
      {(accent) => (
        <>
          <rect className={styles.tint3} x="40" y="136" width="160" height="16" />
          <path className={styles.tint1} d="M72 136V72a48 48 0 0 1 96 0v64z" />
          <path className={line} d="M72 136V72a48 48 0 0 1 96 0v64M40 136h160M40 152h160" />
          <rect className={styles.tint2} x="104" y="112" width="32" height="24" />
          <path className={line} d="M104 136v-24h24l8 8v16M112 120h16M112 128h12" />
          <path className={styles.accent} stroke={accent} fill="none" d="M24 160h192" />
        </>
      )}
    </Frame>
  );
}

/** ILL-02 · permission denied: a stair to a closed door with a small lock. */
export function IllLockedDoor(props: IllustrationProps) {
  return (
    <Frame id="ILL-02" accent="tide" {...props}>
      {(accent) => (
        <>
          <rect className={styles.tint1} x="136" y="40" width="64" height="112" />
          <path className={line} d="M136 152V40h64v112" />
          <path className={line} d="M40 152h24v-16h24v-16h24v-16h24" />
          <rect className={styles.tint3} x="156" y="88" width="24" height="20" rx="3" />
          <path className={line} d="M156 88h24v20h-24zM162 88v-6a6 6 0 0 1 12 0v6" />
          <path className={styles.accent} stroke={accent} fill="none" d="M24 160h192" />
        </>
      )}
    </Frame>
  );
}

/** ILL-03 · done-empty (Laurel): a calm horizon, a cleared table and an olive branch. */
export function IllCalmHorizon(props: IllustrationProps) {
  return (
    <Frame id="ILL-03" accent="laurel" {...props}>
      {(accent) => (
        <>
          <rect className={styles.tint2} x="24" y="96" width="192" height="40" />
          <path className={line} d="M24 96h192M64 136h112M80 136v24M160 136v24" />
          <path
            className={styles.accent}
            stroke={accent}
            fill="none"
            d="M88 72c16-24 48-32 72-24M104 60c-4-8 0-14 8-16M124 52c-2-8 4-14 12-14M144 50c2-8 10-12 16-10"
          />
          <circle className={styles.tint1} cx="180" cy="56" r="12" />
        </>
      )}
    </Frame>
  );
}

/** ILL-04 · no search results: a map fold with a pin and a dashed path. */
export function IllMapFold(props: IllustrationProps) {
  return (
    <Frame id="ILL-04" accent="tide" {...props}>
      {(accent) => (
        <>
          <path className={styles.tint3} d="M48 48l48-16 48 16 48-16v104l-48 16-48-16-48 16z" />
          <path
            className={line}
            d="M48 48l48-16 48 16 48-16v104l-48 16-48-16-48 16zM96 32v104M144 48v104"
          />
          <path className={cx(line, styles.dashed)} d="M68 120c16-24 40-8 56-32" />
          <path
            className={styles.accent}
            stroke={accent}
            fill="none"
            d="M160 96c-10-12-14-20-14-28a14 14 0 0 1 28 0c0 8-4 16-14 28z"
          />
        </>
      )}
    </Frame>
  );
}

/** ILL-05 · page error: a cubic house with a cracked plane and a shield. */
export function IllCrackedHouse(props: IllustrationProps) {
  return (
    <Frame id="ILL-05" accent="tide" {...props}>
      {(accent) => (
        <>
          <rect className={styles.tint1} x="56" y="64" width="96" height="88" />
          <path className={line} d="M56 152V64h96v88M40 152h160M96 64l8 24-12 16 10 20" />
          <rect className={styles.tint3} x="72" y="112" width="20" height="40" />
          <path
            className={styles.accent}
            stroke={accent}
            fill="none"
            d="M176 88l20 8v16c0 14-10 22-20 26-10-4-20-12-20-26V96z"
          />
        </>
      )}
    </Frame>
  );
}

/** ILL-06 · offline: a cloud with a dashed line to a house. */
export function IllOfflineCloud(props: IllustrationProps) {
  return (
    <Frame id="ILL-06" accent="tide" {...props}>
      {(accent) => (
        <>
          <path
            className={styles.tint1}
            d="M56 76a20 20 0 0 1 20-20 28 28 0 0 1 52 8 16 16 0 0 1 0 32H72a16 16 0 0 1-16-20z"
          />
          <path
            className={line}
            d="M56 76a20 20 0 0 1 20-20 28 28 0 0 1 52 8 16 16 0 0 1 0 32H72a16 16 0 0 1-16-20z"
          />
          <path className={cx(line, styles.dashed)} d="M112 96l48 40" />
          <rect className={styles.tint2} x="152" y="120" width="48" height="32" />
          <path className={line} d="M152 152v-32h48v32M144 152h64" />
          <path className={styles.accent} stroke={accent} fill="none" d="M24 160h192" />
        </>
      )}
    </Frame>
  );
}

/** ILL-07 · AI empty or AI switched off: a window with a sparkle (Iris). */
export function IllAiWindow(props: IllustrationProps) {
  return (
    <Frame id="ILL-07" accent="iris" {...props}>
      {(accent) => (
        <>
          <rect className={styles.tint3} x="72" y="40" width="96" height="104" />
          <path
            className={line}
            d="M72 144V72a48 32 0 0 1 96 0v72zM120 48v96M72 104h96M56 144h128"
          />
          <path
            className={styles.accent}
            stroke={accent}
            fill="none"
            d="M184 40v16M176 48h16M176 72l4 4M196 36l-4 4"
          />
        </>
      )}
    </Frame>
  );
}
