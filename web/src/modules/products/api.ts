import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { CatalogueGetResponse, ChargeTypeListPage, ProductListPage } from '../../api/types';

const pfc = '/api/pfc/v1';

/** pfc.Product.list: the products of the caller legal entity with every version. */
export function useProducts() {
  return useQuery({
    queryKey: ['pfc', 'products'],
    queryFn: async ({ signal }) => {
      // Keyset paging (nextCursor): the catalogue screen shows every product, so follow the cursor to the end.
      const items: NonNullable<ProductListPage['items']> = [];
      let cursor: string | null | undefined;
      let last: ProductListPage | undefined;
      do {
        last = await apiRequest<ProductListPage>(`${pfc}/products`, {
          signal,
          query: { limit: 200, ...(cursor ? { cursor } : {}) },
        });
        items.push(...(last.items ?? []));
        cursor = last.nextCursor;
      } while (cursor);
      return { ...last, items };
    },
  });
}

/** pfc.Catalogue.get: the coverages (with terms) of one version artefact. */
export function useCoverages(hash: string | undefined) {
  return useQuery({
    queryKey: ['pfc', 'catalogue', hash],
    enabled: hash !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<CatalogueGetResponse>(`${pfc}/catalogue/${String(hash)}`, {
        signal,
        query: { scope: 'coverages' },
      }),
  });
}

/** pfc.ChargeType.list: the charge types (the rating output grain) of one version artefact. */
export function useChargeTypes(hash: string | undefined) {
  return useQuery({
    queryKey: ['pfc', 'charge-types', hash],
    enabled: hash !== undefined,
    queryFn: ({ signal }) =>
      apiRequest<ChargeTypeListPage>(`${pfc}/charge-types`, {
        signal,
        query: { hash: String(hash), limit: 200 },
      }),
  });
}
