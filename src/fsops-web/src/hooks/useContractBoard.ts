import { useCallback, useEffect, useRef, useState } from 'react'

import { get, post } from '@/lib/api'
import type { ContractAbandonResult, ContractBoard, ContractStartLegResult } from '@/types/contract'

export type ContractBoardStatus = 'loading' | 'ready' | 'error'

export interface UseContractBoardResult {
  status: ContractBoardStatus
  board: ContractBoard | null
  /** The server's own words for a failed board read. Never a blank panel. */
  errorMessage: string | null
  /** True while an accept, a start or an abandon is in flight, so the whole board disables as one. */
  busy: boolean
  /**
   * True while a player-requested refresh is in flight.
   *
   * <p>Separate from {@link busy} because it means something different to the player: `busy` is "a
   * change is being made", this is "I am checking". Without it the Refresh button was a control with
   * no observable effect whatsoever - see {@link UseContractBoardResult.refetch}.</p>
   */
  refreshing: boolean
  /** When the last player-requested refresh completed, as epoch ms - null until they ask for one.
   *  Drives the "checked just now" confirmation; see {@link refetch} for why that confirmation has
   *  to exist. */
  lastRefreshedAt: number | null
  /**
   * Re-reads the board from the server.
   *
   * <p><b>This usually returns identical data, by design.</b> The board is generated deterministically
   * per 24-hour bucket, so re-reading it before the bucket rolls gives back the same jobs - refreshing
   * is not a re-roll, and must not become one, or the predictable world stops being predictable.</p>
   *
   * <p>That is exactly why the caller has to SHOW something: a button whose honest answer is "nothing
   * has changed" still has to say so, or it reads as broken. It was reported as broken.</p>
   */
  refetch: () => void
  accept: (contractId: string) => Promise<void>
  startLeg: (contractId: string) => Promise<ContractStartLegResult>
  abandon: (contractId: string) => Promise<ContractAbandonResult>
}

function messageOf(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

/**
 * The contract board's data and the three things a player can do to a job.
 *
 * <p><b>Every mutation refetches the whole board rather than patching a contract in place.</b> That
 * looks wasteful and is deliberate: accepting moves a job from `offered` to `accepted`, starting a
 * leg changes which leg is next AND what abandoning would now cost, and abandoning closes the job
 * and moves money. Those are server-side consequences that reach further than the contract the
 * caller named, and guessing at them here is how the board comes to disagree with the ledger.</p>
 *
 * <p><b>There is deliberately no polling.</b> The board refreshes on its own schedule (`refreshesUtc`)
 * and the world is meant to be predictable - nothing should change under the player while they are
 * reading it. They refresh when they choose to.</p>
 */
export function useContractBoard(): UseContractBoardResult {
  const [status, setStatus] = useState<ContractBoardStatus>('loading')
  const [board, setBoard] = useState<ContractBoard | null>(null)
  const [errorMessage, setErrorMessage] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [refreshing, setRefreshing] = useState(false)
  const [lastRefreshedAt, setLastRefreshedAt] = useState<number | null>(null)
  const cancelledRef = useRef(false)

  const load = useCallback(async () => {
    try {
      const result = await get<ContractBoard>('/contracts/board')
      if (cancelledRef.current) return
      setBoard(result)
      setErrorMessage(null)
      setStatus('ready')
    } catch (err) {
      if (cancelledRef.current) return
      setErrorMessage(messageOf(err, 'Could not load the contract board. Check your connection and try again.'))
      setStatus('error')
    }
  }, [])

  const refetch = useCallback(() => {
    setStatus((current) => (current === 'ready' ? current : 'loading'))
    setRefreshing(true)
    void load().finally(() => {
      if (cancelledRef.current) return
      setRefreshing(false)
      // Stamped even when the board came back byte-identical - "I checked, and nothing had changed"
      // is the answer in the overwhelming majority of cases, and it is still an answer.
      setLastRefreshedAt(Date.now())
    })
  }, [load])

  useEffect(() => {
    cancelledRef.current = false
    void load()
    return () => {
      cancelledRef.current = true
    }
  }, [load])

  /**
   * Runs one mutation and reloads the board from the server afterwards.
   *
   * The reload is awaited before resolving so a caller that closes a dialog on success is closing it
   * over already-current data - a dialog that shuts and leaves a stale figure behind it reads exactly
   * like the action not having worked.
   */
  const run = useCallback(
    async <T,>(work: () => Promise<T>): Promise<T> => {
      setBusy(true)
      try {
        const result = await work()
        await load()
        return result
      } finally {
        if (!cancelledRef.current) setBusy(false)
      }
    },
    [load],
  )

  const accept = useCallback(
    async (contractId: string) => {
      await run(() => post(`/contracts/${contractId}/accept`))
    },
    [run],
  )

  const startLeg = useCallback(
    (contractId: string) => run(() => post<ContractStartLegResult>(`/contracts/${contractId}/start-leg`)),
    [run],
  )

  const abandon = useCallback(
    (contractId: string) => run(() => post<ContractAbandonResult>(`/contracts/${contractId}/abandon`)),
    [run],
  )

  return { status, board, errorMessage, busy, refreshing, lastRefreshedAt, refetch, accept, startLeg, abandon }
}
