#!/usr/bin/env node
/**
 * PreToolUse · Bash | PowerShell
 *
 * The enforcement half of the merge gate. Prompt-level instructions get forgotten
 * across a ten-issue unattended run; this does not.
 *
 * Blocks:
 *   1. committing while on main/master — every change arrives by PR (SPEC §21.2)
 *   2. force pushes, and deletion of main
 *   3. skipping hooks with --no-verify
 *   4. editing or deleting a committed EF migration (SPEC §16.4, forward-only)
 *   5. `gh pr merge` unless a PASS verdict is recorded for the CURRENT head SHA
 */
import { execFileSync } from 'node:child_process';
import {
  allow,
  block,
  commandOf,
  readPayload,
  readJsonIfExists,
  segments,
  statePath,
} from './_lib.mjs';

const payload = await readPayload();

// Fail closed. There was input but it did not parse, so we cannot see what is about
// to run — and a guard that allows what it cannot read is not a guard.
if (payload.unparseable !== undefined) {
  block(
    `BLOCKED: guard-git could not parse its hook payload, so it cannot tell what this\n` +
      `command does.\n\n` +
      `First 200 characters received:\n  ${payload.unparseable}\n\n` +
      `This guard fails closed on purpose. Fix the payload — or if the hook wiring in\n` +
      `.claude/settings.json is wrong, fix that; do not disable the guard to get past it.`,
  );
}

const command = commandOf(payload);

// No stdin at all is a legitimate invocation shape, and carries no command to judge.
if (!command.trim()) allow();

const parts = segments(command);

function git(args) {
  try {
    return execFileSync('git', args, {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    }).trim();
  } catch {
    return '';
  }
}

// ---------------------------------------------------------------- 1 · no commits on main

const PROTECTED = new Set(['main', 'master']);

for (const part of parts) {
  if (!/\bgit\b[\s\S]*\bcommit\b/.test(part)) continue;

  const branch = git(['rev-parse', '--abbrev-ref', 'HEAD']);
  if (!PROTECTED.has(branch)) continue;

  // Bootstrap: before the repository is published there is no PR workflow to route a
  // change through, so building the initial history on main is legitimate. The moment
  // an 'origin' remote exists, main is closed and everything goes through review.
  const published = git(['remote', 'get-url', 'origin']) !== '';
  if (!published) continue;

  block(
    `BLOCKED: commit on '${branch}'.\n\n` +
      `Every change reaches main through a reviewed pull request (SPEC.md §21.2).\n` +
      `Create a feature branch first, as its own command:\n\n` +
      `  git checkout -b feat/<issue-number>-<slug>\n\n` +
      `Then commit, push, open a PR, and let pr-reviewer gate the merge.`,
  );
}

// ---------------------------------------------------------------- 2 · destructive git

for (const part of parts) {
  if (
    /\bgit\b[\s\S]*\bpush\b/.test(part) &&
    /(--force(?!-with-lease)|(?:^|\s)-f(?:\s|$))/.test(part)
  ) {
    block(
      `BLOCKED: force push.\n\n` +
        `Force pushing rewrites published history and can destroy a branch another\n` +
        `process is reviewing. If a branch genuinely needs rewriting, use\n` +
        `--force-with-lease and say why in the PR.`,
    );
  }

  if (/\bgit\b[\s\S]*\bpush\b[\s\S]*--delete[\s\S]*\b(main|master)\b/.test(part)) {
    block('BLOCKED: deleting the main branch on the remote.');
  }

  if (/\bgit\b[\s\S]*\bbranch\b[\s\S]*-D[\s\S]*\b(main|master)\b/.test(part)) {
    block('BLOCKED: deleting the local main branch.');
  }

  if (/\bgit\b[\s\S]*(--no-verify|--no-gpg-sign)\b/.test(part)) {
    block(
      `BLOCKED: skipping git hooks.\n\n` +
        `If a hook is failing, that is information — fix the cause. SPEC.md §21.10.`,
    );
  }
}

// ------------------------------------------------- 3 · migrations are forward-only

const MIGRATIONS = /src\/Obhijog\.Infrastructure\/Migrations\//i;

for (const part of parts) {
  const touchesMigrations = MIGRATIONS.test(part.replace(/\\/g, '/'));
  if (!touchesMigrations) continue;

  if (/\b(rm|del|Remove-Item|mv|Move-Item)\b/.test(part)) {
    block(
      `BLOCKED: deleting or moving a file under Migrations/.\n\n` +
        `Migrations are forward-only once committed (SPEC.md §16.4). Fix a bad\n` +
        `migration by adding another one:\n\n` +
        `  dotnet ef migrations add <Name> \\\n` +
        `    --project src/Obhijog.Infrastructure \\\n` +
        `    --startup-project src/Obhijog.Api\n\n` +
        `'dotnet ef migrations remove' is only legitimate for a migration that has\n` +
        `never been committed — if that is the case, say so and do it deliberately.`,
    );
  }
}

// ---------------------------------------------------------------- 4 · the merge gate

for (const part of parts) {
  if (!/\bgh\b[\s\S]*\bpr\b[\s\S]*\bmerge\b/.test(part)) continue;

  // Require an explicit PR number. Without one we cannot tell which verdict to check,
  // and a gate that guesses is not a gate.
  const prMatch = part.match(/\bpr\s+merge\s+(?:--?[\w-]+(?:[= ][^\s-]\S*)?\s+)*?(\d+)\b/);
  if (!prMatch) {
    block(
      `BLOCKED: 'gh pr merge' without an explicit PR number.\n\n` +
        `The merge gate looks up the recorded review verdict by PR number, so it must\n` +
        `be given one:  gh pr merge 4 --squash --delete-branch`,
    );
  }

  const pr = prMatch[1];
  const verdict = readJsonIfExists(statePath(`review-${pr}.json`));

  if (!verdict) {
    block(
      `BLOCKED: no review verdict recorded for PR #${pr}.\n\n` +
        `Expected: .claude/state/review-${pr}.json\n\n` +
        `Run the pr-reviewer subagent on this PR first. If it returns PASS, record it\n` +
        `with .claude/bin/record-verdict.mjs and this merge will be allowed.\n` +
        `Do not write the verdict file by hand — that defeats the gate.`,
    );
  }

  if (verdict.verdict !== 'PASS') {
    block(
      `BLOCKED: PR #${pr} has verdict '${verdict.verdict}', not PASS.\n\n` +
        (verdict.summary ? `Reviewer said: ${verdict.summary}\n\n` : '') +
        `Address the findings, push a fix, and re-run pr-reviewer.`,
    );
  }

  // Fail closed. If we cannot establish what is about to be merged, or the verdict
  // carries no SHA, there is no basis for trusting it — refuse rather than wave it
  // through. An unverifiable gate that allows is not a gate.
  const headSha = git(['rev-parse', 'HEAD']);

  if (!headSha) {
    block(
      `BLOCKED: cannot determine the current HEAD, so PR #${pr}'s verdict cannot be\n` +
        `validated against it.\n\n` +
        `The gate refuses to merge when it cannot verify what it is merging. Check that\n` +
        `this is a git repository with at least one commit and that 'git' is on PATH.`,
    );
  }

  if (!verdict.headSha) {
    block(
      `BLOCKED: the recorded verdict for PR #${pr} has no headSha.\n\n` +
        `A verdict without a commit cannot be checked for staleness. Re-run pr-reviewer\n` +
        `and record it with .claude/bin/record-verdict.mjs, which stamps the SHA itself.`,
    );
  }

  if (verdict.headSha !== headSha) {
    block(
      `BLOCKED: PR #${pr} moved since it was reviewed.\n\n` +
        `  reviewed: ${verdict.headSha}\n` +
        `  current:  ${headSha}\n\n` +
        `A verdict is only valid for the exact commit it was given on, otherwise a\n` +
        `passing review could be recycled to merge unreviewed code. Re-run pr-reviewer\n` +
        `on the current head.\n\n` +
        `(If you are on a different branch, check out the PR branch before merging —\n` +
        `the gate compares against local HEAD.)`,
    );
  }
}

allow();
