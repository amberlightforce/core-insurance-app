const NARROW_NBSP = String.fromCharCode(0x202f);

/**
 * Percent for display (Part 4 §8.2): el-GR puts a narrow no-break space (U+202F) before «%» («62 %»),
 * en-GB has none («62%»). `fraction` is 0–1.
 */
export function formatPercent(fraction: number, locale: string): string {
  const text = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 0 }).format(
    fraction,
  );
  if (!locale.startsWith('el')) return text;
  return text.replace(/\s?%/u, `${NARROW_NBSP}%`);
}
