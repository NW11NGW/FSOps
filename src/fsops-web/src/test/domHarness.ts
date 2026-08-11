/**
 * Minimal DOM mount/interact helpers for the handful of dialog tests that need to drive a real
 * click/submit through React state and into a mocked `post()` call. Deliberately NOT
 * @testing-library/react: no snapshotting, no query-by-role sugar - just enough to mount a
 * component into jsdom, flip an input's value the way a browser does, click a button, and let
 * the caller assert on what reached the mocked API. Kept tiny on purpose so it stays easy to
 * reason about rather than becoming a second testing library.
 */
import { act, type ReactElement } from 'react'
import { createRoot, type Root } from 'react-dom/client'

export interface Mounted {
  /** The off-document container the tree was rendered into. Dialog content itself renders via a
   *  portal into `document.body`, not in here - use `document.body.querySelector` for that. */
  container: HTMLElement
  rerender: (next: ReactElement) => Promise<void>
  unmount: () => void
}

/** Mounts `element`, flushing effects and any already-settled microtasks before returning. */
export async function mount(element: ReactElement): Promise<Mounted> {
  const container = document.createElement('div')
  document.body.appendChild(container)
  let root: Root | null = null

  await act(async () => {
    root = createRoot(container)
    root.render(element)
  })

  return {
    container,
    rerender: async (next: ReactElement) => {
      await act(async () => {
        root?.render(next)
      })
    },
    unmount: () => {
      act(() => {
        root?.unmount()
      })
      container.remove()
    },
  }
}

/** Lets pending promise chains (e.g. a mocked `fetch`/`api` call) settle before the next
 *  assertion, without relying on fake timers. */
export async function flush(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0))
  })
}

/**
 * Sets a controlled `<input>`'s value the way a real keystroke would, so React's onChange fires.
 * Assigning `.value` directly does not trigger React's change detection - React patches the
 * native value setter to track "last known value", so the change has to go through that setter.
 */
export function typeInto(input: HTMLInputElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set
  act(() => {
    setter?.call(input, value)
    input.dispatchEvent(new Event('input', { bubbles: true }))
  })
}

/**
 * Fires the same mouse event sequence a real click produces (mousedown, mouseup, click) rather
 * than just 'click' - Radix's Tabs trigger acts on `onMouseDown` (activates on press, before the
 * click completes), not `onClick`, so a synthetic 'click'-only dispatch silently does nothing.
 * Synchronous UI updates are flushed before returning; if the click kicks off an async handler
 * (a submit that awaits `post()`), call `flush()` afterwards.
 */
export function click(element: Element): void {
  act(() => {
    const options = { bubbles: true, cancelable: true, button: 0 }
    element.dispatchEvent(new MouseEvent('mousedown', options))
    element.dispatchEvent(new MouseEvent('mouseup', options))
    element.dispatchEvent(new MouseEvent('click', options))
  })
}

/** Finds the first <button> under `root` whose text contains `text`. Dialog content renders via a
 *  portal into document.body, so `root` is usually `document.body`, not the mount container. */
export function findButton(root: ParentNode, text: string): HTMLButtonElement {
  const buttons = Array.from(root.querySelectorAll('button'))
  const match = buttons.find((b) => b.textContent?.includes(text))
  if (!match) {
    throw new Error(`No button found containing "${text}". Seen: ${buttons.map((b) => JSON.stringify(b.textContent)).join(', ')}`)
  }
  return match
}
