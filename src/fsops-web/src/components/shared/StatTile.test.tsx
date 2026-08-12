import { Gauge } from 'lucide-react'
import { describe, expect, it } from 'vitest'

import { StatTile } from './StatTile'
import { mount, text } from '@/test/domHarness'

describe('StatTile', () => {
  it('shows its label and value', async () => {
    const { container, unmount } = await mount(<StatTile label="Peak G" icon={Gauge} value="1.24g" />)

    expect(text(container)).toContain('Peak G')
    expect(text(container)).toContain('1.24g')

    unmount()
  })

  it('shows a placeholder rather than the word "undefined" when the value has not arrived', async () => {
    // Tiles are routinely rendered before their figure resolves - a bare `value` of undefined
    // stringifies to "undefined" in the wrong hands, and the tile is a headline number.
    const { container, unmount } = await mount(<StatTile label="Block fuel" icon={Gauge} />)

    expect(text(container)).toContain('Block fuel')
    expect(text(container)).not.toContain('undefined')
    expect(text(container)).not.toContain('NaN')

    unmount()
  })

  it('withholds a stale value while loading, rather than showing a figure that may be wrong', async () => {
    const { container, unmount } = await mount(
      <StatTile label="Expected revenue" icon={Gauge} value="$18,400.00" loading />,
    )

    expect(text(container)).not.toContain('$18,400.00')

    unmount()
  })

  it('shows a trend alongside the value when one is given', async () => {
    const { container, unmount } = await mount(
      <StatTile label="Cash" icon={Gauge} value="$1,200,000.00" trend={{ direction: 'up', label: '+4.2% in 30 days' }} />,
    )

    expect(text(container)).toContain('+4.2% in 30 days')

    unmount()
  })

  it('withholds the trend while loading too, since a trend without its value says nothing', async () => {
    const { container, unmount } = await mount(
      <StatTile label="Cash" icon={Gauge} value="$1,200,000.00" trend={{ direction: 'down', label: '-8% in 30 days' }} loading />,
    )

    expect(text(container)).not.toContain('-8% in 30 days')

    unmount()
  })
})
