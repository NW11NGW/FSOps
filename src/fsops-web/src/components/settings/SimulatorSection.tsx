import { useState } from 'react'
import { FolderOpen } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useSettings } from '@/hooks/useSettings'
import { ApiError } from '@/lib/api'

export function SimulatorSection() {
  const { settings, updateSettings } = useSettings()
  const [path, setPath] = useState(settings.communityFolderPath ?? '')
  const [browseOpen, setBrowseOpen] = useState(false)
  const [saving, setSaving] = useState(false)

  const trimmed = path.trim()
  const looksValid = trimmed.length === 0 || /community[\\/]?$/i.test(trimmed)
  const unchanged = trimmed === (settings.communityFolderPath ?? '')

  async function save() {
    setSaving(true)
    try {
      await updateSettings({ communityFolderPath: trimmed || null })
      toast.success('Community folder updated')
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not save the Community folder — reverted.')
      setPath(settings.communityFolderPath ?? '')
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Microsoft Flight Simulator</CardTitle>
        <CardDescription>Where FSOps looks for your MSFS Community add-ons folder.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <Label htmlFor="community-folder">Community folder</Label>
        <div className="flex flex-col gap-2 sm:flex-row">
          <Input
            id="community-folder"
            value={path}
            placeholder="C:\Users\you\AppData\Local\Packages\...\LocalCache\Packages\Community"
            onChange={(event) => setPath(event.target.value)}
            aria-invalid={!looksValid}
            className="font-mono text-xs"
          />
          <Button type="button" variant="outline" onClick={() => setBrowseOpen(true)} className="shrink-0">
            <FolderOpen /> Browse…
          </Button>
        </div>
        <p className={looksValid ? 'text-xs text-muted-foreground' : 'text-xs text-warning'}>
          {looksValid
            ? 'A correct path ends in a folder named "Community" and contains your installed add-on packages.'
            : 'This doesn\u2019t look like a Community folder — it should end with a folder named "Community".'}
        </p>
        <div className="flex justify-end">
          <Button type="button" onClick={() => void save()} disabled={saving || unchanged}>
            {saving ? 'Saving…' : 'Save'}
          </Button>
        </div>
      </CardContent>

      <Dialog open={browseOpen} onOpenChange={setBrowseOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Browse for your Community folder</DialogTitle>
            <DialogDescription>
              A native folder picker isn&rsquo;t available yet. For now, open File Explorer, navigate to your MSFS
              Community folder, copy its address from the address bar, and paste it into the field above.
            </DialogDescription>
          </DialogHeader>
          <p className="text-sm text-muted-foreground">
            It&rsquo;s usually under something like{' '}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              …\Packages\Microsoft.FlightSimulator_...\LocalCache\Packages\Community
            </code>{' '}
            (Microsoft Store) or <code className="rounded bg-muted px-1 py-0.5 text-xs">…\Community</code> (Steam or
            other installs).
          </p>
          <DialogFooter>
            <Button type="button" onClick={() => setBrowseOpen(false)}>
              Got it
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
