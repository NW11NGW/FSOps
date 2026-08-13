import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { readVatsimAtcVisible, writeVatsimAtcVisible } from './vatsimAtcVisibility'

const STORAGE_KEY = 'fsops-liveops-atc-visible'
const TRAFFIC_STORAGE_KEY = 'fsops-liveops-traffic-visible'

beforeEach(() => {
  window.localStorage.clear()
})

afterEach(() => {
  window.localStorage.clear()
})

describe('vatsimAtcVisibility - default is off', () => {
  it('is hidden with no stored preference at all - a brand-new user', () => {
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()
    expect(readVatsimAtcVisible()).toBe(false)
  })

  it('is hidden if the stored value is malformed rather than a literal "true"', () => {
    window.localStorage.setItem(STORAGE_KEY, 'yes')
    expect(readVatsimAtcVisible()).toBe(false)

    window.localStorage.setItem(STORAGE_KEY, '1')
    expect(readVatsimAtcVisible()).toBe(false)

    window.localStorage.setItem(STORAGE_KEY, '')
    expect(readVatsimAtcVisible()).toBe(false)
  })

  it('falls back to hidden if localStorage throws (a locked-down embed)', () => {
    const original = window.localStorage.getItem
    window.localStorage.getItem = () => {
      throw new Error('denied')
    }
    try {
      expect(readVatsimAtcVisible()).toBe(false)
    } finally {
      window.localStorage.getItem = original
    }
  })
})

describe('vatsimAtcVisibility - persisting an explicit choice', () => {
  it('turning it on writes an explicit true, and reads back as visible', () => {
    writeVatsimAtcVisible(true)
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')
    expect(readVatsimAtcVisible()).toBe(true)
  })

  it('survives a reload - a fresh read sees the stored choice with no writer in between', () => {
    writeVatsimAtcVisible(true)
    // Nothing re-writes the key here; this is exactly what a page reload does - construct the
    // initial state straight from storage.
    expect(readVatsimAtcVisible()).toBe(true)
  })

  it('turning it back off removes the key rather than storing a literal false', () => {
    writeVatsimAtcVisible(true)
    writeVatsimAtcVisible(false)
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()
    expect(readVatsimAtcVisible()).toBe(false)
  })

  it('a write failure is swallowed rather than breaking the dashboard', () => {
    const original = window.localStorage.setItem
    window.localStorage.setItem = () => {
      throw new Error('denied')
    }
    try {
      expect(() => writeVatsimAtcVisible(true)).not.toThrow()
    } finally {
      window.localStorage.setItem = original
    }
  })
})

describe('vatsimAtcVisibility - independent of the traffic toggle', () => {
  it('showing traffic does not also show ATC', () => {
    window.localStorage.setItem(TRAFFIC_STORAGE_KEY, 'true')
    expect(readVatsimAtcVisible()).toBe(false)
  })

  it('showing ATC does not disturb the traffic key', () => {
    writeVatsimAtcVisible(true)
    expect(window.localStorage.getItem(TRAFFIC_STORAGE_KEY)).toBeNull()
  })
})
