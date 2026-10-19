import { exec } from 'child_process';
exec('ls -la');
const opts = { shell: true };
// ARV-089: prototype pollution (SEC-131, SEC-132).
const snapshots: Record<string, unknown> = {};
export function keep(snapshot: { zoneKey: string }) { snapshots[snapshot.zoneKey] = snapshot; }
export const merged = Object.assign({}, JSON.parse('{}'));
const proto = { __proto__: null };
const counts: Record<string, number> = {};
export function count(key: string) { counts[key] += 1; (counts[key] ??= 0); }
export function bump(ranges: Record<string, number>, key: string) { ranges[key]++; --ranges[key]; }
export function rename(words: Record<string, { title: string }>, language: string) { words[language].title = 'x'; }
export function first(rows: Record<string, number[]>, key: string) { rows[key][0] = 1; }
export function tally(stats: Record<string, { n: number }>, key: string) { stats[key].n++; }
export function halve(counts: Record<string, number>, key: string) { counts[key] %= 2; }
