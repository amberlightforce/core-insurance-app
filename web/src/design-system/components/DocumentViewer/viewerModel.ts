/** Zoom and rotation rules for the document viewer (Part 2 §4.31). */

export const ZOOM_STEPS = [25, 50, 75, 100, 125, 150, 200, 300, 400] as const;
export const MIN_ZOOM = ZOOM_STEPS[0];
export const MAX_ZOOM = ZOOM_STEPS[ZOOM_STEPS.length - 1] ?? 400;

export type FitMode = 'custom' | 'fit-width' | 'fit-page';
export type Rotation = 0 | 90 | 180 | 270;

export function zoomIn(current: number): number {
  return ZOOM_STEPS.find((step) => step > current) ?? MAX_ZOOM;
}

export function zoomOut(current: number): number {
  return [...ZOOM_STEPS].reverse().find((step) => step < current) ?? MIN_ZOOM;
}

export function rotate(current: Rotation): Rotation {
  return ((current + 90) % 360) as Rotation;
}

export function clampPage(page: number, pageCount: number): number {
  return Math.min(Math.max(1, Math.round(page)), Math.max(1, pageCount));
}
