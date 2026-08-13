import { afterEach, beforeEach, describe, expect, it } from 'vitest'

import { readVatsimTrafficVisible, writeVatsimTrafficVisible } from './vatsimTrafficVisibility'

const STORAGE_KEY = 'fsops-liveops-traffic-visible'

beforeEach(() => {
  window.localStorage.clear()
})

afterEach(() => {
  window.localStorage.clear()
})

describe('vatsimTrafficVisibility - default is off', () => {
  it('is hidden with no stored preference at all - a brand-new user', () => {
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()
    expect(readVatsimTrafficVisible()).toBe(false)
  })

  it('is hidden if the stored value is malformed rather than a literal "true"', () => {
    window.localStorage.setItem(STORAGE_KEY, 'yes')
    expect(readVatsimTrafficVisible()).toBe(false)

    window.localStorage.setItem(STORAGE_KEY, '1')
    expect(readVatsimTrafficVisible()).toBe(false)

    window.localStorage.setItem(STORAGE_KEY, '')
    expect(readVatsimTrafficVisible()).toBe(false)
  })

  it('falls back to hidden if localStorage throws (a locked-down embed)', () => {
    const original = window.localStorage.getItem
    window.localStorage.getItem = () => {
      throw new Error('denied')
    }
    try {
      expect(readVatsimTrafficVisible()).toBe(false)
    } finally {
      window.localStorage.getItem = original
    }
  })
})

describe('vatsimTrafficVisibility - persisting an explicit choice', () => {
  it('turning it on writes an explicit true, and reads back as visible', () => {
    writeVatsimTrafficVisible(true)
    expect(window.localStorage.getItem(STORAGE_KEY)).toBe('true')
    expect(readVatsimTrafficVisible()).toBe(true)
  })

  it('turning it back off removes the key rather than storing a literal false', () => {
    writeVatsimTrafficVisible(true)
    writeVatsimTrafficVisible(false)
    expect(window.localStorage.getItem(STORAGE_KEY)).toBeNull()
    expect(readVatsimTrafficVisible()).toBe(false)
  })

  it('a write failure is swallowed rather than breaking the dashboard', () => {
    const original = window.localStorage.setItem
    window.localStorage.setItem = () => {
      throw new Error('denied')
    }
    try {
      expect(() => writeVatsimTrafficVisible(true)).not.toThrow()
    } finally {
      window.localStorage.setItem = original
    }
  })
})
