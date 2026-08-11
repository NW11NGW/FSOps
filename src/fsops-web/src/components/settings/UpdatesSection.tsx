import { CheckCircle2, Download, FolderOpen, RefreshCw, ShieldCheck } from 'lucide-react'

import { ToggleGroup } from '@/components/shared/ToggleGroup'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { useUpdate } from '@/hooks/useUpdate'
import { formatDownloadedBytes, shortenHash } from '@/lib/updateApi'

/**
 * The updater's home. Everything it can do lives here, so the app-wide notice can stay to a single
 * quiet line and this is where anyone who wants detail comes.
 *
 * <p>Three things about this screen are deliberate rather than incidental:</p>
 * <ul>
 *   <li><b>The switch really is a switch.</b> Turned off, the server makes no outbound request at
 *   all - not one that is made and then ignored. It is the only thing FSOps ever sends anywhere.</li>
 *   <li><b>The checksum is shown, not just used.</b> FSOps ships unsigned, so the SHA-256 published
 *   with a release is the only thing that says a downloaded installer is the one the author built.
 *   Showing it lets anyone check it against the release page themselves.</li>
 *   <li><b>Nothing here runs an installer.</b> The button opens the folder. Deciding to run an
 *   unsigned installer belongs to the person, in Explorer, where they can see what they are
 *   launching - an app that quietly executed a downloaded binary would be building the exact
 *   problem the checksum exists to prevent.</li>
 * </ul>
 */
export function UpdatesSection() {
  const { status, loading, busy, checkNow, setEnabled, download, reveal } = useUpdate()

  if (loading) {
    return (
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2 text-base">
            <RefreshCw className="size-4 text-accent" />
            Updates
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <Skeleton className="h-5 w-48" />
          <Skeleton className="h-4 w-72" />
        </CardContent>
      </Card>
    )
  }

  if (!status) {
    return null
  }

  const lastChecked = status.lastCheckedUtc ? new Date(status.lastCheckedUtc) : null

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          <RefreshCw className="size-4 text-accent" />
          Updates
        </CardTitle>
        <CardDescription>
          FSOps can look at its own releases page once a day to see whether a newer version exists. This is the only
          thing the app ever sends anywhere, and turning it off stops the request being made at all — nothing else in
          FSOps needs the internet.
        </CardDescription>
      </CardHeader>

      <CardContent className="space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <p className="text-sm font-medium">Check for updates</p>
            <p className="text-xs text-muted-foreground">
              You are running version <span className="font-mono text-foreground">{status.currentVersion}</span>.
            </p>
          </div>
          <ToggleGroup
            ariaLabel="Check for updates"
            value={status.enabled ? 'on' : 'off'}
            onChange={(value) => void setEnabled(value === 'on')}
            options={[
              { value: 'on', label: 'On' },
              { value: 'off', label: 'Off' },
            ]}
          />
        </div>

        {!status.enabled && (
          <p className="rounded-md border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
            Update checks are off. FSOps will not contact anything outside your machine. You can still download new
            versions yourself from the project's releases page whenever you like.
          </p>
        )}

        {status.enabled && (
          <>
            <div className="flex flex-wrap items-center gap-3">
              <Button type="button" variant="outline" size="sm" disabled={busy || status.checking} onClick={() => void checkNow()}>
                <RefreshCw className={status.checking ? 'mr-2 size-4 animate-spin' : 'mr-2 size-4'} />
                {status.checking ? 'Checking…' : 'Check now'}
              </Button>
              <span className="text-xs text-muted-foreground">
                {lastChecked ? `Last checked ${lastChecked.toLocaleString()}` : 'Not checked yet'}
                {status.lastCheckFailed && ' — could not reach the releases page, so nothing has changed'}
              </span>
            </div>

            {!status.updateAvailable && !status.checking && (
              <div className="flex items-center gap-2 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
                <CheckCircle2 className="size-4 text-success" aria-hidden />
                <span>You are on the latest version.</span>
              </div>
            )}

            {status.updateAvailable && (
              <div className="space-y-3 rounded-md border border-accent/40 bg-accent/5 p-3">
                <div>
                  <p className="text-sm font-medium">
                    Version <span className="font-mono">{status.latestVersion}</span> is available
                  </p>
                  {status.releasePublishedUtc && (
                    <p className="text-xs text-muted-foreground">
                      Published {new Date(status.releasePublishedUtc).toLocaleDateString()}
                    </p>
                  )}
                </div>

                {status.releaseNotes && (
                  <p className="max-h-40 overflow-y-auto whitespace-pre-line text-xs text-muted-foreground scrollbar-thin">
                    {status.releaseNotes}
                  </p>
                )}

                <div className="flex flex-wrap items-center gap-2">
                  {status.releaseUrl && (
                    <Button type="button" variant="outline" size="sm" asChild>
                      <a href={status.releaseUrl} target="_blank" rel="noreferrer noopener">
                        What&apos;s new
                      </a>
                    </Button>
                  )}

                  {status.downloadAvailable && status.downloadState !== 'ready' && (
                    <Button
                      type="button"
                      size="sm"
                      disabled={busy || status.downloadState === 'downloading'}
                      onClick={() => void download()}
                    >
                      <Download className="mr-2 size-4" />
                      {status.downloadState === 'downloading'
                        ? `Downloading… ${formatDownloadedBytes(status.downloadedBytes)}`
                        : 'Download and verify'}
                    </Button>
                  )}

                  {status.downloadState === 'ready' && (
                    <Button type="button" size="sm" onClick={() => void reveal()}>
                      <FolderOpen className="mr-2 size-4" />
                      Show the installer
                    </Button>
                  )}
                </div>

                {!status.downloadAvailable && (
                  <p className="text-xs text-muted-foreground">
                    This release does not publish an installer with a checksum, so FSOps will not download it for you.
                    Get it from the releases page instead.
                  </p>
                )}

                {status.downloadState === 'failed' && status.downloadMessage && (
                  <p className="rounded-md border border-danger/40 bg-danger/5 px-3 py-2 text-xs text-danger">
                    {status.downloadMessage}
                  </p>
                )}

                {status.downloadState === 'ready' && status.downloadFileName && (
                  <div className="space-y-1 rounded-md border border-border bg-surface px-3 py-2">
                    <p className="flex items-center gap-2 text-xs font-medium">
                      <ShieldCheck className="size-4 text-success" aria-hidden />
                      {status.downloadFileName} — SHA-256 verified
                    </p>
                    {status.downloadSha256 && (
                      <p className="font-mono text-[11px] text-muted-foreground" title={status.downloadSha256}>
                        {shortenHash(status.downloadSha256)}
                      </p>
                    )}
                    <p className="text-xs text-muted-foreground">
                      FSOps will not run the installer for you — close FSOps, then run it yourself from the folder that
                      opens.
                    </p>
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </CardContent>
    </Card>
  )
}
