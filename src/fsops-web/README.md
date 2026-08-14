# fsops-web

The FSOps single-page app: React 18, TypeScript (strict), Tailwind CSS v3, and hand-rolled
shadcn-style components in `src/components/ui/`.

It is not a standalone site. In a normal run it is built into the server's `wwwroot` and served by
FSOps itself on `http://localhost:5977`, so the API and the UI share one origin.

## Commands

```
npm run dev        # dev server on 5173, proxying /api and /hubs to the server on 5977
npm run build      # type-check, then build into ../FSOps.Server/wwwroot
npm run test:run   # Vitest, once
npm run lint -- --deny-warnings   # oxlint, the way CI runs it
npx tsc --noEmit   # type-check only
```

**Lint with `--deny-warnings` or it cannot fail you.** oxlint exits 0 with its warnings printed to
stdout, so a bare `npm run lint` reports problems and still succeeds. CI passes the flag
(`.github/workflows/ci.yml`), so that is the command whose result actually decides anything.

`npm run dev` needs the FSOps server running separately for anything that touches data. To see the
app exactly as a player does, build it and run the server on its own.

## Conventions worth knowing before editing

- **Use the design tokens.** Colours come from CSS custom properties defined in `src/index.css`;
  no hardcoded hex values. Both light and dark themes have to look right.
- **Tailwind v3, not v4** — the syntax differs and v4 examples will not work here.
- **No external runtime network calls.** Fonts and map assets are bundled.
- **MapLibre is loaded through a dynamic import** so it stays out of the main bundle. Follow the
  existing pattern in the map components rather than importing it at the top level.
