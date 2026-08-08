import { useState } from 'react'
import { ClipboardCopy, ExternalLink } from 'lucide-react'
import { toast } from 'sonner'

import { Button } from '@/components/ui/button'
import { buildSimBriefUrl, openSimBrief, type SimBriefParams } from '@/lib/simbrief'

interface SimBriefActionsProps {
  params: SimBriefParams
  summaryLines: string[]
}

/** "Plan in SimBrief" hand-off plus a copyable plain-text summary for FMS entry. */
export function SimBriefActions({ params, summaryLines }: SimBriefActionsProps) {
  const [lastUrl, setLastUrl] = useState<string | null>(null)

  function handlePlanInSimBrief() {
    const url = openSimBrief(params)
    setLastUrl(url)
  }

  async function handleCopySummary() {
    const text = summaryLines.join('\n')
    try {
      await navigator.clipboard.writeText(text)
      toast.success('Flight summary copied to clipboard.')
    } catch {
      toast.error('Could not copy to clipboard.')
    }
  }

  return (
    <div className="space-y-2">
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" onClick={handlePlanInSimBrief} className="gap-2">
          <ExternalLink className="size-4" />
          Plan in SimBrief
        </Button>
        <Button type="button" variant="outline" onClick={handleCopySummary} className="gap-2">
          <ClipboardCopy className="size-4" />
          Copy summary
        </Button>
      </div>
      {lastUrl && (
        <p className="truncate text-[11px] text-muted-foreground" title={lastUrl}>
          Opened: {buildSimBriefUrl(params)}
        </p>
      )}
    </div>
  )
}
