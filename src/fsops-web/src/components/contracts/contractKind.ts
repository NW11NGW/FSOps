import type { ComponentType } from 'react'
import type { LucideProps } from 'lucide-react'
import { Package, Users, Waypoints } from 'lucide-react'

import type { Contract, ContractKind } from '@/types/contract'

/**
 * What makes each kind of contract read as its own thing.
 *
 * <p><b>Variety of flying is the entire reason this feature exists</b> - not the money, and not a way
 * to fly aircraft the player does not own. So the three kinds must not be one job type wearing three
 * labels. Each gets its own icon, its own word, its own colour, and - the part that actually does the
 * work - <b>its own headline fact</b>: a ferry leads with how many legs and how far, cargo leads with
 * what is in the hold, a charter leads with who is on board. Two jobs of identical length and fee
 * should still look like different work.</p>
 *
 * <p>Colours come from the categorical <c>chart-*</c> tokens rather than success/warning/danger.
 * Those three mean something in this app - good, careful, bad - and a cargo job is none of them.
 * Class names are written out in full because Tailwind only sees literal strings.</p>
 */
export interface ContractKindStyle {
  label: string
  icon: ComponentType<LucideProps>
  /** One line explaining what this kind of job actually is, for the board's own legend. */
  blurb: string
  /** Icon/text colour. */
  text: string
  /** Tinted chip background for the badge. */
  chip: string
  /** The card's left edge, which is what makes three kinds scannable down a column. */
  stripe: string
}

export const CONTRACT_KIND_STYLES: Record<ContractKind, ContractKindStyle> = {
  Ferry: {
    label: 'Ferry',
    icon: Waypoints,
    blurb:
      'Move an operator’s aircraft to a distant airfield. Several legs over several sessions, and the aeroplane waits where you left it.',
    text: 'text-chart-5',
    chip: 'bg-chart-5/15 text-chart-5',
    stripe: 'border-l-chart-5',
  },
  Cargo: {
    label: 'Cargo',
    icon: Package,
    blurb: 'Freight between two points. What is in the hold decides which airframe can take it.',
    text: 'text-chart-4',
    chip: 'bg-chart-4/15 text-chart-4',
    stripe: 'border-l-chart-4',
  },
  Charter: {
    label: 'Charter',
    icon: Users,
    blurb: 'A one-off passenger job, often somewhere your own network does not reach.',
    text: 'text-chart-3',
    chip: 'bg-chart-3/15 text-chart-3',
    stripe: 'border-l-chart-3',
  },
}

export function kindStyle(kind: ContractKind): ContractKindStyle {
  // Falls back rather than throwing: a kind added to the backend enum should render as a plain job,
  // not blank the page.
  return CONTRACT_KIND_STYLES[kind] ?? CONTRACT_KIND_STYLES.Cargo
}

/**
 * The one fact this kind of job leads with, beneath the route.
 *
 * A ferry has no payload at all - the aeroplane IS the cargo - so showing it "0 kg" would be both
 * true and useless. The server already writes a sentence for every kind (`loadDescription`); this
 * picks the short label that sits beside it.
 */
export function kindHeadline(contract: Contract): string {
  switch (contract.kind) {
    case 'Cargo':
      return 'In the hold'
    case 'Charter':
      return 'On board'
    default:
      return 'The job'
  }
}

/**
 * Where a job sits on the board's range of sizes, in plain words.
 *
 * <p><b>This is presentation, not economics.</b> Nothing on the server defines "a big job" - the fee
 * follows distance, legs, aircraft and payload, and these bands exist purely so a forty-minute hop
 * and an eleven-leg ocean crossing do not read as the same row in a table. The numbers themselves are
 * always shown alongside, so the word never has to be trusted on its own.</p>
 *
 * <p>Banded on <b>legs first, then block time</b>, because leg count is what makes a job span
 * sessions - the thing the player actually feels - while a single long-haul sector is still one
 * evening's flying.</p>
 */
export type ContractScale = 'Short hop' | 'Day’s work' | 'Multi-day' | 'Expedition'

export function contractScale(contract: Contract): ContractScale {
  if (contract.legCount >= 8) return 'Expedition'
  if (contract.legCount >= 4) return 'Multi-day'
  if (contract.legCount >= 2 || contract.totalPlannedBlockMinutes >= 180) return 'Day’s work'
  return 'Short hop'
}

/** True for jobs big enough to deserve the heavier card treatment - the chain shown in full rather
 *  than summarised, because for these the chain IS the offer. */
export function isExpedition(contract: Contract): boolean {
  return contract.legCount >= 4
}

/** Where the whole job starts and ends. Derived from the server's own leg ordering, never re-sorted:
 *  `legs` arrives in sequence order and `legCount` is authoritative. */
export function contractEndpoints(contract: Contract): { from: string; to: string } | null {
  const legs = contract.legs
  if (!legs || legs.length === 0) return null
  const first = legs[0]
  const last = legs[legs.length - 1]
  if (!first || !last) return null
  return { from: first.departureIcao, to: last.arrivalIcao }
}
