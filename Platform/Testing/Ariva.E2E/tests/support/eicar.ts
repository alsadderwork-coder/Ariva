import { createHash } from 'node:crypto';

// ARV-092b: the EICAR anti-malware test file, 68 ASCII characters that every scanner reports as a test detection and that
// are harmless by design. Built at run time from two parts and never written out whole in a source file: a developer
// machine's antivirus would quarantine a file that holds the whole string. The first part holds one backslash.

const parts = ['X5O!P%@AP[4\\PZX54(P^)7CC)7}$', 'EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*'];

/** The length of the test file; a file of any other length is not the test file (an escaping slip, for example). */
const length = 68;

/** The signature name clamd gives the test file sent alone (the run's pinned clamav image and its signature snapshot). */
export const eicarSignature = 'Eicar-Test-Signature';

/** The test file's bytes, a new copy each call. */
export function eicar(): Buffer {
	const bytes = Buffer.from([parts[0], parts[1]].join(''), 'ascii');
	if (bytes.length !== length) throw new Error(`the EICAR test file must be ${length} bytes`);
	return bytes;
}

/** The SHA-256 of the test file, lower-case hexadecimal (as Ariva's audit summaries write it). */
export const eicarSha256 = createHash('sha256').update(eicar()).digest('hex');
