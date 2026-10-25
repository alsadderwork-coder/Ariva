// Types of scripts/raw-http.mjs for the specs and support files (ARV-164b).
/** The answer to an announced request, readable as the other helpers read an answer. */
export interface AnnouncedAnswer {
	status(): number;
	headers(): { [key: string]: string };
	text(): Promise<string>;
	json(): Promise<any>;
	url(): string;
}
export interface Announcement {
	/** The Content-Length announced (a whole number of bytes); no byte of the body is sent. */
	length: number;
	/** Token names, values without a line break; never Host, Content-Length or Transfer-Encoding, which the client writes. */
	headers?: Record<string, string>;
	/** How long the answer may take (the server answers from the head alone); 15 s unless given. */
	timeoutMs?: number;
}
/** Sends the request head only and resolves with the whole final answer; rejects on a close, a reset or no answer in time. */
export function announced(method: string, url: string, announcement: Announcement): Promise<AnnouncedAnswer>;
