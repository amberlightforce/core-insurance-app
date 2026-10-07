import { DataTable, type DataTableProps } from '../../design-system';

export type SimpleTableProps<Row> = Pick<
  DataTableProps<Row>,
  | 'aria-label'
  | 'columns'
  | 'data'
  | 'getRowId'
  | 'getRowLabel'
  | 'footerTotal'
  | 'emptyState'
  | 'onOpen'
>;

/** A short, fixed-shape DataTable (charge lines, invoice items, journal lines): no search, density or column chrome. */
export function SimpleTable<Row>(props: SimpleTableProps<Row>) {
  return (
    <DataTable<Row>
      {...props}
      searchable={false}
      showDensityToggle={false}
      showColumnSettings={false}
    />
  );
}
