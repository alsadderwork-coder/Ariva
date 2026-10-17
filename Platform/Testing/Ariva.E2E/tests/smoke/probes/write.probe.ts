import { expect, smoke } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only (loaded when ARIVA_SMOKE_PROBES names it). It is declared as a
// @write test and touches nothing: against a non-local target the guard refuses it before the run starts; against a
// local one it runs and passes.

smoke('the write probe', { tag: '@write' }, async () => {
	expect(true).toBe(true);
});
