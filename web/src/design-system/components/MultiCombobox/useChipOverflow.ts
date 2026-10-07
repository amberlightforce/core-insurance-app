import { useEffect, useState, type RefObject } from 'react';

interface Measurement {
  signature: string;
  width: number;
  /** Chips that fit in `maxLines` (leaving room for «+N»), or null when all fit. */
  count: number | null;
}

/** How many chips fit in `maxLines` lines, from the rendered rows' vertical offsets. */
function fittingCount(container: HTMLElement, maxLines: number): number | null {
  const rows = Array.from(container.querySelectorAll<HTMLElement>('[role="row"]'));
  const tops = [...new Set(rows.map((row) => row.offsetTop))].sort((a, b) => a - b);
  const cutoff = tops[maxLines];
  if (cutoff === undefined) return null;
  const firstHidden = rows.findIndex((row) => row.offsetTop >= cutoff);
  // Leave room on the last line for the «+N» chip.
  return Math.max(1, firstHidden - 1);
}

/**
 * Chips wrap to at most `maxLines` lines, then collapse into «+N» (Part 2 §4.4). Measures with a
 * ResizeObserver while every chip is rendered; re-measures when the selection (`signature`) or the width
 * changes. Returns the number of chips to show, or null for all.
 */
export function useChipOverflow(
  containerRef: RefObject<HTMLElement | null>,
  signature: string,
  maxLines = 3,
): number | null {
  const [measured, setMeasured] = useState<Measurement>({ signature: '', width: 0, count: null });

  useEffect(() => {
    const element = containerRef.current;
    if (!element || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(() => {
      const width = element.clientWidth;
      const count = fittingCount(element, maxLines);
      setMeasured((previous) => {
        const sameSelection = previous.signature === signature;
        if (sameSelection && previous.count !== null) {
          // Collapsed: only a width change makes us show everything again and re-measure.
          return previous.width === width ? previous : { signature, width, count: null };
        }
        return { signature, width, count };
      });
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, [containerRef, signature, maxLines]);

  return measured.signature === signature ? measured.count : null;
}
