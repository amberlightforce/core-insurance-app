import { useInfiniteQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { JournalQueryPage } from '../../api/types';

/** fin.Journal.query by policy number (a business key, not personal data), paged by cursor. Read-only. */
export function useJournals(policyNumber: string) {
  return useInfiniteQuery({
    queryKey: ['fin', 'journals', policyNumber],
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam, signal }) =>
      apiRequest<JournalQueryPage>('/api/fin/v1/journals/query', {
        query: { policyNumber, limit: 50, cursor: pageParam },
        signal,
      }),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  });
}
