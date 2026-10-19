import { formatMoney } from '../../format';
import type { RegionFormat } from '../../format/numbers';
import type { ContractLayer } from './api';
import { useFormat } from '../staff/useFormat';

/** «500.000 xs 250.000» — the market notation for a limit excess of an attachment (no currency symbol). */
export function layerSummary(layer: ContractLayer, region: RegionFormat): string {
  const plain = (amount: string) =>
    formatMoney(amount, { currency: layer.limit.currency, region, showCurrency: false });
  return `${plain(layer.limit.amount)} xs ${plain(layer.attachment.amount)}`;
}

export function useLayerSummary() {
  const { region } = useFormat();
  return (layer: ContractLayer) => layerSummary(layer, region);
}
