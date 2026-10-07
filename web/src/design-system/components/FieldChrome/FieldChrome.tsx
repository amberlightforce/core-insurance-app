import { CircleAlert, Info, TriangleAlert } from 'lucide-react';
import type { ReactNode } from 'react';

import { Icon } from '../../icons';
import { cx } from '../../utils/cx';
import { FieldLabel } from '../TextField/FormField';
import styles from './FieldChrome.module.css';
import type { FieldChromeMessages, FieldChromeMessagesInput } from './useFieldMessages';

export interface FieldChromeLabelProps {
  children: ReactNode;
  isRequired?: boolean | undefined;
  /** Render a plain element with this id instead of a React Aria `Label` (read-only displays). */
  id?: string;
}

/**
 * Field label above the control (Part 2 §4.2): `type.label`, `text.secondary`, wraps to two lines (never an
 * ellipsis). Required: a `*` in `danger.fg` (hidden from screen readers) plus the SR text «υποχρεωτικό».
 * Inside a React Aria field the label is wired to the control automatically.
 */
export function FieldChromeLabel({ children, isRequired, id }: FieldChromeLabelProps) {
  // One label implementation for every field (D-FE: no duplicate chrome): reuse TextField's FieldLabel.
  return (
    <FieldLabel
      {...(id !== undefined ? { id, elementType: 'span' } : {})}
      {...(isRequired ? { isRequired: true } : {})}
    >
      {children}
    </FieldLabel>
  );
}

export interface FieldMessageListProps extends FieldChromeMessagesInput {
  messages: FieldChromeMessages;
}

/**
 * Helper, error, warning, info and disabled-reason lines under a control (`type.caption`). The error has a
 * `circle-alert` icon in `danger.fg`; the warning a `triangle-alert` in `warning.fg`.
 */
export function FieldMessageList({
  messages,
  description,
  errorMessage,
  warning,
  info,
  disabledReason,
}: FieldMessageListProps) {
  return (
    <>
      {messages.showError ? (
        <div id={messages.errorId} className={cx(styles.message, styles.error)}>
          <Icon icon={CircleAlert} size={14} className={styles.messageIcon} />
          <span>{errorMessage}</span>
        </div>
      ) : null}
      {messages.showWarning ? (
        <div id={messages.warningId} className={cx(styles.message, styles.warning)}>
          <Icon icon={TriangleAlert} size={14} className={styles.messageIcon} />
          <span>{warning}</span>
        </div>
      ) : null}
      {messages.showInfo ? (
        <div id={messages.infoId} className={cx(styles.message, styles.info)}>
          <Icon icon={Info} size={14} className={styles.messageIcon} />
          <span>{info}</span>
        </div>
      ) : null}
      {messages.showReason ? (
        <div id={messages.reasonId} className={cx(styles.message, styles.helper)}>
          {disabledReason}
        </div>
      ) : null}
      {messages.showHelper ? (
        <div id={messages.helperId} className={cx(styles.message, styles.helper)}>
          {description}
        </div>
      ) : null}
    </>
  );
}
