import { Download } from 'lucide-react'

import { Button } from '@/components/ui/button'
import { downloadCsv, toCsv, type CsvColumn } from '@/lib/csv'

interface ExportCsvButtonProps<T extends object> {
  filename: string
  rows: T[]
  columns: CsvColumn<T>[]
}

/** "CSV export for the underlying data" - one small, reused control rather than a bespoke export
 *  path per table, so every section's export behaves identically. Disabled (not hidden) when there
 *  is nothing to export, so the control's own presence never implies there might be. */
export function ExportCsvButton<T extends object>({ filename, rows, columns }: ExportCsvButtonProps<T>) {
  return (
    <Button
      type="button"
      variant="outline"
      size="sm"
      disabled={rows.length === 0}
      onClick={() => downloadCsv(filename, toCsv(rows, columns))}
    >
      <Download className="size-4" />
      Export CSV
    </Button>
  )
}
