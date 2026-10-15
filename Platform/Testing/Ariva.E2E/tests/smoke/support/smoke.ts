import { expect, test as base, type Page, type TestInfo } from '@playwright/test';
import { guardPage, type PageGuards } from '../../support/browser-guards';
import { target } from './client';
import { refusal, requestAllowed, type SmokeTag } from './guard';

// ARV-128: the only test a smoke spec may use. `smoke(title, { tag }, body)` refuses, while Playwright collects the tests
// and so before any test starts, a test that names no kind or more than one, and any kind but @read against a non-local
// target (guard.ts). An automatic fixture checks the same at run time, and against a non-local target the browser sends
// only reads and the session calls (every other request is aborted before it leaves the browser).

/** Set by playwright.config.ts on the local E2E stack when it has no database, so nobody can sign in: the smoke specs skip. */
const unavailable = process.env.ARIVA_SMOKE_UNAVAILABLE;

interface SmokeFixtures {
	smokeGuard: void;
	/** Console errors, dialogs, uncaught errors and CSP violations of the page, recorded from before the first navigation. */
	guards: PageGuards;
}

const test = base.extend<SmokeFixtures>({
	smokeGuard: [
		async ({}, use, testInfo) => {
			const reason = refusal(testInfo.title, testInfo.tags, target.local);
			if (reason) throw new Error(reason);
			await use();
		},
		{ auto: true }
	],
	page: async ({ page }, use) => {
		if (!target.local) {
			await page.route('**/*', (route) => (requestAllowed(route.request().method(), route.request().url(), false) ? route.continue() : route.abort('blockedbyclient')));
		}
		await use(page);
	},
	guards: async ({ page }, use) => {
		await use(await guardPage(page));
	}
});

/**
 * What a smoke test gets: the guarded page and its guards, nothing else (security review recommendation 2). Playwright's
 * request, browser, context and playwright fixtures would reach the target past the smoke client's and the page's request
 * checks, so the type does not offer them and the wrapper does not pass them.
 */
export interface SmokeArgs {
	page: Page;
	guards: PageGuards;
}

/** Declares a smoke test of one kind; refused here, before the run starts, when the kind may not run against the target. */
export function smoke(title: string, details: { tag: SmokeTag }, body: (args: SmokeArgs, testInfo: TestInfo) => Promise<void>): void {
	// Playwright also takes a tag from each @word of the title, so those count here as at run time.
	const titleTags = title.split(/\s+/).filter((word) => /^@[\w-]+$/.test(word));
	const reason = refusal(title, [details.tag, ...titleTags], target.local);
	if (reason) throw new Error(reason);
	// Declared skipped, so a file's beforeAll (the sign-in) does not run either.
	if (unavailable !== undefined) test.skip(title, { tag: details.tag, annotation: { type: 'skip', description: unavailable } }, async () => {});
	// Only the page and its guards reach the body, whatever it destructures (every smoke test therefore gets a page).
	else test(title, { tag: details.tag }, async ({ page, guards }, testInfo) => body({ page, guards }, testInfo));
}

// No describe: its tags would reach the tests past the declaration check (only the run-time check would see them).
smoke.configure = test.describe.configure;
smoke.beforeAll = test.beforeAll;
smoke.afterAll = test.afterAll;

export { expect, target };
