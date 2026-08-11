import { describe, expect, it } from 'vitest'

import { formatDownloadedBytes, shortenHash } from './updateApi'

describe('formatDownloadedBytes', () => {
  it('reads as KB below a megabyte and MB above it', () => {
    expect(formatDownloadedBytes(0)).toBe('0 MB')
    expect(formatDownloadedBytes(-1)).toBe('0 MB')
    expect(formatDownloadedBytes(2048)).toBe('2 KB')
    expect(formatDownloadedBytes(1024 * 1024)).toBe('1.0 MB')
    expect(formatDownloadedBytes(62_914_560)).toBe('60.0 MB')
  })
})

describe('shortenHash', () => {
  it('keeps both ends so a hash can still be checked against the release page by eye', () => {
    const hash = 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855'
    const short = shortenHash(hash)

    expect(short.startsWith('e3b0c44298')).toBe(true)
    expect(short.endsWith('91b7852b855'.slice(-10))).toBe(true)
    expect(short.length).toBeLessThan(hash.length)
  })

  it('leaves a short value alone rather than mangling it', () => {
    expect(shortenHash('abc123')).toBe('abc123')
  })
})
