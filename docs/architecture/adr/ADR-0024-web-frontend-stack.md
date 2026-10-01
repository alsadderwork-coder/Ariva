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
