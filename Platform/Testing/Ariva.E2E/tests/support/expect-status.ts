import { expect } from '@playwright/test';
import { redactSecrets } from '../../scripts/secret-shapes.mjs';

// ARV-152 (CWE-532, ASVS V13.3.1): Playwright uses an expect's custom message as the step title even when the assertion
// passes, and the HTML report keeps every step title, so `expect(x.status(), await x.text())` copied each answer into the
// report (device credentials, display credentials, integration client secrets). These helpers check a status and put the
// answer into the message only when the status is wrong, with the credential shapes redacted, so a passing step's title
// holds no answer and a failing one still says what the server answered. `node scripts/expect-messages.mjs` (run by the
// security step of scripts/verify.mjs and by CI) fails when a spec passes an answer's body as an expect message again.

/** An answer from Playwright's request context or `call` (tests/support/accounts.ts), or a raw one (status and body). */
export type Answer = { status(): number; text(): Promise<string>; url?(): string } | { status: number; body: string };

/** How much of an answer a failure message quotes. */
const quoted = 2000;

/**
 * The text with every secret JSON member (plain or JSON-escaped, string, array or object valued), query or form value
 * (access_token=, password=, key=, ...) and credential shape (ardk_, ardp_, ics_, JWTs, bearer values, otpauth seeds,
 * temporary passwords, recovery codes) replaced: scripts/secret-shapes.mjs, the shapes the artifact check looks for, whose
 * self-test proves each is cleared (scripts/artifact-secrets.mjs --self-test).
 */
export { redactSecrets };

function isRaw(answer: Answer): answer is { status: number; body: string } {
	return typeof answer.status === 'number';
}

function statusOf(answer: Answer): number {
	return isRaw(answer) ? answer.status : answer.status();
}

/** The failure message: the caller's context, the status, the path (never the query) and the redacted answer. */
async function failureMessage(answer: Answer, status: number, message: string | undefined): Promise<string> {
	let body: string;
	if (isRaw(answer)) body = answer.body;
	else body = await answer.text().catch((error: unknown) => `(the answer could not be read: ${String(error)})`);
	let where = '';
	if (!isRaw(answer) && typeof answer.url === 'function') {
		try {
			where = ` from ${new URL(answer.url()).pathname}`;
		} catch {
			where = '';
		}
	}
	const text = redactSecrets(body);
	const shown = text.length > quoted ? `${text.slice(0, quoted)}... (${text.length} characters)` : text;
	return `${message ? `${message}: ` : ''}status ${status}${where}, answer: ${shown}`;
}

/**
 * Asserts the answer's status, as `expect(answer.status(), message).toBe(expected)` would; the answer's body joins the
 * message only when the status is wrong. `soft` makes it a soft assertion (expect.soft).
 */
export async function expectStatus(answer: Answer, expected: number, message?: string, { soft = false }: { soft?: boolean } = {}): Promise<void> {
	const assert = soft ? expect.soft : expect;
	const status = statusOf(answer);
	assert(status, status === expected ? message : await failureMessage(answer, status, message)).toBe(expected);
}

/**
 * Asserts the answer's status is one of several, as `expect(expected, message).toContain(answer.status())` would; the
 * answer's body joins the message only when the status is none of them.
 */
export async function expectStatusIn(answer: Answer, expected: readonly number[], message?: string, { soft = false }: { soft?: boolean } = {}): Promise<void> {
	const assert = soft ? expect.soft : expect;
	const status = statusOf(answer);
	assert(expected, expected.includes(status) ? message : await failureMessage(answer, status, message)).toContain(status);
}
