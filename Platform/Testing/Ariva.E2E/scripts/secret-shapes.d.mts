// Types of scripts/secret-shapes.mjs for the specs and support files (ARV-152).
export interface SecretShape {
	kind: string;
	re: RegExp;
	accept?: (match: string) => boolean;
}

export const SECRET_MEMBERS: readonly string[];
export const SHAPES: readonly SecretShape[];
export function shapeMatches(shape: SecretShape, text: string): boolean;
/** The text with every secret JSON member, query or form value and credential shape replaced. */
export function redactSecrets(text: string): string;
export const REDACTION_SAMPLES: { cleared: readonly string[]; kept: readonly string[] };
