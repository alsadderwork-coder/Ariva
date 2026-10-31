import type { APIResponse } from '@playwright/test';
import { redactSecrets } from '../../scripts/secret-shapes.mjs';

const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';

/** An answer to a create request, from `call`, a request context or the page. */
type Created = Pick<APIResponse, 'status' | 'json' | 'text'>;

/** `n` random capital letters. */
export const randomLetters = (n: number): string => Array.from({ length: n }, () => letters[Math.floor(Math.random() * 26)]).join('');

/** Three random capital letters, the shape of an IATA code. */
export const randomIata = (): string => randomLetters(3);

/** The detail Ariva.Api.Main answers a duplicate airport with (409, `TopologyErrors.Duplicate`). */
export const airportTaken = 'A record with that code already exists here.';

/** The detail Ariva.Api.Main answers a site code in use with (409, `AdministrationErrors.SiteTaken`). */
export const siteTaken = 'That site code is already in use.';

/** Whether a 409 says the code is taken: its ProblemDetails detail is the endpoint's own text, never another conflict. */
async function says(answer: Created, detail: string): Promise<boolean> {
	try {
		return (await answer.json())?.detail === detail;
	} catch {
		return false;
	}
}

/**
 * Creates a record under a code from `draw` and draws another when the one drawn is taken (409 with the endpoint's
 * `taken` detail). The E2E database keeps what earlier runs created, and specs draw their airport and site codes at
 * random, so two of them drawing the same code is a matter of time; a spec must not fail on it (ARV-196). Any other
 * answer, another 409 included, fails at once with its body.
 * @param what the kind of record, for the error
 * @param draw a new code each time it is called
 * @param post sends the create request for one code and returns its answer
 * @param taken the 409 detail that means the code is taken
 * @param attempts how many codes to try before giving up
 * @returns the code the record got and the 201 answer
 */
export async function createUnderFreshCode(
	what: string,
	draw: () => string,
	post: (code: string) => Promise<Created>,
	taken: string,
	attempts = 8
): Promise<{ code: string; answer: Created }> {
	for (let attempt = 0; attempt < attempts; attempt++) {
		const code = draw();
		const response = await post(code);
		if (response.status() === 201) return { code, answer: response };
		if (response.status() !== 409 || !(await says(response, taken)))
			throw new Error(`${what}: ${response.status()} ${redactSecrets(await response.text())}`);
	}
	throw new Error(`${what}: ${attempts} codes in a row were taken`);
}

/**
 * A code from `draw` that no record holds yet, drawn again while `exists` says it is taken: for a spec that sends
 * refused requests under the code before it creates the record, so the helpers above do not fit (ARV-196).
 * @param exists whether a record holds the code; it throws on an answer that says neither
 */
export async function unusedCode(what: string, draw: () => string, exists: (code: string) => Promise<boolean>, attempts = 8): Promise<string> {
	for (let attempt = 0; attempt < attempts; attempt++) {
		const code = draw();
		if (!(await exists(code))) return code;
	}
	throw new Error(`${what}: ${attempts} codes in a row were taken`);
}

/**
 * Creates an airport under a random IATA code, drawing again when the code is taken. A run creates about 25 airports from
 * 17,576 codes, beside the seeded ones and those of earlier runs.
 * @returns the new airport's id and the code it got
 */
export async function createAirport(post: (iataCode: string) => Promise<Created>, attempts = 8): Promise<{ id: string; iataCode: string }> {
	const { code, answer } = await createUnderFreshCode('airports', randomIata, post, airportTaken, attempts);
	return { id: (await answer.json()).id as string, iataCode: code };
}

/**
 * Creates a site under a code from `draw` (a spec's prefix and random letters, at most 8 characters before any dash),
 * drawing again when the code is taken (ARV-196). A spec that derives other codes from the random part draws it inside
 * `draw`, so they follow the code the site got.
 * @returns the code the site got
 */
export async function createSite(draw: () => string, post: (code: string) => Promise<Created>, attempts = 8): Promise<string> {
	return (await createUnderFreshCode('sites', draw, post, siteTaken, attempts)).code;
}
