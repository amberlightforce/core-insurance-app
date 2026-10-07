/** Line and area paths for a sparkline in a `0 0 100 height` viewBox, with 2 px vertical padding. */
export function sparklinePaths(
  values: number[],
  height: number,
): { line: string; area: string; endY: number } | null {
  if (values.length < 2) return null;
  const min = Math.min(...values);
  const max = Math.max(...values);
  const pad = 2;
  const span = max - min || 1;
  const points = values.map((v, i) => {
    const x = (i / (values.length - 1)) * 100;
    const y = max === min ? height / 2 : pad + (1 - (v - min) / span) * (height - pad * 2);
    return [x, y] as const;
  });
  const line = points
    .map(([x, y], i) => `${i === 0 ? 'M' : 'L'}${x.toFixed(2)} ${y.toFixed(2)}`)
    .join(' ');
  const area = `${line} L100 ${String(height)} L0 ${String(height)} Z`;
  const last = points[points.length - 1];
  return { line, area, endY: last ? last[1] : height / 2 };
}

/** Whether a trend is favourable for a KPI whose favourable direction is `higherIsBetter`. */
export function trendTone(
  values: number[],
  higherIsBetter: boolean,
): 'positive' | 'adverse' | 'neutral' {
  const first = values[0];
  const last = values[values.length - 1];
  if (first === undefined || last === undefined || first === last) return 'neutral';
  return last > first === higherIsBetter ? 'positive' : 'adverse';
}
