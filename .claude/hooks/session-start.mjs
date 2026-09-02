#!/usr/bin/env node
/**
 * SessionStart
 *
 * Prints where the build actually is, so a session does not have to guess or
 * re-derive it: branch, working-tree state, the roadmap line from SPEC.md, and
 * the next unblocked issue.
 *
 * Never fails the session. Anything unavailable (no origin, no gh auth, no
 * network) is reported as unknown rather than thrown.
 */
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { projectDir } from './_lib.mjs';

function run(cmd, args) {
  try {
    return execFileSync(cmd, args, {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
      cwd: projectDir,
      timeout: 15000,
    }).trim();
  } catch {
    return '';
  }
}

const out = [];
const say = (line = '') => out.push(line);

// ------------------------------------------------------------------ git state

const branch = run('git', ['rev-parse', '--abbrev-ref', 'HEAD']) || '(unknown)';
const dirty = run('git', ['status', '--porcelain']);
const published = run('git', ['remote', 'get-url', 'origin']) !== '';

say(`## Municipal SLA — session start`);
say();
say(`- branch: **${branch}**${branch === 'main' ? ' (protected — branch before committing)' : ''}`);
say(`- working tree: ${dirty ? `**dirty**, ${dirty.split('\n').length} path(s)` : 'clean'}`);
say(`- remote: ${published ? 'origin present' : '**no origin yet** — repo not published'}`);

if (dirty) {
  say();
  say('```');
  say(dirty.split('\n').slice(0, 20).join('\n'));
  say('```');
  say('If you did not make these edits, read the diff before doing anything else.');
}

// ------------------------------------------------------------------ roadmap

const specPath = join(projectDir, 'SPEC.md');
if (existsSync(specPath)) {
  const spec = readFileSync(specPath, 'utf8');
  const rows = [...spec.matchAll(/^\|\s*(M\d+)\s*\|\s*#(\d+)\s*\|\s*([^|]+?)\s*\|.*\|\s*([☐◐☑])\s*\|/gm)];
  if (rows.length) {
    const done = rows.filter((r) => r[4] === '☑').length;
    const active = rows.find((r) => r[4] === '◐');
    const next = rows.find((r) => r[4] === '☐');
    say();
    say(`- roadmap (SPEC §20): **${done}/${rows.length}** milestones done`);
    if (active) say(`- in progress: **${active[1]} — ${active[3]}** (issue #${active[2]})`);
    if (next) say(`- next milestone: **${next[1]} — ${next[3]}** (issue #${next[2]})`);
  }
}

// ------------------------------------------------------------------ issues & PRs

if (!published) {
  say();
  say('The repository has no origin remote, so there are no issues to work yet.');
  say('Publish it and file the milestone issues before running `/next`.');
} else {
  const issuesRaw = run('gh', [
    'issue',
    'list',
    '--state',
    'open',
    '--limit',
    '100',
    '--json',
    'number,title,body,labels',
  ]);

  if (!issuesRaw) {
    say();
    say('Could not read issues (`gh` unauthenticated, offline, or no issues). Run `/status`.');
  } else {
    let issues = [];
    try {
      issues = JSON.parse(issuesRaw);
    } catch {
      issues = [];
    }

    const openNumbers = new Set(issues.map((i) => i.number));

    // "Depends on: #2, #3" in the issue body drives ordering.
    const dependsOn = (issue) => {
      const line = /depends on:?\s*(.+)/i.exec(issue.body ?? '');
      if (!line) return [];
      return [...line[1].matchAll(/#(\d+)/g)].map((m) => Number(m[1]));
    };

    const unblocked = issues
      .filter((i) => dependsOn(i).every((d) => !openNumbers.has(d)))
      .sort((a, b) => a.number - b.number);

    say();
    say(`- open issues: **${issues.length}**`);

    if (unblocked.length) {
      const n = unblocked[0];
      const inProgress = (n.labels ?? []).some((l) => l.name === 'status:in-progress');
      say(
        `- next unblocked: **#${n.number} — ${n.title}**${inProgress ? ' _(already claimed)_' : ''}`,
      );
    } else if (issues.length) {
      say('- next unblocked: **none** — every open issue is waiting on another. Report this.');
    }

    const blocked = issues.length - unblocked.length;
    if (blocked > 0) say(`- blocked: ${blocked}`);
  }

  const prs = run('gh', ['pr', 'list', '--state', 'open', '--json', 'number,title,headRefName']);
  if (prs && prs !== '[]') {
    try {
      const list = JSON.parse(prs);
      say(
        `- open PRs: ${list.map((p) => `#${p.number} (${p.headRefName})`).join(', ') || 'none'}`,
      );
    } catch {
      /* ignore */
    }
  }
}

say();
say('`/next` runs one issue from pick to merge. `/status` reports the full picture.');
say('SPEC.md is authoritative — read the relevant section rather than inferring the design.');

console.log(out.join('\n'));
process.exit(0);
