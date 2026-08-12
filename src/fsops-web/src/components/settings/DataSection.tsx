import { useState } from 'react'
import { AlertTriangle } from 'lucide-react'
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
import { WorldDataSection } from '@/components/settings/WorldDataSection'
import { ApiError, del } from '@/lib/api'

/**
 * Everything about the data FSOps holds: the world airport/runway data it ships with, and the one
 * irreversible action that throws the player's own data away. They sit together, in that order,
 * because both are "data" to someone scanning the page — but the world-data card is a separate
 * component so its refresh action can never be mistaken for part of the danger zone. Refreshing
 * world data deletes nothing; deleting an airline deletes everything.
 */
export function DataSection() {
  return (
    <>
      <WorldDataSection />
      <DangerZone />
    </>
  )
}

function DangerZone() {
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [deleting, setDeleting] = useState(false)

  async function handleDelete() {
    setDeleting(true)
    try {
      await del('/airline', { confirm: true })
      toast.success('Airline deleted. Starting over…')
      setConfirmOpen(false)
      window.setTimeout(() => window.location.reload(), 700)
    } catch (err) {
      toast.error(err instanceof ApiError ? err.message : 'Could not delete your airline. Try again.')
      setDeleting(false)
    }
  }

  return (
    <Card className="border-danger/30">
      <CardHeader>
        <CardTitle className="text-danger">Danger zone</CardTitle>
        <CardDescription>Irreversible actions. Proceed with care.</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="flex flex-col items-start justify-between gap-4 rounded-md border border-danger/30 bg-danger/5 p-4 sm:flex-row sm:items-center">
          <div>
            <p className="text-sm font-medium">Start over</p>
            <p className="text-xs text-muted-foreground">
              Permanently deletes your airline, fleet, routes, and finances. This cannot be undone.
            </p>
          </div>
          <Button type="button" variant="destructive" onClick={() => setConfirmOpen(true)} className="shrink-0">
            Delete airline
          </Button>
        </div>
      </CardContent>

      <Dialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <AlertTriangle className="size-5 text-danger" />
              Delete your airline?
            </DialogTitle>
            <DialogDescription>
              This permanently removes your airline, fleet, routes, pilots, and financial history. There is no undo.
              You&rsquo;ll be returned to the setup wizard.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setConfirmOpen(false)} disabled={deleting}>
              Cancel
            </Button>
            <Button type="button" variant="destructive" onClick={() => void handleDelete()} disabled={deleting}>
              {deleting ? 'Deleting…' : 'Yes, delete everything'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  )
}
