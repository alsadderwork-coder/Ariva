# Design reference

`prototype/app/` is the clickable prototype approved with the business plan (open `prototype/app/index.html` in a browser; it runs offline). It is the reference for screens, roles, wording, the create flows and the demo behaviour.

Visual design (decided 2026-10-01): Ariva.Web uses Aman.Web's design system, not the prototype's dark control-room palette, so both products look like one family. Tokens, type, layout measurements and page patterns are in `Platform/Frontplane/Ariva.Web/src/app.css` and the `svelte-ui` skill (`.claude/skills/svelte-ui/SKILL.md`). Differences from Aman.Web, on purpose: an Arabic typeface (Noto Sans Arabic), a theme script loaded as a file because of the strict CSP, readable button colour in dark mode, darker muted and tertiary text (Aman.Web's values fail WCAG AA contrast on its grey surfaces; worth fixing in Aman.Web too), Lucide icons until Aman's icon font is packaged. `prototype/app/assets/sim.js` is the reference implementation of the demo day (seed 9303) that `Ariva.Simulation.Api` ports to C#.

`prototype-spec.md` is the specification the prototype was built from (Part 2 describes every screen, the roles and their visibility rules, the scripted events and the create flows).

The prototype is synthetic and front-end only: it shows what to build, not how. Production code follows `CLAUDE.md`, the ADRs and the security controls.
