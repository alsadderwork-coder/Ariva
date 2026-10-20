import type { APIResponse } from '@playwright/test';
import { redactSecrets } from '../../scripts/secret-shapes.mjs';

const letters = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';

/** Three random capital letters, the shape of an IATA code. */
export const randomIata = (): string => Array.from({ length: 3 }, () => letters[Math.floor(Math.random() * 26)]).join('');

/**
 * Creates an airport under a random IATA code and draws another code when the one drawn is taken (409). A run creates
 * about 25 airports from 17,576 codes, beside the seeded ones, so two specs drawing the same code is a matter of time
 * (about one run in sixty); a spec must not fail on it. Any other answer fails at once with its body.
 * @param post sends the create request for one code and returns its answer (from `call`, a request context or the page)
 * @param attempts how many codes to try before giving up
 * @returns the new airport's id and the code it got
 */
export async function createAirport(post: (iataCode: string) => Promise<Pick<APIResponse, 'status' | 'json' | 'text'>>, attempts = 8): Promise<{ id: string; iataCode: string }> {
	for (let attempt = 0; attempt < attempts; attempt++) {
		const iataCode = randomIata();
		const response = await post(iataCode);
		if (response.status() === 201) return { id: (await response.json()).id as string, iataCode };
		if (response.status() !== 409) throw new Error(`airports: ${response.status()} ${redactSecrets(await response.text())}`);
	}
	throw new Error(`airports: ${attempts} random codes in a row were taken`);
}
