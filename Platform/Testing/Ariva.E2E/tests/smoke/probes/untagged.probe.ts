import { expect, smoke } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only. It names no smoke kind (the type is bypassed on purpose), so
// the guard refuses it everywhere, local or not, before the run starts.

smoke('the untagged probe', { tag: '@nothing' } as never, async () => {
	expect(true).toBe(true);
});
