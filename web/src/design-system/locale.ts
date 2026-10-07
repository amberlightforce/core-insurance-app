/**
 * React Aria's locale drives its built-in strings (calendar labels, "Clear", …) and its date segments,
 * so it follows the UI language: el → el-GR, en → en-GB (both dd/mm/yyyy, 24-hour, Monday first).
 * Money, numbers and dates that we format ourselves follow the region-format preference (src/format).
 */
export function ariaLocaleFor(language: string): string {
  return language.startsWith('en') ? 'en-GB' : 'el-GR';
}
