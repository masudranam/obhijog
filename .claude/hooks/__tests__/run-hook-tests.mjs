#!/usr/bin/env node
/**
 * Self-test for guard-git.mjs.
 *
 * These guards are the only thing enforcing the workflow across a long unattended
 * run, and they are the one piece of the repo whose failure mode is silent: a guard
 * that stops working simply allows everything, and nothing looks wrong.
 *
 * This suite exists because exactly that happened — a BOM on the payload made
 * `JSON.parse` throw, the payload became `{}`, and every guard allowed every
 * command. It was found by accident. Hence the tests.
 *
 * NOT part of the four application suites in SPEC §18; this is harness
 * infrastructure and runs in the `harness` CI job.
 *
 * Usage: node .claude/hooks/__tests__/run-hook-tests.mjs
 */
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const GUARD = resolve(here, '..', 'guard-git.mjs');

const BLOCK = 2;
const ALLOW = 0;

let passed = 0;
const failures = [];

/** Run the guard with a raw stdin string in a given fake project dir. */
function runRaw(stdin, projectDir) {
  const result = spawnSync(process.execPath, [GUARD], {
    input: stdin,
    cwd: projectDir,
    env: { ...process.env, CLAUDE_PROJECT_DIR: projectDir },
    encoding: 'utf8',
  });
  return { code: result.status, stderr: result.stderr ?? '' };
}

function run(command, projectDir) {
  return runRaw(JSON.stringify({ tool_input: { command } }), projectDir);
}

function check(name, actual, expected, stderr) {
  if (actual === expected) {
    passed++;
    return;
  }
  failures.push(
    `${name}\n    expected exit ${expected}, got ${actual}` +
      (stderr ? `\n    stderr: ${stderr.split('\n')[0]}` : '\n    stderr: (empty)'),
  );
}

// ---------------------------------------------------------------- fixtures

function git(args, cwd) {
  execFileSync('git', args, { cwd, stdio: ['ignore', 'ignore', 'ignore'] });
}

/** A throwaway repo on `main` with an origin remote, so the "no commits on main" rule applies. */
function makeRepo({ branch, withOrigin }) {
  const dir = mkdtempSync(join(tmpdir(), 'guard-test-'));
  git(['init', '-b', 'main'], dir);
  git(['config', 'user.email', 'test@example.test'], dir);
  git(['config', 'user.name', 'Guard Test'], dir);
  writeFileSync(join(dir, 'f.txt'), 'x', 'utf8');
  git(['add', '.'], dir);
  git(['-c', 'commit.gpgsign=false', 'commit', '-m', 'init'], dir);
  if (withOrigin) git(['remote', 'add', 'origin', 'https://example.test/r.git'], dir);
  if (branch && branch !== 'main') git(['checkout', '-b', branch], dir);
  mkdirSync(join(dir, '.claude', 'state'), { recursive: true });
  return dir;
}

function headSha(dir) {
  return execFileSync('git', ['rev-parse', 'HEAD'], { cwd: dir, encoding: 'utf8' }).trim();
}

function writeVerdict(dir, pr, verdict, sha) {
  writeFileSync(
    join(dir, '.claude', 'state', `review-${pr}.json`),
    JSON.stringify({ pr, verdict, headSha: sha, summary: 'test' }),
    'utf8',
  );
}

const repos = [];
function repo(opts) {
  const d = makeRepo(opts);
  repos.push(d);
  return d;
}

// ---------------------------------------------------------------- the tests

const mainPublished = repo({ branch: 'main', withOrigin: true });
const mainBootstrap = repo({ branch: 'main', withOrigin: false });
const feature = repo({ branch: 'feat/1-thing', withOrigin: true });

// 1 · payload handling — the bug this suite was written for
//
// The first assertion deliberately uses a HARMLESS command and expects ALLOW. An
// earlier version used a force push and expected BLOCK, which passed even with the
// BOM strip removed: an unparseable payload also blocks, so "expect BLOCK" could not
// tell "understood the command" from "choked on the input". Mutation testing caught
// it. Expecting ALLOW is the only assertion that proves the payload was actually read.
{
  const bomAllow = '﻿' + JSON.stringify({ tool_input: { command: 'dotnet build' } });
  const r = runRaw(bomAllow, feature);
  check('a BOM-prefixed payload is parsed, not choked on', r.code, ALLOW, r.stderr);
}
{
  const bomBlock = '﻿' + JSON.stringify({ tool_input: { command: 'git push --force' } });
  const r = runRaw(bomBlock, feature);
  check('a BOM-prefixed payload is still judged on its content', r.code, BLOCK, r.stderr);
}
{
  const r = runRaw('this is not json at all', feature);
  check('an unparseable payload fails CLOSED', r.code, BLOCK, r.stderr);
}
{
  const r = runRaw('', feature);
  check('an empty payload is allowed (no command to judge)', r.code, ALLOW, r.stderr);
}

// 2 · commits on main
{
  const r = run('git commit -m "x"', mainPublished);
  check('commit on main is blocked once origin exists', r.code, BLOCK, r.stderr);
}
{
  const r = run('git commit -m "x"', mainBootstrap);
  check('commit on main is allowed before the repo is published', r.code, ALLOW, r.stderr);
}
{
  const r = run('git commit -m "x"', feature);
  check('commit on a feature branch is allowed', r.code, ALLOW, r.stderr);
}

// 3 · destructive git
{
  const r = run('git push --force origin feat/1-thing', feature);
  check('force push is blocked', r.code, BLOCK, r.stderr);
}
{
  const r = run('git push --force-with-lease origin feat/1-thing', feature);
  check('force-with-lease is allowed', r.code, ALLOW, r.stderr);
}
{
  const r = run('git commit --no-verify -m "x"', feature);
  check('--no-verify is blocked', r.code, BLOCK, r.stderr);
}
{
  const r = run('git push --delete origin main', feature);
  check('deleting remote main is blocked', r.code, BLOCK, r.stderr);
}
{
  const r = run('git commit -m "remember: never use --force here"', feature);
  check('a quoted mention of --force is not mistaken for one', r.code, ALLOW, r.stderr);
}

// 4 · migrations are forward-only
{
  const r = run('rm src/MunicipalSla.Infrastructure/Migrations/20260101_Init.cs', feature);
  check('deleting a migration is blocked', r.code, BLOCK, r.stderr);
}
{
  const r = run('cat src/MunicipalSla.Infrastructure/Migrations/20260101_Init.cs', feature);
  check('reading a migration is allowed', r.code, ALLOW, r.stderr);
}

// 5 · the merge gate
{
  const r = run('gh pr merge 7 --squash --delete-branch', feature);
  check('merge with no recorded verdict is blocked', r.code, BLOCK, r.stderr);
}
{
  const r = run('gh pr merge --squash', feature);
  check('merge without an explicit PR number is blocked', r.code, BLOCK, r.stderr);
}
{
  writeVerdict(feature, 7, 'FAIL', headSha(feature));
  const r = run('gh pr merge 7 --squash', feature);
  check('merge on a FAIL verdict is blocked', r.code, BLOCK, r.stderr);
}
{
  writeVerdict(feature, 7, 'PASS', 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef');
  const r = run('gh pr merge 7 --squash', feature);
  check('merge on a PASS verdict for a DIFFERENT sha is blocked', r.code, BLOCK, r.stderr);
}
{
  writeVerdict(feature, 7, 'PASS', headSha(feature));
  const r = run('gh pr merge 7 --squash --delete-branch', feature);
  check('merge on a PASS verdict matching HEAD is allowed', r.code, ALLOW, r.stderr);
}

// 6 · unrelated commands are not the guard's business
{
  const r = run('dotnet build --configuration Release', feature);
  check('dotnet build is allowed', r.code, ALLOW, r.stderr);
}
{
  const r = run('npm run build --prefix web', feature);
  check('npm run build is allowed', r.code, ALLOW, r.stderr);
}

// ---------------------------------------------------------------- report

for (const d of repos) {
  try {
    rmSync(d, { recursive: true, force: true });
  } catch {
    /* a Windows file lock here must not fail the suite */
  }
}

const total = passed + failures.length;
if (failures.length) {
  console.error(`\nguard-git: ${passed}/${total} passed, ${failures.length} FAILED\n`);
  for (const f of failures) console.error(`  ✗ ${f}\n`);
  process.exit(1);
}

console.log(`guard-git: ${passed}/${total} passed`);
