import { Component, type ErrorInfo, type ReactNode } from 'react'
import { AlertTriangle } from 'lucide-react'

import { Button } from '@/components/ui/button'

interface ErrorBoundaryProps {
  children: ReactNode
}

interface ErrorBoundaryState {
  error: Error | null
}

/**
 * Catches render-time errors so one broken component degrades to a readable message instead of
 * an empty page. Without this, any thrown error unmounts the whole tree and the user is left
 * staring at a blank white screen with no indication of what happened or what to do - which is
 * exactly what a stale field in one card produced during Chunk D.
 *
 * React only surfaces render errors to class components, so this stays a class by necessity.
 */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { error }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // Keep the detail in the console: the message shown below is deliberately plain, but anyone
    // diagnosing a report needs the stack and the component that failed.
    console.error('Unhandled error in FSOps UI', error, info.componentStack)
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    return (
      <div className="flex min-h-[60vh] items-center justify-center p-6">
        <div className="max-w-md space-y-4 rounded-lg border border-border bg-surface p-6 text-center">
          <div className="flex justify-center">
            <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-warning/15 text-warning">
              <AlertTriangle className="size-5" />
            </span>
          </div>
          <div className="space-y-1.5">
            <h2 className="text-lg font-semibold tracking-tight">Something went wrong on this screen</h2>
            <p className="min-w-0 break-words text-sm text-muted-foreground">
              The rest of FSOps is still running, and nothing has been lost. If this keeps happening after a
              restart, the details are in the browser console and in your FSOps log file.
            </p>
          </div>
          <p className="break-words rounded-md border border-border bg-surface-elevated px-3 py-2 text-left font-mono text-xs text-muted-foreground">
            {error.message}
          </p>
          <Button type="button" onClick={() => this.setState({ error: null })}>
            Try again
          </Button>
        </div>
      </div>
    )
  }
}
