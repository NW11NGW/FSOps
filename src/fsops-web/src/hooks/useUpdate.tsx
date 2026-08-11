import { useCallback, useEffect, useRef, useState } from 'react'

import {
  checkForUpdateNow,
  dismissUpdate,
  fetchUpdateStatus,
  revealUpdateDownload,
  setUpdateChecksEnabled,
  startUpdateDownload,
  type UpdateStatus,
} from '@/lib/updateApi'

/** How often to re-ask while something is actually in progress. Nothing polls when nothing is
 *  happening - the check itself runs at most once a day, server-side. */
const ACTIVE_POLL_MS = 2000

/**
 * The updater, as a hook. Fetches once on mount and then only keeps asking while the server says
 * something is in flight (a background check, or a download the user started). When the app is idle
 * this makes exactly one request per mount and then stops.
 *
 * <p>Nothing here ever throws into the UI. A failed request leaves the last known status in place
 * and is otherwise ignored, because "the update check did not work" is not something worth
 * interrupting anyone over - it is indistinguishable from being up to date, on purpose.</p>
 */
export function useUpdate() {
  const [status, setStatus] = useState<UpdateStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const mounted = useRef(true)

  useEffect(() => {
    mounted.current = true
    return () => {
      mounted.current = false
    }
  }, [])

  const load = useCallback(async () => {
    try {
      const next = await fetchUpdateStatus()
      if (mounted.current) setStatus(next)
    } catch {
      // Deliberately silent - see the hook's doc comment.
    } finally {
      if (mounted.current) setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const inProgress = status?.checking === true || status?.downloadState === 'downloading'

  useEffect(() => {
    if (!inProgress) return
    const timer = window.setInterval(() => void load(), ACTIVE_POLL_MS)
    return () => window.clearInterval(timer)
  }, [inProgress, load])

  /** Runs an action that returns a fresh status, keeping the UI from firing two at once. */
  const run = useCallback(async (action: () => Promise<UpdateStatus>) => {
    setBusy(true)
    try {
      const next = await action()
      if (mounted.current) setStatus(next)
    } catch {
      // Same rule as load(): never surface a failed update request as an error.
    } finally {
      if (mounted.current) setBusy(false)
    }
  }, [])

  return {
    status,
    loading,
    busy,
    refresh: load,
    checkNow: useCallback(() => run(checkForUpdateNow), [run]),
    setEnabled: useCallback((enabled: boolean) => run(() => setUpdateChecksEnabled(enabled)), [run]),
    dismiss: useCallback(() => run(dismissUpdate), [run]),
    download: useCallback(() => run(startUpdateDownload), [run]),
    reveal: useCallback(async () => {
      try {
        await revealUpdateDownload()
      } catch {
        // The folder failing to open is not worth an error dialog; the path is shown in the UI.
      }
      await load()
    }, [load]),
  }
}
