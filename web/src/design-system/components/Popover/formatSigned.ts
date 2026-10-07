const MINUS = String.fromCharCode(0x2212);

/**
 * Signed factor value for explain-why rows: «+1.240,00» / «−320,50», with a true minus (U+2212,
 * Part 4 §8.2). The caller passes its own text when the unit matters (money, percent).
 */
export function formatSigned(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, {
    signDisplay: 'exceptZero',
    minimumFractionDigits: 0,
    maximumFractionDigits: 2,
  })
    .format(value)
    .replace('-', MINUS);
}
