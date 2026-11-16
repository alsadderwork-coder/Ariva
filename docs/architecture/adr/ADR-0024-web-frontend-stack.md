# ADR-0024: Web front end: SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts, svelte-i18n

- Status: Accepted
- Date: 2026-10-01
- Source: Product owner decision, 2026-10-01; D5 asked to use the same framework as Aman.Web

## Context

D5 asked to build the front end in the same framework as Aman.Web to reuse components and skills. Ariva needs dashboards, a live floor plan, a zone and line editor, forecast charts, and kiosk display pages in Arabic (right to left), English, Portuguese and Swahili.

## Decision

Ariva.Web (port 51010) uses SvelteKit 2, Svelte 5, Tailwind 4, bits-ui, ECharts and svelte-i18n. Floor plans are calibrated with two reference points; zones and lines are drawn as polygons in local metric coordinates and stored as GeoJSON (NetTopologySuite on the server; PostGIS optional). Display pages are kiosk URLs per channel.

## Consequences

- Component and skill reuse with Aman.Web.
- Right-to-left layout must be tested for Arabic displays and dashboards.

## Alternatives considered

- A different framework from Aman.Web. Rejected: loses reuse.

## Amendment proposed 2026-11-16 (ARV-300), for the owner's decision

- What: Ariva.Web moves to SvelteKit 3.2.0, with Vite 8, vite-plugin-svelte 7, adapter-static 4 and TypeScript 6. Svelte 5, Tailwind 4, bits-ui, ECharts and svelte-i18n stay.
- Why: on 2026-11-16 the web npm audit failed on GHSA-3r8g-jcqq-3p5j (High, CWE-407: a denial of service while SvelteKit parses a remote functions form), which has no fix on the 2.x line, with five Moderate advisories in SvelteKit, adapter-static and Vite beside it. The static build runs no SvelteKit server code in production, so the server-side advisories do not reach a deployment; the dev and preview servers do run it, and the gate refuses a High whatever its reach.
- Effect on the decision: the framework stays the one Aman.Web uses, but not the same major version until Aman.Web moves (it is on SvelteKit 2). Components port unchanged: both run Svelte 5, and Ariva declares `$lib` as an alias, which SvelteKit 3 no longer provides. What differs is configuration: SvelteKit 3 takes its options in the Vite plugin (`vite.config.ts`) and refuses a `svelte.config.js`, and `goto` takes `replace` and `reset` in place of `replaceState`, `noScroll` and `keepFocus`.
- Alternative: stay on SvelteKit 2.70.4 with Vite 6.4.6 and adapter-static 3.0.11 (which fix the Moderate advisories) and approve an exception for GHSA-3r8g-jcqq-3p5j until Aman.Web and bits-ui move; the gate fails on every story until that exception is approved.
