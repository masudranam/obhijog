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
 *   4. `git reset --hard` — the one operation here that destroys work outright
 *   5. deleting or moving a committed EF migration (SPEC §16.4, forward-only)
 *   6. `gh pr merge` unless a PASS verdict is recorded for the CURRENT head SHA
 *
 * Rule 5 says *deleting or moving*, not editing: this hook runs on `Bash|PowerShell`
 * only, so an Edit tool call — or `sed -i` against a migration — is not something it
 * can see. The header said "editing" for three milestones and was wrong.
 *
 * Deliberately NOT blocked, so the next reader does not assume otherwise:
 *   · `git rebase -i` — named in the same breath as the above in the user's own
 *     rules, but its failure here is a hang rather than data loss (this environment
 *     has no interactive editor). Tracked separately.
 *   · `git push origin :branch`, the colon refspec form of a delete.
 *   · `git clean -fd`, which destroys untracked files.
 *
 * Every rule matches only where `git` or `gh` is the command word — see `argvFor`.
 */
import { execFileSync } from 'node:child_process';
import {
  allow,
  block,
  commandOf,
  readPayload,
  readJsonIfExists,
  argvFor,
  hasShortFlag,
  refName,
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
  const argv = argvFor(part, 'git');
  if (!argv?.includes('commit')) continue;

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
  const argv = argvFor(part, 'git');
  if (!argv) continue;

  // By bare name or by qualified ref: `main` and `refs/heads/main` are one branch.
  const namesProtected = argv.some((a) => PROTECTED.has(refName(a)));

  if (argv.includes('push') && (argv.includes('--force') || hasShortFlag(argv, 'f'))) {
    block(
      `BLOCKED: force push.\n\n` +
        `Force pushing rewrites published history and can destroy a branch another\n` +
        `process is reviewing. If a branch genuinely needs rewriting, use\n` +
        `--force-with-lease and say why in the PR.`,
    );
  }

  if (argv.includes('push') && argv.includes('--delete') && namesProtected) {
    block('BLOCKED: deleting the main branch on the remote.');
  }

  if (argv.includes('branch') && hasShortFlag(argv, 'D') && namesProtected) {
    block('BLOCKED: deleting the local main branch.');
  }

  if (argv.includes('--no-verify') || argv.includes('--no-gpg-sign')) {
    block(
      `BLOCKED: skipping git hooks.\n\n` +
        `If a hook is failing, that is information — fix the cause. SPEC.md §21.10.`,
    );
  }

  // `reset --hard` is the only command reached for routinely that throws work away
  // with no way back: uncommitted changes are gone, not stashed, and no reflog entry
  // brings them back. The three rules above are all about published history, which
  // is recoverable; this one is not. It cost this very PR a working tree once.
  //
  // Blocked unconditionally rather than only on a dirty tree. A guard whose answer
  // depends on mutable state is one the agent cannot predict and a reader cannot
  // reason about, and the dirty-tree test would miss the other half of what a hard
  // reset discards — the commits it moves the branch off.
  if (argv.includes('reset') && argv.includes('--hard')) {
    block(
      `BLOCKED: git reset --hard.\n\n` +
        `This discards every uncommitted change in the working tree with no way back —\n` +
        `no stash, no reflog entry, nothing to recover from. Depending on what you meant:\n\n` +
        `  git restore <path>          discard one file's changes\n` +
        `  git stash                   set everything aside, recoverably\n` +
        `  git reset --keep <commit>   move the branch, keep local modifications\n` +
        `  git reset --soft <commit>   move the branch, keep everything staged\n\n` +
        `If a hard reset is genuinely what is wanted, say so and ask — that is the one\n` +
        `thing this guard is here to make you do.`,
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
  const argv = argvFor(part, 'gh');
  if (!argv?.includes('pr') || !argv.includes('merge')) continue;

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
