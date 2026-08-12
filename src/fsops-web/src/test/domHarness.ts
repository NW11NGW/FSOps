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
 * Chooses an option on a controlled `<select>` the way a real selection would. Same reasoning as
 * `typeInto`: React patches the native value setter to track the last known value, so assigning
 * `.value` directly leaves the tracker stale and the change event is treated as a no-op.
 */
export function selectOption(select: HTMLSelectElement, value: string): void {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLSelectElement.prototype, 'value')?.set
  act(() => {
    setter?.call(select, value)
    select.dispatchEvent(new Event('change', { bubbles: true }))
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

/* ------------------------------------------------------------------------------------------- *
 * Role and accessible-name queries
 *
 * These exist for the same reason as everything above: to let a test say "the button labelled
 * Repay" without pulling in a second testing library. That is not a reversal of the decision at
 * the top of this file. The objection recorded there is to SNAPSHOTTING - to tests that pin the
 * rendered DOM and then break on every spacing tweak - and to a large dependency growing a second
 * way of doing things. Finding an element by its role and accessible name is the opposite of that
 * problem: it is the most churn-resistant handle a test can take, because it survives a wrapper
 * div, a re-ordered class list or a Tailwind change, and it fails only when the thing a user can
 * actually perceive has changed. Reaching for `querySelector('.text-danger')` is the habit worth
 * avoiding, and these helpers exist so nobody needs to.
 *
 * Deliberately partial, and meant to stay that way. The role map covers only the roles this app's
 * own tests assert on, and `accessibleName` implements the top of the accname algorithm
 * (aria-label, then aria-labelledby, then an associated <label> for form controls, then text
 * content) rather than the whole thing. If a test ever needs more than this - full accname,
 * visibility filtering, async findBy* retries, user-event's input simulation - that is the signal
 * that @testing-library/react was the right answer after all. Add the dependency at that point;
 * do not finish reimplementing it here.
 * ------------------------------------------------------------------------------------------- */

const ROLE_SELECTORS = {
  button: 'button, [role="button"]',
  cell: 'td, [role="cell"]',
  columnheader: 'th, [role="columnheader"]',
  combobox: 'select, [role="combobox"]',
  heading: 'h1, h2, h3, h4, h5, h6, [role="heading"]',
  link: 'a[href], [role="link"]',
  list: 'ul, ol, [role="list"]',
  listitem: 'li, [role="listitem"]',
  option: 'option, [role="option"]',
  row: 'tr, [role="row"]',
  tab: '[role="tab"]',
  table: 'table, [role="table"]',
  textbox: 'textarea, input[type="text"], input[type="search"], input:not([type]), [role="textbox"]',
} as const

export type Role = keyof typeof ROLE_SELECTORS

/** Collapses whitespace the way a browser does when it renders text, so an assertion doesn't have
 *  to care that JSX split a sentence across three lines and two elements. */
function normalise(value: string | null | undefined): string {
  return (value ?? '').replace(/\s+/g, ' ').trim()
}

/**
 * All the text a user would read under `root`, whitespace-collapsed.
 *
 * The workhorse for "does this state say the right thing". Prefer it over raw `textContent`:
 * React routinely splits one rendered sentence across several text nodes (`{' '}`, interpolated
 * values, nested <strong>), so `textContent` contains line breaks and double spaces in places the
 * rendered output has none, and a `toContain` against a natural sentence fails for reasons that
 * have nothing to do with the component.
 */
export function text(root: ParentNode | Element): string {
  return normalise((root as Element).textContent)
}

/**
 * The name assistive technology would announce for `element`: aria-label, else aria-labelledby,
 * else the associated <label> for a form control, else its own text. Enough for this app's
 * controls; see the block comment above for when that stops being true.
 */
export function accessibleName(element: Element): string {
  const ariaLabel = element.getAttribute('aria-label')
  if (ariaLabel) return normalise(ariaLabel)

  const labelledBy = element.getAttribute('aria-labelledby')
  if (labelledBy) {
    return normalise(
      labelledBy
        .split(/\s+/)
        .map((id) => document.getElementById(id)?.textContent ?? '')
        .join(' '),
    )
  }

  // Only form controls take their name from a <label>; for anything else `closest('label')` would
  // happily attribute a surrounding label's text to a button that merely sits inside it.
  const isFormControl =
    element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement
  if (isFormControl) {
    if (element.id) {
      const explicit = document.querySelector(`label[for="${CSS.escape(element.id)}"]`)
      if (explicit) return normalise(explicit.textContent)
    }
    const wrapping = element.closest('label')
    if (wrapping) return normalise(wrapping.textContent)
  }

  return normalise(element.textContent)
}

/** A string name must match the whole accessible name; a RegExp is tested against it. Exact-by-
 *  default is deliberate - a substring match would quietly pass when a button's label gained a
 *  price or a count it should not have. Use a RegExp when a partial match is genuinely wanted. */
function matchesName(actual: string, expected: string | RegExp): boolean {
  return typeof expected === 'string' ? actual === expected : expected.test(actual)
}

/** Every element under `root` with the given role, optionally narrowed to an accessible name. */
export function queryAllByRole(root: ParentNode, role: Role, options: { name?: string | RegExp } = {}): HTMLElement[] {
  const candidates = Array.from(root.querySelectorAll<HTMLElement>(ROLE_SELECTORS[role]))
  if (options.name === undefined) return candidates
  return candidates.filter((element) => matchesName(accessibleName(element), options.name!))
}

/**
 * The single matching element, or null when there is none - for asserting that a control is
 * deliberately ABSENT, which `getByRole` cannot express.
 *
 * Throws when more than one element matches. An ambiguous query is a finding, not a convenience
 * to paper over: it means either the assertion is looser than the author thought, or the page is
 * rendering a control twice.
 */
export function queryByRole(root: ParentNode, role: Role, options: { name?: string | RegExp } = {}): HTMLElement | null {
  const matches = queryAllByRole(root, role, options)
  if (matches.length > 1) {
    throw new Error(
      `Expected at most one ${role}${describeName(options.name)}, found ${matches.length}: ${matches.map((m) => JSON.stringify(accessibleName(m))).join(', ')}`,
    )
  }
  return matches[0] ?? null
}

/** The single matching element, throwing a message that lists what WAS there when it misses. */
export function getByRole(root: ParentNode, role: Role, options: { name?: string | RegExp } = {}): HTMLElement {
  const match = queryByRole(root, role, options)
  if (!match) {
    const seen = queryAllByRole(root, role).map((element) => JSON.stringify(accessibleName(element)))
    throw new Error(
      `No ${role}${describeName(options.name)} found. ${role}s present: ${seen.length ? seen.join(', ') : '(none)'}`,
    )
  }
  return match
}

function describeName(name: string | RegExp | undefined): string {
  return name === undefined ? '' : ` named ${typeof name === 'string' ? JSON.stringify(name) : String(name)}`
}

/** True when a control is non-interactive, by either the native attribute or aria-disabled. */
export function isDisabled(element: Element): boolean {
  return element.hasAttribute('disabled') || element.getAttribute('aria-disabled') === 'true'
}
