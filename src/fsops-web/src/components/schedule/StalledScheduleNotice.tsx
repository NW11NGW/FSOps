import { Info } from 'lucide-react'
import { Link } from 'react-router-dom'

import type { PilotSummary } from '@/types/pilot'

interface StalledScheduleNoticeProps {
  pilots: PilotSummary[]
}

/**
 * Tells the player that a standing weekly schedule has stopped producing flights because its
 * aircraft is out of position - the one thing about a rolling pattern that can go wrong silently.
 * Before this, it surfaced only as an advisory if they happened to open the builder and save
 * something, so a schedule could sit dead for a week with the only trace being missed sectors in
 * flight history.
 *
 * Deliberately styled as information rather than an error, and it blocks nothing: the schedule is
 * valid and still repeating, and the only thing wrong is where an airframe is parked. Both ways out
 * are offered because they cost differently - flying it back earns a sector, repositioning charges
 * the standard fee - and which one suits is the player's call.
 */
export function StalledScheduleNotice({ pilots }: StalledScheduleNoticeProps) {
  const affected = pilots.flatMap((pilot) =>
    (pilot.scheduleStalls ?? []).map((stall) => ({ pilot, stall })),
  )

  if (affected.length === 0) return null

  return (
    <div
      className="space-y-3 rounded-md border border-warning/30 bg-warning/10 p-3 text-sm text-warning"
      role="status"
    >
      <p className="flex items-start gap-2 font-medium">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span>
          {affected.length === 1
            ? 'One weekly schedule has stopped flying'
            : `${affected.length} weekly schedules have stopped flying`}
        </span>
      </p>
      <ul className="space-y-2 pl-6">
        {affected.map(({ pilot, stall }) => (
          <li key={`${pilot.id}-${stall.fleetAircraftId}`} className="min-w-0 break-words">
            <span className="font-medium">{pilot.name}</span> — {stall.message}{' '}
            <Link to="/fleet" className="underline underline-offset-2 hover:no-underline">
              Open the Fleet page
            </Link>
          </li>
        ))}
      </ul>
    </div>
  )
}
