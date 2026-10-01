// Shared helpers for Ariva's Claude Code hooks. Node 20+, no dependencies.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

export const PROJECT_DIR = path.resolve(process.env.CLAUDE_PROJECT_DIR || process.cwd());

export async function readInput() {
  const chunks = [];
  for await (const c of process.stdin) chunks.push(c);
  const raw = Buffer.concat(chunks).toString('utf8').trim();
  try { return raw ? JSON.parse(raw) : {}; } catch { return {}; }
}

// PreToolUse: deny with a reason the agent sees.
export function deny(reason) {
  process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'deny', permissionDecisionReason: reason } }));
  process.exit(0);
}

// PreToolUse: ask the human.
export function ask(reason) {
  process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PreToolUse', permissionDecision: 'ask', permissionDecisionReason: reason } }));
  process.exit(0);
}

// PostToolUse or Stop: block and feed the reason back to the agent.
export function block(reason, event = 'PostToolUse') {
  const out = { decision: 'block', reason };
  if (event === 'PostToolUse') out.hookSpecificOutput = { hookEventName: 'PostToolUse', additionalContext: reason };
  process.stdout.write(JSON.stringify(out));
  process.exit(0);
}

export function context(text, event) {
  process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: event, additionalContext: text } }));
  process.exit(0);
}

export function resolveInProject(p) {
  if (!p) return null;
  return path.isAbsolute(p) ? path.resolve(p) : path.resolve(PROJECT_DIR, p);
}

export function isInside(child, parent) {
  const rel = path.relative(parent, child);
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

export function isTempOrClaudeHome(p) {
  const candidates = [os.tmpdir(), path.join(os.homedir(), '.claude')];
  return candidates.some(c => isInside(p, path.resolve(c)));
}

export const AMAN_DIR = path.resolve(PROJECT_DIR, '..', 'Aman');

export function readJson(p, fallback = null) {
  try { return JSON.parse(fs.readFileSync(p, 'utf8').replace(/^﻿/, '')); } catch { return fallback; }
}
