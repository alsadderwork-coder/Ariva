import { expect, smoke, target } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only. A raw request past the smoke client: the structure rule refuses the
// file, so playwright.smoke.config.ts refuses to load and nothing runs.

smoke('a raw request past the smoke client', { tag: '@read' }, async () => {
	expect((await fetch(`${target.base}/api/v1/sites`)).status).toBe(401);
});
