import { Link } from 'react-aria-components';

import { cx } from '../../utils/cx';
import styles from './Comments.module.css';
import { segments, type MentionValue } from './mentionModel';

export interface CommentBodyProps {
  body: MentionValue;
  /** Profile link for a mentioned person; mentions are links when given (E.2), brand chips otherwise. */
  mentionHref?: (personId: string) => string;
}

/** Renders comment text with @mentions as brand chips. */
export function CommentBody({ body, mentionHref }: CommentBodyProps) {
  return (
    <p className={styles.body}>
      {segments(body).map((segment, index) =>
        segment.type === 'text' ? (
          <span key={index}>{segment.text}</span>
        ) : mentionHref ? (
          <Link key={index} className={cx(styles.mention)} href={mentionHref(segment.mention.id)}>
            @{segment.mention.name}
          </Link>
        ) : (
          <span key={index} className={styles.mention}>
            @{segment.mention.name}
          </span>
        ),
      )}
    </p>
  );
}
