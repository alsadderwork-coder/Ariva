import { expect, smoke } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only. Declared @read, but its title carries @write, which Playwright
// takes as a second tag: a test of two kinds is refused everywhere, before the run starts.

smoke('the probe that says @write in its title', { tag: '@read' }, async () => {
	expect(true).toBe(true);
});
