import { expect, type Page } from '@playwright/test';

/** What a page reported while a test ran. */
export interface PageGuards {
	consoleErrors: string[];
	dialogs: string[];
	pageErrors: string[];
	/** Content Security Policy violations seen by the page (securitypolicyviolation events). */
	cspViolations(): Promise<string[]>;
	/** Asserts no console error, uncaught error, dialog or CSP violation happened. */
	expectClean(): Promise<void>;
}

declare global {
	interface Window {
		__arivaCspViolations?: string[];
	}
}

/**
 * Starts recording console errors, uncaught page errors, dialogs (alert, confirm, prompt, which an XSS payload
 * would open) and Content Security Policy violations. Call it before the first navigation.
 */
export async function guardPage(page: Page): Promise<PageGuards> {
	const consoleErrors: string[] = [];
	const dialogs: string[] = [];
	const pageErrors: string[] = [];

	await page.addInitScript(() => {
		window.__arivaCspViolations = [];
		document.addEventListener('securitypolicyviolation', (event) => {
			window.__arivaCspViolations?.push(
				`${event.violatedDirective} blocked ${event.blockedURI || 'inline'} (${event.sourceFile}:${event.lineNumber})`
			);
		});
	});

	page.on('console', (message) => {
		if (message.type() === 'error' || /Content Security Policy/i.test(message.text())) {
			consoleErrors.push(message.text());
		}
	});
	page.on('pageerror', (error) => pageErrors.push(error.message));
	page.on('dialog', async (dialog) => {
		dialogs.push(`${dialog.type()}: ${dialog.message()}`);
		await dialog.dismiss();
	});

	const cspViolations = async (): Promise<string[]> =>
		page.evaluate(() => [...(window.__arivaCspViolations ?? [])]).catch(() => []);

	return {
		consoleErrors,
		dialogs,
		pageErrors,
		cspViolations,
		async expectClean() {
			expect(dialogs, 'dialogs opened by the page').toEqual([]);
			expect(pageErrors, 'uncaught page errors').toEqual([]);
			expect(consoleErrors, 'console errors and CSP messages').toEqual([]);
			expect(await cspViolations(), 'Content Security Policy violations').toEqual([]);
		}
	};
}
