import type { LucideIcon, LucideProps } from 'lucide-react';

import { strokeForSize, type IconSize } from './stroke';

export interface IconProps extends Omit<LucideProps, 'size' | 'strokeWidth' | 'ref'> {
  icon: LucideIcon;
  size?: IconSize;
  /**
   * Accessible name for a meaningful standalone icon. Omit for decorative icons (the default), which are
   * hidden from assistive technology.
   */
  label?: string;
}

/** Lucide icon with the design system's size and stroke rules (Part 3 §7.1). Colour is `currentColor`. */
export function Icon({ icon: Glyph, size = 16, label, ...rest }: IconProps) {
  const a11y = label
    ? ({ role: 'img', 'aria-label': label } as const)
    : ({ 'aria-hidden': true, focusable: false } as const);
  return (
    <Glyph size={size} strokeWidth={strokeForSize(size)} nonScalingStroke {...a11y} {...rest} />
  );
}
