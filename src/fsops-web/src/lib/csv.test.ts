import { describe, expect, it } from 'vitest'

import { toCsv } from '@/lib/csv'

interface Row {
  name: string
  sectors: number
  onTimePercent: number | null
}

describe('toCsv', () => {
  it('renders a header row followed by one row per record, in column order', () => {
    const rows: Row[] = [
      { name: 'G-TEST', sectors: 4, onTimePercent: 87.5 },
      { name: 'G-ABCD', sectors: 2, onTimePercent: 100 },
    ]
    const csv = toCsv(rows, [
      { key: 'name', header: 'Registration' },
      { key: 'sectors', header: 'Sectors' },
      { key: 'onTimePercent', header: 'On time %' },
    ])

    expect(csv).toBe('Registration,Sectors,On time %\r\nG-TEST,4,87.5\r\nG-ABCD,2,100')
  })

  it('renders null/undefined as an empty field rather than the literal word "null"', () => {
    const rows: Row[] = [{ name: 'G-TEST', sectors: 1, onTimePercent: null }]
    const csv = toCsv(rows, [
      { key: 'name', header: 'Registration' },
      { key: 'onTimePercent', header: 'On time %' },
    ])

    expect(csv).toBe('Registration,On time %\r\nG-TEST,')
  })

  it('quotes a field containing a comma and doubles any interior quote', () => {
    const rows = [{ name: 'Bristol, "The" Airport', sectors: 1, onTimePercent: 100 }]
    const csv = toCsv(rows, [
      { key: 'name', header: 'Name' },
      { key: 'sectors', header: 'Sectors' },
    ])

    expect(csv).toBe('Name,Sectors\r\n"Bristol, ""The"" Airport",1')
  })

  it('quotes a field containing a newline', () => {
    const rows = [{ name: 'Line one\nLine two', sectors: 1, onTimePercent: 100 }]
    const csv = toCsv(rows, [{ key: 'name', header: 'Name' }])

    expect(csv).toBe('Name\r\n"Line one\nLine two"')
  })

  it('returns only the header line for an empty dataset', () => {
    const csv = toCsv([] as Row[], [{ key: 'name', header: 'Registration' }])
    expect(csv).toBe('Registration')
  })
})
