import { X } from 'lucide-react'
import { Link } from 'react-router-dom'

import { Button } from '@/components/ui/button'
import { useUpdate } from '@/hooks/useUpdate'

/**
 * A single quiet line telling the user a newer FSOps exists. Mounted app-wide, but it renders
 * nothing at all unless there is genuinely something to say.
 *
 * <p><b>Why a line and not a dialog.</b> An update is not a problem, and this app is used with a
 * flight simulator running - often mid-flight, sometimes on a second monitor beside an approach
 * plate. Anything modal, anything that steals focus, and anything coloured like a warning would be
 * wrong for "there is a slightly newer version of this whenever you feel like it". So: one line, in
 * the app's ordinary surface colours, at the top of the shell, with a dismiss that is respected
 * permanently for that version.</p>
 *
 * <p><b>What it never does.</b> It never appears because a check failed. No internet, GitHub down,
 * rate limited, a corporate proxy, an offline sim rig - all of those produce exactly what "you are
 * up to date" produces, which is nothing. The user is told when there is good news and left alone
 * otherwise.</p>
 */
export function UpdateNotice() {
  const { status, busy, dismiss } = useUpdate()

  if (!status || !status.enabled || !status.updateAvailable || status.dismissed) {
    return null
  }

  return (
    <div
      role="status"
      className="flex items-center gap-3 border-b border-border bg-surface-elevated px-6 py-2 text-sm"
    >
      <span className="min-w-0 flex-1 truncate text-muted-foreground">
        FSOps <span className="font-mono text-foreground">{status.latestVersion}</span> is available — you have{' '}
        <span className="font-mono">{status.currentVersion}</span>.
      </span>

      <Button asChild variant="outline" size="sm">
        <Link to="/settings">See what&apos;s new</Link>
      </Button>

      <Button
        type="button"
        variant="ghost"
        size="sm"
        disabled={busy}
        aria-label={`Dismiss the update to version ${status.latestVersion}`}
        onClick={() => void dismiss()}
      >
        <X className="size-4" aria-hidden />
      </Button>
    </div>
  )
}
