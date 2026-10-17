import { expect, smoke, target } from '../support/smoke';
import { credential, credentialVariables } from '../support/target';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only, run against a local target with a made-up password. It writes
// the password into the report on purpose (an annotation), which the artifact check must find: the run fails and names
// the variable, never the value. Against a non-local target it skips, so a real password never reaches a report.

smoke('a credential that reaches the report fails the run', { tag: '@read' }, async ({}, testInfo) => {
	// Never with a real credential: against a non-local target this probe does nothing.
	testInfo.skip(!target.local, 'the leak probe runs against a local target only');
	testInfo.annotations.push({ type: 'probe', description: `leaked ${credential(credentialVariables.password)}` });
	expect(true).toBe(true);
});
