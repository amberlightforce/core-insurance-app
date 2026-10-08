import { Focusable } from 'react-aria-components';

import { Tooltip } from '../../design-system';

/** A long technical reference (a snapshot ref) shortened in place, the full value in a tooltip and copyable. */
export function TruncatedRef({ value, keep = 12 }: { value: string; keep?: number }) {
  if (value.length <= keep + 1) return <span className="ds-mono">{value}</span>;
  return (
    <Tooltip content={value}>
      <Focusable>
        <span role="note" aria-label={value} className="ds-mono" tabIndex={0}>
          {value.slice(0, keep)}…
        </span>
      </Focusable>
    </Tooltip>
  );
}
