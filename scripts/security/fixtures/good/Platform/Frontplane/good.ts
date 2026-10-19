export const run = async () => fetch('/api/health');
// ARV-089: objects keyed by data without the __proto__ setter (SEC-131, SEC-132 stay quiet).
const byKey = new Map<string, unknown>();
export function keep(snapshot: { zoneKey: string }) { byKey.set(snapshot.zoneKey, snapshot); }
export const fromPairs = (pairs: [string, number][]) => Object.fromEntries(pairs);
export const spread = (old: Record<string, number>, key: string) => ({ ...old, [key]: 1 });
const headers: Record<string, string> = {};
headers['Content-Type'] = 'application/json';
const bytes = new Uint8Array(16);
bytes[6] = (bytes[6] & 0x0f) | 0x40;
export const pick = (body: Record<string, string>, recovery: boolean) => { body[recovery ? 'recoveryCode' : 'code'] = 'x'; };
export const same = (list: number[], i: number) => list[i] === 1 || list[i] == 2 || list[i] >= 3 || list[i] <= 4;
export const arrow = (list: Array<() => void>, i: number) => list[i];
let [first, second] = [1, 2];
[first, second] = [second, first];
export const counter = (list: number[]) => { let i = 0; i++; list.push(i); return list[0]; };
export const reads = (o: Record<string, { a: number }>, k: string) => o[k].a === 1 || o[k].a >= 2 || o[k].a != 3 || o[k].a <= 4;
export const literalTail = (o: { list: number[] }[]) => { o[0].list = []; };
