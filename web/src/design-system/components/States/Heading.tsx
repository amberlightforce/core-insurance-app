import type { ReactNode } from 'react';

export type HeadingLevel = 1 | 2 | 3 | 4 | 5 | 6;

export interface HeadingProps {
  level: HeadingLevel;
  className?: string | undefined;
  id?: string | undefined;
  children: ReactNode;
}

/** A heading whose level the consumer picks so the state fits the page outline. */
export function Heading({ level, className, id, children }: HeadingProps) {
  const Tag = `h${String(level)}` as 'h1';
  return (
    <Tag className={className} id={id}>
      {children}
    </Tag>
  );
}
