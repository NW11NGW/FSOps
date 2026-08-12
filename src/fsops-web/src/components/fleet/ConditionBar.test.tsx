import { describe, expect, it } from 'vitest'

import { ConditionBar } from './ConditionBar'
import { mount, text } from '@/test/domHarness'

describe('ConditionBar', () => {
  it('shows the condition as a whole percentage', async () => {
    const { container, unmount } = await mount(<ConditionBar percent={82.4} />)

    expect(text(container)).toBe('82%')

    unmount()
  })

  it('rounds rather than truncating, so 99.6% does not read as 99%', async () => {
    const { container, unmount } = await mount(<ConditionBar percent={99.6} />)

    expect(text(container)).toBe('100%')

    unmount()
  })

  it('clamps a figure above 100 rather than rendering it', async () => {
    const { container, unmount } = await mount(<ConditionBar percent={118} />)

    expect(text(container)).toBe('100%')

    unmount()
  })

  it('clamps a negative figure to zero rather than rendering a negative condition', async () => {
    const { container, unmount } = await mount(<ConditionBar percent={-12} />)

    expect(text(container)).toBe('0%')

    unmount()
  })
})
