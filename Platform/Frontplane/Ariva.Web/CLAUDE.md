# Ariva.Web rules

SvelteKit 2 with Svelte 5 runes, TypeScript, Tailwind 4, bits-ui with shadcn-svelte components (same toolchain as AMAN.Web), ECharts for charts, svelte-i18n for English and Arabic. Load the `svelte-ui` skill for patterns.

- **Look:** Aman.Web's design system (tokens, DM Sans, sidebar and header measurements, page pattern), so Ariva and AMAN read as one product family; the `svelte-ui` skill lists the exact tokens and classes. **Behaviour and content:** `docs/design/prototype/app/` stays the reference for screens, roles, wording and the demo day; do not copy its plain JavaScript or its dark control-room palette.
- **MCP first:** use the `svelte` MCP for Svelte 5 and SvelteKit APIs and run `svelte-autofixer` on every component you write; use the `shadcn-svelte` MCP to pick and install components (`npx shadcn-svelte@latest add <component>`), and Bits UI docs for primitives.
- **Runes only:** `$state`, `$derived`, `$effect`, `$props`. No legacy stores for component state.
- **API access** only through `src/lib/core/Api.ts` with relative paths from `Endpoints.ts`; the AMAN `Result<T>` shape (`hasErrors`, `errorMessages`, `data`). Never fetch a URL taken from the page or user input (CWE-918).
- **Live data** through the SignalR client with `accessTokenFactory`; access tokens live in memory only, never in `localStorage` or `sessionStorage` (CWE-384). The refresh token is an HttpOnly cookie handled by the server.
- **XSS (CWE-79):** text interpolation only; `{@html}`, `innerHTML` and `eval` are banned by the security gate. The CSP is strict (`csp.config.js`); do not add inline scripts or `unsafe-inline` for scripts.
- **RTL:** use logical CSS properties (`ms-`, `me-`, `ps-`, `pe-`, `start`, `end`), set `dir` and `lang` from the active locale, and test Arabic layouts.
- **Accessibility:** semantic headings, labelled controls, keyboard paths, visible focus, charts with titles and text alternatives.
- **Role-aware UI** hides what a role cannot see, but the server is the authority; never rely on hidden UI for security.
- **Tests:** every screen gets Playwright functional tests in `Platform/Testing/Ariva.E2E/tests/functional` (happy path per role, role hiding, RTL, no console errors, no CSP violations, XSS probes on every input that is rendered back).
- Gates: `node scripts/verify.mjs web` and `node scripts/verify.mjs e2e`.
