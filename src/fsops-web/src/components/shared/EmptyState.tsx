import type { ComponentType, ReactNode } from 'react'
import type { LucideProps } from 'lucide-react'
import { Inbox } from 'lucide-react'

import { Card, CardContent } from '@/components/ui/card'

interface EmptyStateProps {
  icon?: ComponentType<LucideProps>
  title?: string
  description?: string
  action?: ReactNode
}

export function EmptyState({
  icon: Icon = Inbox,
  title = 'Nothing here yet',
  description = 'This arrives in a later update.',
  action,
}: EmptyStateProps) {
  return (
    <Card>
      <CardContent className="flex flex-col items-center gap-3 py-16 text-center">
        <div className="flex size-12 items-center justify-center rounded-full bg-muted text-muted-foreground">
          <Icon className="size-5" />
        </div>
        <div className="space-y-1">
          <p className="text-sm font-medium">{title}</p>
          <p className="text-sm text-muted-foreground">{description}</p>
        </div>
        {action}
      </CardContent>
    </Card>
  )
}
