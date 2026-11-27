import { useQuery } from '@tanstack/react-query';

import { apiRequest } from '../../api/client';
import type { CatalogueGetResponse, ChargeTypeListPage, ProductListPage } from '../../api/types';

const pfc = '/api/pfc/v1';

/** pfc.Product.list: the products of the caller legal entity with every version. */
export function useProducts() {
  return useQuery({
    queryKey: ['pfc', 'products'],
    queryFn: ({ signal }) => apiRequest<ProductListPage>(`${pfc}/products`, { signal }),
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
