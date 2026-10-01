# Design reference

`prototype/app/` is the clickable prototype approved with the business plan (open `prototype/app/index.html` in a browser; it runs offline). It is the reference for screens, roles, wording, the dark control-room palette, the create flows and the demo behaviour. `prototype/app/assets/sim.js` is the reference implementation of the demo day (seed 9303) that `Ariva.Simulation.Api` ports to C#.

`prototype-spec.md` is the specification the prototype was built from (Part 2 describes every screen, the roles and their visibility rules, the scripted events and the create flows).

The prototype is synthetic and front-end only: it shows what to build, not how. Production code follows `CLAUDE.md`, the ADRs and the security controls.
