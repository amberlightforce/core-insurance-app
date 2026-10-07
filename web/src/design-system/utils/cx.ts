/** Joins class names, skipping falsy entries (CSS Module lookups are `string | undefined` under strict TS). */
export function cx(...classes: (string | false | null | undefined)[]): string {
  return classes.filter(Boolean).join(' ');
}

type Defined<T> = { [K in keyof T]?: Exclude<T[K], undefined> };

/**
 * Drops `undefined` values so optional props can be forwarded under `exactOptionalPropertyTypes`:
 * `<AriaThing {...defined({ onPress, placement })} />`.
 */
export function defined<T extends object>(props: T): Defined<T> {
  const out: Record<string, unknown> = {};
  for (const [key, value] of Object.entries(props)) {
    if (value !== undefined) out[key] = value;
  }
  return out as Defined<T>;
}
