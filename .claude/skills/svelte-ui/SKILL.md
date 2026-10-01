---
name: svelte-ui
description: Ariva.Web patterns (SvelteKit 2, Svelte 5 runes, shadcn-svelte and Bits UI, Tailwind 4 tokens from the prototype, ECharts, i18n with RTL, SignalR live data, CSP-safe code). Load for any front-end work.
---
# Ariva.Web patterns

- Reference screens: docs/design/prototype/app (open index.html). Port behaviour and layout, not code.
- MCP: `svelte` (docs, svelte-autofixer on every component), `shadcn-svelte` (components, Bits UI, Lucide icons). Install components with `npx shadcn-svelte@latest add <name>`; keep generated components in `src/lib/components/ui`.
- Structure (AMAN-style): `src/routes/(modules)/...` authenticated routes, `src/routes/(public)/login`, `src/lib/components/pages/<feature>/`, `src/lib/core/{Api.ts,Endpoints.ts,Live.ts}`, `src/lib/i18n/{en,ar}.json`.
- Tokens: the prototype palette as Tailwind theme variables (bg #0A1015, panel #111A21, line #22323E, ink #DCE6EC, muted #8C9CA8, accent #3FB8C9, good #3DBE7A, warn #E8A93A, crit #EF5B52), dark by default with a light theme; tabular numerals for live figures.
- State: `$state`, `$derived`, `$effect`, `$props`; components small; no global mutable stores for screen state.
- Live: `Live.ts` wraps the SignalR client (`accessTokenFactory` from the in-memory auth store, automatic reconnect, group join per zone or checkpoint); show "stale" when the last update is older than the threshold.
- Charts: ECharts with titles and text summaries; P50 line and P90 band; target line at the SLA threshold.
- Floor plan: SVG with zones coloured by status and the wait printed as text; keyboard focusable shapes; the zones editor uses pointer events with a vertex table alternative.
- i18n: every string through svelte-i18n; `dir` and `lang` on `<html>` follow the locale; logical CSS properties only.
- Security: no `{@html}`, no innerHTML, no eval, no URL fetches from input, tokens never in web storage, strict CSP (no inline scripts).
