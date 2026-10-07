/** Split pane geometry (Part 1 §3.4 IB-05). */
export const splitDefaults = {
  /** List pane width: 420 compact / 460 comfortable. */
  compactWidth: 420,
  comfortableWidth: 460,
  min: 320,
  max: 640,
  /** Detail pane minimum; below list + this the layout stacks. */
  detailMin: 560,
  step: 16,
  bigStep: 64,
} as const;

export function clampWidth(
  width: number,
  min: number = splitDefaults.min,
  max: number = splitDefaults.max,
): number {
  return Math.min(max, Math.max(min, Math.round(width)));
}

/** Keyboard on the separator: ←/→ 16 px, Shift 64 px, Home/End min/max. Returns null for other keys. */
export function widthForKey(
  key: string,
  shiftKey: boolean,
  width: number,
  min: number = splitDefaults.min,
  max: number = splitDefaults.max,
): number | null {
  const step = shiftKey ? splitDefaults.bigStep : splitDefaults.step;
  switch (key) {
    case 'ArrowLeft':
      return clampWidth(width - step, min, max);
    case 'ArrowRight':
      return clampWidth(width + step, min, max);
    case 'Home':
      return min;
    case 'End':
      return max;
    default:
      return null;
  }
}

/** True when list + detail do not fit side by side. */
export function shouldStack(containerWidth: number, listWidth: number): boolean {
  return containerWidth > 0 && containerWidth < listWidth + splitDefaults.detailMin;
}
