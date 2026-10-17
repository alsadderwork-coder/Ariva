import { expect, smoke } from '../support/smoke';

// ARV-128: a probe for tests/api/smoke-guard.spec.ts only. A smoke test gets the guarded page and its guards and nothing
// else: the type offers no request, browser, context or playwright fixture (each line below is a compile error, which
// `tsc --noEmit` checks through @ts-expect-error), and the wrapper passes none of them at run time either.

smoke('a smoke test gets the page and its guards only', { tag: '@read' }, async (args) => {
	// @ts-expect-error: no request fixture in a smoke test
	expect(args.request).toBeUndefined();
	// @ts-expect-error: no browser fixture in a smoke test
	expect(args.browser).toBeUndefined();
	// @ts-expect-error: no context fixture in a smoke test
	expect(args.context).toBeUndefined();
	// @ts-expect-error: no playwright fixture in a smoke test
	expect(args.playwright).toBeUndefined();
	expect(Object.keys(args).sort()).toEqual(['guards', 'page']);
});
