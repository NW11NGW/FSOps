import { useState } from 'react'
import { toast } from 'sonner'
import { CalendarClock, LayoutGrid, Loader2, Plane, Plus, Trash2, Users } from 'lucide-react'

import { PilotScheduleDialog } from '@/components/schedule/PilotScheduleDialog'
import { ScheduleOverview } from '@/components/schedule/ScheduleOverview'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { usePilots } from '@/hooks/usePilots'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'
import type { PilotStatus, PilotSummary } from '@/types/pilot'

const STATUS_BADGE: Record<PilotStatus, { label: string; variant: 'success' | 'warning' | 'muted' }> = {
  Available: { label: 'Available', variant: 'success' },
  Flying: { label: 'Flying', variant: 'warning' },
  Inactive: { label: 'Inactive', variant: 'muted' },
}

/** Hire and release virtual pilots, and open each one's weekly schedule builder - see
 *  docs/PLAN.md "Virtual pilot scheduling". Virtual pilots are what let the airline keep running
 *  while the player is away; this page is where that starts. */
export function Pilots() {
  const pilotsQuery = usePilots()
  const { fmt } = useSettings()

  const [hireOpen, setHireOpen] = useState(false)
  const [hireName, setHireName] = useState('')
  const [hiring, setHiring] = useState(false)
  const [hireError, setHireError] = useState<string | null>(null)

  const [releasing, setReleasing] = useState<PilotSummary | null>(null)
  const [releaseBusy, setReleaseBusy] = useState(false)
  const [releaseError, setReleaseError] = useState<string | null>(null)

  const [scheduling, setScheduling] = useState<PilotSummary | null>(null)

  async function handleHire() {
    setHiring(true)
    setHireError(null)
    try {
      const result = await pilotsQuery.hire(hireName.trim() || undefined)
      toast.success(`${result.pilot.name} hired.`)
      setHireOpen(false)
      setHireName('')
    } catch (err) {
      setHireError(err instanceof ApiError ? err.message : 'Could not hire a pilot. Try again.')
    } finally {
      setHiring(false)
    }
  }

  async function handleRelease() {
    if (!releasing) return
    setReleaseBusy(true)
    setReleaseError(null)
    try {
      await pilotsQuery.release(releasing.id)
      toast.success(`${releasing.name} released.`)
      setReleasing(null)
    } catch (err) {
      setReleaseError(err instanceof ApiError ? err.message : 'Could not release this pilot. Try again.')
    } finally {
      setReleaseBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <PageHeader
        title="Pilots"
        description="Hire virtual pilots to fly a standing weekly schedule while you're away."
        actions={
          <Button onClick={() => setHireOpen(true)}>
            <Plus />
            Hire pilot
          </Button>
        }
      />

      <Tabs defaultValue="roster">
        <TabsList>
          <TabsTrigger value="roster" className="gap-1.5">
            <Users className="size-3.5" />
            Roster
          </TabsTrigger>
          <TabsTrigger value="overview" className="gap-1.5">
            <LayoutGrid className="size-3.5" />
            Schedule overview
          </TabsTrigger>
        </TabsList>

        <TabsContent value="roster">
      <Card>
        <CardContent className="p-0">
          {pilotsQuery.status === 'loading' && (
            <div className="space-y-2 p-6">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          )}

          {pilotsQuery.status === 'error' && (
            <p className="p-6 text-sm text-danger">Could not load your pilots. Check your connection and try again.</p>
          )}

          {pilotsQuery.status === 'ready' && pilotsQuery.pilots.length === 0 && (
            <div className="p-6">
              <EmptyState
                icon={Users}
                title="No pilots yet"
                description="Hire one to fly while you're away - a standing weekly schedule keeps your airline running even when you're not flying it yourself."
                action={
                  <Button onClick={() => setHireOpen(true)}>
                    <Plus />
                    Hire your first pilot
                  </Button>
                }
              />
            </div>
          )}

          {pilotsQuery.status === 'ready' && pilotsQuery.pilots.length > 0 && (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Pilot</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Skill</TableHead>
                  <TableHead>Hours flown</TableHead>
                  <TableHead>Monthly salary</TableHead>
                  <TableHead>Weekly schedule</TableHead>
                  <TableHead className="w-10">
                    <span className="sr-only">Actions</span>
                  </TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {pilotsQuery.pilots.map((pilot) => {
                  const status = STATUS_BADGE[pilot.status]
                  return (
                    <TableRow key={pilot.id}>
                      <TableCell>
                        <div className="flex items-center gap-2">
                          <span className="flex size-8 shrink-0 items-center justify-center rounded-full bg-accent/15 text-accent">
                            <Plane className="size-4" />
                          </span>
                          <span className="min-w-0 break-words font-medium">{pilot.name}</span>
                        </div>
                      </TableCell>
                      <TableCell>
                        <Badge variant={status.variant}>{status.label}</Badge>
                      </TableCell>
                      <TableCell className="tabular-nums">{pilot.skillRating}</TableCell>
                      <TableCell className="tabular-nums">{pilot.hoursFlown.toFixed(1)}h</TableCell>
                      <TableCell className="tabular-nums">{fmt.money(pilot.monthlySalary)}</TableCell>
                      <TableCell className="text-sm text-muted-foreground">
                        {pilot.isPlayer ? (
                          <span>—</span>
                        ) : pilot.sectorsPerWeek != null ? (
                          <span>
                            {pilot.sectorsPerWeek} sector{pilot.sectorsPerWeek === 1 ? '' : 's'}/week
                            {pilot.weeklyEstimatedRevenue != null && ` · ${fmt.money(pilot.weeklyEstimatedRevenue)} revenue`}
                          </span>
                        ) : (
                          <span>Not scheduled</span>
                        )}
                      </TableCell>
                      <TableCell>
                        {pilot.isPlayer ? (
                          <p className="text-right text-xs text-muted-foreground">You fly this one yourself, from the Fly screen.</p>
                        ) : (
                          <div className="flex items-center justify-end gap-1">
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              onClick={() => setScheduling(pilot)}
                            >
                              <CalendarClock className="size-3.5" />
                              Schedule
                            </Button>
                            <Button
                              type="button"
                              variant="ghost"
                              size="icon"
                              className="size-8 text-muted-foreground hover:text-danger"
                              aria-label={`Release ${pilot.name}`}
                              onClick={() => {
                                setReleaseError(null)
                                setReleasing(pilot)
                              }}
                            >
                              <Trash2 className="size-4" />
                            </Button>
                          </div>
                        )}
                      </TableCell>
                    </TableRow>
                  )
                })}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
        </TabsContent>

        <TabsContent value="overview">
          <ScheduleOverview />
        </TabsContent>
      </Tabs>

      <Dialog open={hireOpen} onOpenChange={(open) => !hiring && setHireOpen(open)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Hire a pilot</DialogTitle>
            <DialogDescription>They'll join available and ready to fly a weekly schedule you set up.</DialogDescription>
          </DialogHeader>
          <div className="space-y-1.5">
            <Label htmlFor="pilot-name">Name (optional)</Label>
            <Input
              id="pilot-name"
              value={hireName}
              onChange={(event) => setHireName(event.target.value)}
              placeholder="Leave blank for a generated name"
              maxLength={80}
            />
          </div>
          {hireError && <p className="text-sm text-danger">{hireError}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setHireOpen(false)} disabled={hiring}>
              Cancel
            </Button>
            <Button type="button" onClick={handleHire} disabled={hiring}>
              {hiring ? <Loader2 className="size-4 animate-spin" /> : <Plus className="size-4" />}
              {hiring ? 'Hiring…' : 'Hire pilot'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={releasing !== null} onOpenChange={(open) => !open && setReleasing(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Release {releasing?.name}?</DialogTitle>
            <DialogDescription>
              Their weekly schedule stops. This can't be undone{releasing?.status === 'Flying' ? ' - and cannot be done while they are flying.' : '.'}
            </DialogDescription>
          </DialogHeader>
          {releaseError && <p className="text-sm text-danger">{releaseError}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setReleasing(null)} disabled={releaseBusy}>
              Cancel
            </Button>
            <Button type="button" variant="destructive" onClick={handleRelease} disabled={releaseBusy}>
              {releaseBusy ? 'Releasing…' : 'Release pilot'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <PilotScheduleDialog
        pilot={scheduling}
        onOpenChange={(open) => !open && setScheduling(null)}
        onSaved={() => pilotsQuery.refetch()}
      />
    </div>
  )
}
