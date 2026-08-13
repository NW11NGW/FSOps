/** One CSV column: `key` reads the raw value off each row, `header` is the printed column title. */
export interface CsvColumn<T> {
  key: keyof T
  header: string
}

/** Quotes a single CSV field per RFC 4180: wrapped in double quotes whenever it contains a comma,
 *  quote, or newline, with any interior quote doubled. null/undefined become an empty field rather
 *  than the literal string "null" - the underlying figure is genuinely absent (e.g. "not measured"),
 *  not zero, and a spreadsheet should see a blank cell for it. */
function escapeCsvField(value: unknown): string {
  if (value === null || value === undefined) return ''
  const text = String(value)
  if (/[",\n\r]/.test(text)) {
    return `"${text.replace(/"/g, '""')}"`
  }
  return text
}

/** Converts rows of underlying data into an RFC 4180 CSV string (header row plus one row per
 *  record) - the shared implementation behind every "Export CSV" button on the Stats page, so a
 *  table's on-screen columns and its exported ones can never quietly drift apart. Uses CRLF line
 *  endings, the CSV convention most spreadsheet tools expect. */
export function toCsv<T extends object>(rows: T[], columns: CsvColumn<T>[]): string {
  const headerLine = columns.map((c) => escapeCsvField(c.header)).join(',')
  const lines = rows.map((row) => columns.map((c) => escapeCsvField(row[c.key])).join(','))
  return [headerLine, ...lines].join('\r\n')
}

/** Triggers a browser download of `csv` as `filename` - a Blob + object URL, no network call, so
 *  this works with no internet connection at all. */
export function downloadCsv(filename: string, csv: string): void {
  // Prefixed so Excel (and other BOM-sniffing tools) reliably detect UTF-8 rather than guessing an
  // 8-bit codepage and mangling any non-ASCII pilot/aircraft name.
  const blob = new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8;' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  document.body.removeChild(link)
  URL.revokeObjectURL(url)
}
