#!/usr/bin/env node
/**
 * Record a pr-reviewer verdict, stamped with the current head SHA.
 *
 *   node .claude/bin/record-verdict.mjs --pr 4 --verdict PASS --summary "one line"
 *
 * guard-git.mjs reads the file this writes and refuses `gh pr merge` unless the
 * verdict is PASS *and* its headSha still matches local HEAD. The SHA is taken
 * from git here rather than accepted as an argument — a verdict you can point at
 * an arbitrary commit is not a gate.
 *
 * Do not hand-write .claude/state/review-<pr>.json. The whole value of the gate
 * is that the verdict was produced by a review that actually ran.
 */
import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

const projectDir = process.env.CLAUDE_PROJECT_DIR ?? process.cwd();

function arg(name) {
  const i = process.argv.indexOf(`--${name}`);
  return i !== -1 && process.argv[i + 1] ? process.argv[i + 1] : null;
}

function die(message) {
  console.error(message);
  process.exit(1);
}

const pr = arg('pr');
const verdict = arg('verdict');
const summary = arg('summary') ?? '';

if (!pr || !/^\d+$/.test(pr)) {
  die('Usage: record-verdict.mjs --pr <number> --verdict PASS|FAIL|BLOCKED [--summary "..."]');
}

const ALLOWED = new Set(['PASS', 'FAIL', 'BLOCKED']);
if (!verdict || !ALLOWED.has(verdict.toUpperCase())) {
  die(`--verdict must be one of ${[...ALLOWED].join(', ')} — got '${verdict}'`);
}

let headSha = '';
try {
  headSha = execFileSync('git', ['rev-parse', 'HEAD'], {
    encoding: 'utf8',
    cwd: projectDir,
    stdio: ['ignore', 'pipe', 'ignore'],
  }).trim();
} catch {
  die('Could not read HEAD. Record a verdict from inside the PR branch checkout.');
}

if (!headSha) die('git rev-parse HEAD returned nothing; refusing to record an unanchored verdict.');

const branch = execFileSync('git', ['rev-parse', '--abbrev-ref', 'HEAD'], {
  encoding: 'utf8',
  cwd: projectDir,
  stdio: ['ignore', 'pipe', 'ignore'],
}).trim();

const target = join(projectDir, '.claude', 'state', `review-${pr}.json`);
mkdirSync(dirname(target), { recursive: true });

const record = {
  pr: Number(pr),
  verdict: verdict.toUpperCase(),
  summary,
  headSha,
  branch,
  recordedAt: new Date().toISOString(),
};

writeFileSync(target, `${JSON.stringify(record, null, 2)}\n`, 'utf8');

console.log(`Recorded ${record.verdict} for PR #${pr} at ${headSha.slice(0, 8)} (${branch}).`);
if (record.verdict !== 'PASS') {
  console.log('This verdict does not open the merge gate. Fix, push, and re-review.');
}
