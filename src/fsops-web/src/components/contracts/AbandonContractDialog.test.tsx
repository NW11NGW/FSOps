import { describe, expect, it, vi } from 'vitest'

import { AbandonContractDialog } from './AbandonContractDialog'
import { SettingsProvider } from '@/hooks/SettingsProvider'
import { click, findButton, flush, mount, text } from '@/test/domHarness'
import { settingsResponseFor } from '@/test/settingsStub'
import type { Contract } from '@/types/contract'

vi.mock('@/lib/api', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/lib/api')>()
  return { ...actual, get: vi.fn(), post: vi.fn(), put: vi.fn(), del: vi.fn() }
})

import { get } from '@/lib/api'

function stubSettings() {
  vi.mocked(get).mockImplementation(async (path: string) => {
    const settings = settingsResponseFor(path)
    if (settings) return settings as never
    throw new Error(`Unexpected GET ${path}`)
  })
}

function contract(overrides: Partial<Contract> = {}): Contract {
  return {
    id: 'contract-1',
    kind: 'Ferry',
    status: 'Accepted',
    operatorName: 'Northgate Freight',
    aircraft: {
      typeDesignator: 'C172',
      name: 'Cessna 172 Skyhawk',
      manufacturer: 'Cessna',
      category: 'LightSingle',
      rangeNm: 544,
      cruiseTasKts: 120,
      seats: 3,
    },
    loadDescription: 'Positioning flight - Cessna 172 Skyhawk, empty',
    payloadKg: 0,
    paxCount: 0,
    fee: 20000,
    completionBonus: 5000,
    totalIfCompleted: 25000,
    totalDistanceNm: 2400,
    totalPlannedBlockMinutes: 1200,
    legCount: 5,
    flownLegCount: 3,
    earnedSoFar: 12000,
    outstandingFee: 8000,
    // Deliberately NOT equal to outstandingFee: the dialog must render the server's quote, not a
    // local multiplication of the outstanding balance.
    abandonCharge: 6400,
    abandonReason: '2 legs left unflown - the operator has to recover the aircraft from where you stopped.',
    offeredUtc: '2026-08-01T00:00:00Z',
    deadlineUtc: '2026-09-01T00:00:00Z',
    acceptedUtc: '2026-08-02T00:00:00Z',
    closedUtc: null,
    closedReason: null,
    nextLeg: { id: 'leg-4', sequence: 4, departureIcao: 'BGGH', arrivalIcao: 'CYFB' },
    legs: null,
    ...overrides,
  }
}

describe('AbandonContractDialog', () => {
  it('shows the server’s charge and its sentence verbatim, not a locally recomputed figure', async () => {
    stubSettings()
    const job = contract()

    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={job} onOpenChange={vi.fn()} onConfirm={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)

    // The server's own words for why there is a charge at all.
    expect(body).toContain('the operator has to recover the aircraft from where you stopped')
    // The quoted charge, which is NOT the outstanding fee. If the dialog ever went back to
    // multiplying outstandingFee itself it would show $8,000.00 here and this would fail.
    expect(body).toContain('$6,400.00')
    expect(findButton(document.body, 'Hand back').textContent).toContain('$6,400.00')

    mounted.unmount()
  })

  /**
   * The bonus is forfeited, not billed. Both halves need saying: losing it is the real cost of
   * walking away from a long chain, but it is not part of the charge and must not read as though it
   * were - a player adding the two together would think handing back cost far more than it does.
   */
  it('says the completion bonus is given up, and that it is not added to the charge', async () => {
    stubSettings()
    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={contract()} onOpenChange={vi.fn()} onConfirm={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('$5,000.00')
    expect(body).toContain('not added to the charge')
    // The charge on the button is still the abandon charge alone - the bonus is nowhere in it.
    expect(findButton(document.body, 'Hand back').textContent).toContain('$6,400.00')

    mounted.unmount()
  })

  it('says nothing about a bonus when the job has none', async () => {
    stubSettings()
    const noBonus = contract({ completionBonus: 0, totalIfCompleted: 20000 })
    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={noBonus} onOpenChange={vi.fn()} onConfirm={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    expect(text(document.body)).not.toContain('bonus')

    mounted.unmount()
  })

  it('says a free hand-back is free, and does not show a charge', async () => {
    stubSettings()
    // No leg flown: the aeroplane never moved, so handing the job back costs nothing.
    const job = contract({
      flownLegCount: 0,
      earnedSoFar: 0,
      outstandingFee: 20000,
      abandonCharge: 0,
      abandonReason:
        'No leg was flown, so the aircraft is still where its operator left it - handing the job back costs nothing.',
    })

    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={job} onOpenChange={vi.fn()} onConfirm={vi.fn()} />
      </SettingsProvider>,
    )
    await flush()

    const body = text(document.body)
    expect(body).toContain('handing the job back costs nothing')
    expect(body).toContain('Free')
    expect(findButton(document.body, 'Hand back').textContent).toContain('free')

    mounted.unmount()
  })

  /**
   * The one that matters most. Abandoning spends real money, so a click that fires the request twice
   * would charge twice - and this app has previously answered that risk by adding confirmation steps
   * rather than by making one click mean one thing.
   */
  it('fires exactly one request for one click, and stays disabled while it is in flight', async () => {
    stubSettings()
    const job = contract()

    let resolveConfirm: (() => void) | undefined
    const onConfirm = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveConfirm = resolve
        }),
    )

    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={job} onOpenChange={vi.fn()} onConfirm={onConfirm} />
      </SettingsProvider>,
    )
    await flush()

    const button = findButton(document.body, 'Hand back')
    click(button)
    await flush()

    expect(onConfirm).toHaveBeenCalledTimes(1)
    expect(onConfirm).toHaveBeenCalledWith('contract-1')

    // While the request is outstanding the control is disabled, so an impatient second click cannot
    // raise a second charge.
    const inFlight = findButton(document.body, 'Handing back')
    expect(inFlight.hasAttribute('disabled')).toBe(true)
    click(inFlight)
    await flush()
    expect(onConfirm).toHaveBeenCalledTimes(1)

    resolveConfirm?.()
    await flush()
    mounted.unmount()
  })

  it('shows the server’s refusal rather than a generic failure, and lets the player try again', async () => {
    stubSettings()
    const job = contract()
    const onConfirm = vi
      .fn()
      .mockRejectedValueOnce(
        new (await import('@/lib/api')).ApiError(
          400,
          'A leg of this contract is still in progress. Finish or abandon that flight before handing the job back.',
        ),
      )

    const mounted = await mount(
      <SettingsProvider>
        <AbandonContractDialog contract={job} onOpenChange={vi.fn()} onConfirm={onConfirm} />
      </SettingsProvider>,
    )
    await flush()

    click(findButton(document.body, 'Hand back'))
    await flush()

    expect(text(document.body)).toContain('A leg of this contract is still in progress')
    // Re-enabled, not stuck: a refusal the player can act on has to leave them able to act.
    expect(findButton(document.body, 'Hand back').hasAttribute('disabled')).toBe(false)

    mounted.unmount()
  })
})
