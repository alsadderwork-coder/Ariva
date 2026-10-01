---
name: svelte-ui
description: Ariva.Web patterns (SvelteKit 2, Svelte 5 runes, Aman.Web's design system and tokens, shadcn-svelte and Bits UI, Tailwind 4, ECharts, i18n with RTL, SignalR live data, CSP-safe code). Load for any front-end work.
---
# Ariva.Web patterns

## Design system: Aman.Web's, so both products look like one family
- Tokens live in `src/app.css`, copied from Aman.Web's brand theme (`:root.angola` and `:root.angola.dark` in `../Aman/Platform/Frontplane/Aman.Web/src/app.css`) under the class `ariva`. Use the shadcn token classes only: `bg-background`, `bg-card`, `text-foreground`, `text-muted-foreground`, `text-secondary-foreground`, `bg-primary/10 text-primary`, `bg-button-primary`, `border`, `bg-surface-2`, `text-tertiary`, `bg-sidebar*`, and the status families `bg-status-{success,warning,danger,info,neutral}` with `-foreground`, `-border`, `-solid`. Never hard-code hex colours in components.
- Brand values: primary and buttons #2563eb, sidebar primary navy #033069, canvas #f0f0f2, cards white, radius 0.625rem. Two tokens are darker than Aman.Web's because Aman's fail WCAG AA contrast: `--muted-foreground` #5f6871 and `--tertiary` #666a80 (dark: oklch 72%). `tests/functional/accessibility.spec.ts` runs axe (WCAG 2.2 AA) on every screen in light, dark, Arabic and collapsed layouts; add each new screen to it. Dark mode is Aman's slate dark (`.dark` on `<html>`); `static/theme-init.js` applies the saved mode before first paint, `src/lib/theme/theme.svelte.ts` changes it.
- Type: DM Sans Variable (`@fontsource-variable/dm-sans`, as Aman.Web) and Noto Sans Arabic Variable for Arabic (Aman.Web has none; Ariva adds it). Page title `text-[28px] leading-none font-semibold tracking-[-0.14px]`; description `text-[13px] text-secondary-foreground`; section title `text-xl font-semibold`; body `text-sm`; table headers `text-xs font-semibold uppercase tracking-wide text-tertiary`; figures `tabular-nums`.
- Layout (`src/lib/components/layout`): sidebar 19.5rem expanded, 4rem icon rail, state in the `sidebar:state` cookie, Mod+B toggles; groups Operations, Oversight, Administration with uppercase labels; active item `bg-sidebar-accent text-sidebar-accent-foreground` with a 4px start-edge bar; sticky header `h-16 border-b bg-card` with breadcrumb on the start side and actions on the end side; main `p-4 pt-0.5 md:p-6 md:pt-1.5 lg:p-8 lg:pt-1.5`.
- Page pattern (as Aman's list pages): `SimplePageHeader` (44px icon chip, title, description, actions), a metric strip of `MetricCard`s, then cards `rounded-xl border bg-card p-5` holding tables. Tables: `thead bg-surface-2`, header cells `p-4`, rows `odd:bg-surface even:bg-surface-2/50 hover:bg-muted`, cells `px-4 py-3`.
- Status is always text plus colour (`StatusBadge`); never colour alone.
- Icons: Lucide (`@lucide/svelte`). Aman.Web is moving to its own icon font (icosun); revisit when that set is published as a package.
- Toasts: svelte-sonner, `richColors closeButton`, top right (top left in Arabic).
- Example data must be labelled on screen (`DemoDataBanner`, "Demo data" chip) until the live source exists.

## Code
- MCP: `svelte` (docs, `svelte-autofixer` on every component), `shadcn-svelte` (components, Bits UI, Lucide). Install components with `npx shadcn-svelte@latest add <name>` (components.json matches Aman.Web's); keep them in `src/lib/components/ui`.
- Structure (Aman style): `src/routes/(modules)/...` for authenticated screens once login exists (ARV-051), `src/routes/(public)/login`, `src/lib/components/{layout,shared,pages/<feature>,ui}`, `src/lib/core/{Api.ts,Endpoints.ts,Live.ts}`, `src/lib/i18n/{en,ar}.json`, `src/lib/navigation.ts` (nav items with `ready` and the delivering story).
- Runes only: `$state`, `$derived`, `$effect`, `$props`; small components; no global mutable stores for screen state.
- Live: `Live.ts` wraps the SignalR client (`accessTokenFactory` from the in-memory auth store, automatic reconnect, group join per zone or checkpoint); show "stale" when the last update is older than the threshold.
- Charts: ECharts with titles and text summaries; colours from `--chart-1..5` and the status tokens; P50 line and P90 band; target line at the SLA threshold.
- Floor plan: SVG with zones coloured by status and the wait printed as text; keyboard focusable shapes; the zones editor uses pointer events with a vertex table alternative.
- i18n: every string through svelte-i18n, both dictionaries with the same keys; `dir` and `lang` on `<html>` follow the locale; logical properties only (`ms-`, `pe-`, `start-`, `border-e`, `text-start`), mirror directional icons with `rtl:-scale-x-100`.
- Security and CSP: no `{@html}`, no innerHTML, no eval, no URL fetches from input, tokens never in web storage (theme and sidebar preferences are fine). No inline scripts and no static `style="..."` attributes (style-src-attr allows only SvelteKit's announcer); dynamic values go through `style:` directives (CSSOM), which CSP allows.
