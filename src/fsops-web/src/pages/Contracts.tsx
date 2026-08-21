import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { ClipboardList, Info, PlaneTakeoff, RefreshCw, Settings as SettingsIcon } from 'lucide-react'
import { toast } from 'sonner'

import { AbandonContractDialog } from '@/components/contracts/AbandonContractDialog'
import { ContractAcceptButton, ContractCard, ContractKindLegend } from '@/components/contracts/ContractCard'
import { EmptyState } from '@/components/shared/EmptyState'
import { PageHeader } from '@/components/shared/PageHeader'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useContractBoard } from '@/hooks/useContractBoard'
import { ApiError } from '@/lib/api'
import { formatDateTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import type { Contract } from '@/types/contract'

function describeError(err: unknown, fallback: string): string {
  return err instanceof ApiError && err.message ? err.message : fallback
}

/**
 * Contract flying: a board of jobs other operators are offering, and the ones the player has taken.
 *
 * <p><b>Why this is its own page rather than part of Fly.</b> The Fly screen answers "what am I
 * flying right now" - pick a route, read the brief, go - and it is already the busiest screen in the
 * app. A contract board answers a different question over a much longer horizon: a ferry accepted
 * tonight might be eleven legs and several weeks of evenings, with the aeroplane sitting where it was
 * left in between. That is a standing commitment the player comes back to, not something chosen at
 * the moment of departure, and it needs somewhere to live between sessions. It sits directly beneath
 * Fly in the sidebar because it is still the player's own flying, and starting a leg hands straight
 * over to Fly, which is where every tracked flight belongs.</p>
 */
export function Contracts() {
  const { status, board, errorMessage, busy, refreshing, lastRefreshedAt, refetch, accept, startLeg, abandon } =
    useContractBoard()
  const [abandoning, setAbandoning] = useState<Contract | null>(null)
  const navigate = useNavigate()

  async function handleAccept(contract: Contract) {
    try {
      await accept(contract.id)
      toast.success(`Accepted ${contract.operatorName}'s job. It's now under "Your jobs".`)
    } catch (err) {
      toast.error(describeError(err, 'Could not accept this job.'))
    }
  }

  async function handleStartLeg(contract: Contract) {
    try {
      const leg = await startLeg(contract.id)
      toast.success(`Leg ${leg.legSequence} of ${leg.legCount} started — ${leg.departureIcao} → ${leg.arrivalIcao}.`)
      // A contract leg is a real tracked flight, so it belongs on the Fly screen like any other.
      navigate('/fly')
    } catch (err) {
      // Every refusal here is a sentence a player can act on - a flight already in progress, a leg
      // out of order. Shown as itself rather than as a generic failure.
      toast.error(describeError(err, 'Could not start this leg.'))
    }
  }

  async function handleAbandon(contractId: string) {
    const result = await abandon(contractId)
    toast.success(
      result.charge > 0
        ? `Job handed back. ${result.reason}`
        : `Job handed back at no charge. ${result.reason}`,
    )
  }

  if (status === 'loading') {
    return (
      <div>
        <PageHeader title="Contracts" description="Fly for other operators, in their aircraft, for a flat fee." />
        <div className="space-y-4">
          <Skeleton className="h-24 w-full" />
          <Skeleton className="h-64 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      </div>
    )
  }

  if (status === 'error') {
    return (
      <div>
        <PageHeader title="Contracts" description="Fly for other operators, in their aircraft, for a flat fee." />
        <EmptyState
          icon={ClipboardList}
          title="Could not load the contract board"
          description={errorMessage ?? 'Check your connection and try again.'}
          action={
            <Button type="button" variant="outline" onClick={refetch}>
              Try again
            </Button>
          }
        />
      </div>
    )
  }

  const offered = board?.offered ?? []
  const accepted = board?.accepted ?? []
  const limitation = board?.limitation.message ?? null

  return (
    <div className="space-y-8">
      <PageHeader
        title="Contracts"
        description="Fly for other operators, in their aircraft, for a flat fee. They pay the bills; you're paid per leg you fly."
        actions={
          // Refreshing re-READS the board; it never re-rolls it (see useContractBoard.refetch). So
          // the honest outcome is almost always "nothing has changed", and the button has to show
          // that rather than appearing inert - reported as "refresh does not appear to do anything",
          // which was fair: it did not disable, did not spin, and the deterministic board came back
          // identical, so not one pixel moved.
          <div className="flex items-center gap-3">
            {/* Deliberately not "no new jobs": the bucket CAN roll between reads, and a message that
              *  claims nothing changed while the board changed underneath it is worse than the
              *  silence it replaced. "Checked just now" is true either way. */}
            {lastRefreshedAt !== null && !refreshing && (
              <span className="text-xs text-muted-foreground" role="status">
                Checked just now
              </span>
            )}
            <Button
              type="button"
              variant="outline"
              onClick={refetch}
              disabled={busy || refreshing}
              className="gap-2"
            >
              <RefreshCw className={cn('size-4', refreshing && 'animate-spin')} />
              {refreshing ? 'Checking…' : 'Refresh'}
            </Button>
          </div>
        }
      />

      {/* A thin board must say why. Swallowing this leaves the player thinking the feature is broken
       *  when in fact it is one click from being fixed. The server's own words, verbatim. */}
      {limitation && (
        <Card className="border-warning/30">
          <CardContent className="flex items-start gap-3 p-4">
            <Info className="mt-0.5 size-4 shrink-0 text-warning" />
            <div className="min-w-0 space-y-2">
              <p className="text-sm text-warning">{limitation}</p>
              <Button type="button" variant="outline" size="sm" onClick={() => navigate('/settings')} className="gap-2">
                <SettingsIcon className="size-3.5" />
                Open Settings
              </Button>
            </div>
          </CardContent>
        </Card>
      )}

      <section className="space-y-4">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-base font-semibold tracking-tight">Your jobs</h2>
          {accepted.length > 0 && (
            <p className="text-xs text-muted-foreground">
              {accepted.length} accepted — these stay yours through every board refresh.
            </p>
          )}
        </div>

        {accepted.length === 0 ? (
          <EmptyState
            icon={PlaneTakeoff}
            title="No jobs accepted"
            description="Take something from the board below and it will appear here, with its chain of stops and where you have got to."
          />
        ) : (
          <div className="space-y-4">
            {accepted.map((contract) => (
              <ContractCard
                key={contract.id}
                contract={contract}
                expanded
                action={
                  <div className="flex flex-wrap items-center gap-2">
                    <Button type="button" variant="ghost" size="sm" onClick={() => setAbandoning(contract)} disabled={busy}>
                      Hand back
                    </Button>
                    {contract.nextLeg ? (
                      <Button type="button" onClick={() => handleStartLeg(contract)} disabled={busy} className="gap-2">
                        <PlaneTakeoff className="size-4" />
                        Fly leg {contract.nextLeg.sequence}: {contract.nextLeg.departureIcao} → {contract.nextLeg.arrivalIcao}
                      </Button>
                    ) : (
                      <span className="text-sm text-muted-foreground">Every leg flown.</span>
                    )}
                  </div>
                }
              />
            ))}
          </div>
        )}
      </section>

      <section className="space-y-4">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h2 className="text-base font-semibold tracking-tight">On the board</h2>
          {board && (
            <p className="text-xs text-muted-foreground">
              Refreshes {formatDateTime(board.refreshesUtc)}
            </p>
          )}
        </div>

        <ContractKindLegend />

        {offered.length === 0 ? (
          <EmptyState
            icon={ClipboardList}
            title="Nothing on the board right now"
            description={
              limitation ??
              'The board refreshes on its own schedule — check back after the time shown above, and jobs will have turned over.'
            }
          />
        ) : (
          <div className="space-y-4">
            {offered.map((contract) => (
              <ContractCard
                key={contract.id}
                contract={contract}
                action={<ContractAcceptButton onAccept={() => handleAccept(contract)} disabled={busy} />}
              />
            ))}
          </div>
        )}
      </section>

      <AbandonContractDialog
        contract={abandoning}
        onOpenChange={(open) => !open && setAbandoning(null)}
        onConfirm={handleAbandon}
      />
    </div>
  )
}
