import type { Result } from '$lib/core/Api';
import { isRetryable, newKey } from '$lib/core/validation';

/**
 * Unsent submissions of the observer tablet (ARV-104c), in memory only: nothing is written to web storage (an
 * offline-capable tablet would need an ADR, since tokens never go to storage). Each item gets a random Idempotency-Key
 * when it is created and keeps it for its own retries, so a retry after a lost answer returns the stored record instead
 * of counting twice. A request sent without a usable answer (network failure, timeout, 408, 429, 5xx) waits and is sent
 * again after a growing delay (10 s doubling to 5 minutes), at once on Send again now, and with the next send; any other
 * refusal, and a request refused before it left the browser, is final and shown with its reason.
 *
 * Each item belongs to the account that queued it (the token's subject). An item is sent only while that account is
 * signed in; otherwise it is dropped unsent. close() (the screen left, a sign-out, another account) stops the outbox for
 * good: it forgets every item, cancels the retry timer, and a send already in flight neither records its answer nor
 * sends anything after it, so one account's items never leave under another account's token (security review M1).
 */

export type Delivery = 'sending' | 'waiting' | 'refused';

export interface OutboxItem<P> {
	key: string;
	payload: P;
	/** The account (token subject) that queued the item; only that account sends it. */
	owner: string;
	state: Delivery;
	/** The last answer's text when the item is waiting or refused (shown as text, never markup). */
	message: string;
	/** The HTTP status of the last answer: 0 when none came, absent for a request refused before it was sent. */
	status?: number;
	attempts: number;
}

/** What a view of the outbox reads (the sender stays private, so an outbox of a wider payload fits). */
export interface OutboxView<P> {
	readonly items: OutboxItem<P>[];
	readonly unsent: number;
	readonly refused: OutboxItem<P>[];
	discard(key: string): void;
}

/** The first retry delay after a failure worth retrying, doubled per failure up to the longest. */
export const firstRetryMs = 10_000;
export const longestRetryMs = 5 * 60_000;

export class Outbox<P, R> implements OutboxView<P> {
	items = $state<OutboxItem<P>[]>([]);
	#busy = false;
	#closed = false;
	#failures = 0;
	#timer: ReturnType<typeof setTimeout> | undefined;
	#owner: () => string | null;
	#send: (payload: P, key: string, owner: string) => Promise<Result<R>>;
	#sent: (payload: P, data: R) => void;

	constructor(
		owner: () => string | null,
		send: (payload: P, key: string, owner: string) => Promise<Result<R>>,
		sent: (payload: P, data: R) => void
	) {
		this.#owner = owner;
		this.#send = send;
		this.#sent = sent;
	}

	get closed(): boolean {
		return this.#closed;
	}

	/** Items not yet recorded by the server and still worth sending. */
	get unsent(): number {
		return this.items.filter((i) => i.state !== 'refused').length;
	}

	get refused(): OutboxItem<P>[] {
		return this.items.filter((i) => i.state === 'refused');
	}

	/** Queues a new submission for the signed-in account with a new key and sends what is waiting. */
	add(payload: P): void {
		const owner = this.#owner();
		if (this.#closed || owner === null) return;
		this.items.push({ key: newKey(), payload, owner, state: 'waiting', message: '', attempts: 0 });
		void this.flush();
	}

	/** Sends every waiting item in order, one at a time; stops at the first failure worth retrying (the network is down). */
	async flush(): Promise<void> {
		if (this.#busy || this.#closed) return;
		this.#busy = true;
		clearTimeout(this.#timer);
		try {
			for (;;) {
				if (this.#closed) return;
				// Items of another account (or of nobody, after a sign-out) are never sent: drop them.
				const owner = this.#owner();
				if (this.items.some((i) => i.owner !== owner))
					this.items = this.items.filter((i) => i.owner === owner);
				const item = this.items.find((i) => i.state === 'waiting');
				if (!item || owner === null) return;
				item.state = 'sending';
				item.attempts++;
				const result = await this.#send(item.payload, item.key, item.owner);
				// Closed while the request was out: forget the answer and send nothing more.
				if (this.#closed) return;
				if (!result.hasErrors && result.data !== null) {
					this.#failures = 0;
					this.items = this.items.filter((i) => i.key !== item.key);
					this.#sent(item.payload, result.data);
					continue;
				}
				item.message = result.errorMessages.join(' ');
				item.status = result.status;
				if (isRetryable(result)) {
					item.state = 'waiting';
					this.#schedule();
					return;
				}
				item.state = 'refused';
			}
		} finally {
			this.#busy = false;
		}
	}

	/** Drops an item (a refusal the observer has read). */
	discard(key: string): void {
		this.items = this.items.filter((i) => i.key !== key);
	}

	/** Stops the outbox for good and forgets every item (the screen left, a sign-out or another account). */
	close(): void {
		this.#closed = true;
		clearTimeout(this.#timer);
		this.items = [];
	}

	#schedule(): void {
		clearTimeout(this.#timer);
		this.#failures++;
		const delay = Math.min(firstRetryMs * 2 ** (this.#failures - 1), longestRetryMs);
		this.#timer = setTimeout(() => void this.flush(), delay);
	}
}
