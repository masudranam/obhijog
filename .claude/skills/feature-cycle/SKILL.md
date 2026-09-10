---
name: feature-cycle
description: Runs one complete milestone from GitHub issue to merged PR — pick, claim, branch, implement, test, CI, PR, review, gate, merge, advance. Use for "do the next milestone", "/next", "work issue #4", or any request to advance the build.
---

# Feature cycle

One issue, start to merge. This is the loop the whole project runs on.

Never skip a step because the change looks small. Each step exists because it catches something.

## 0 · Orient

Read the milestone's row in [SPEC.md §20](../../../SPEC.md#20-delivery-roadmap) and the `F*` feature
sections it names in §14. **The acceptance criteria in SPEC.md are the requirements** — the issue
body is a convenience copy. If the two disagree, SPEC.md wins and the issue gets corrected.

Read the non-negotiables in [CLAUDE.md](../../../CLAUDE.md) and the matching
[`.claude/rules/`](../../rules/) file for the area you are about to touch.

## 1 · Pick

```
gh issue list --state open --json number,title,labels,body --limit 100
```

Choose the **lowest-numbered open issue whose `Depends on` issues are all closed**. If nothing is
unblocked, say so and stop — do not start a blocked issue.

If a specific issue was named, use it, but check its dependencies and warn if any are still open.

## 2 · Claim

```
gh issue edit <n> --add-label "status:in-progress"
```

Post a short comment on the issue saying what you are about to build. If an acceptance criterion is
impossible, contradictory, or disagrees with SPEC.md, **say so on the issue now**, before writing
code.

## 3 · Branch

```
git checkout main
git pull --ff-only
git checkout -b feat/<n>-<slug>
```

Always branch from fresh `main`. **Run `git checkout -b` as its own command** — the guard hook reads
the current branch *before* the command runs, so chaining it with a commit reads as a commit on
`main` and is blocked.

## 4 · Implement and test

Write it directly. There are no implementation subagents — handing the work to one costs a full
context hand-off and a re-read of the same rules for a feature you are already holding in mind.

Order within the slice, because later steps depend on earlier ones:

1. **Domain** first — entities, enums, the state machine or SLA arithmetic the feature needs.
2. **Migration** next, and never edit a committed one.
3. **Service** in `Infrastructure`, going through `ComplaintQueryScope` for anything complaint-shaped.
4. **Endpoint** in `Api/Endpoints/` — bind, call one service, map a status code. No EF here.
5. **Angular** — service, then component, rendering from `availableActions` rather than re-deriving.
6. **SPEC.md** — tick the §20 roadmap row, and amend any section the implementation proved wrong.

Write the tests as you go, one per acceptance criterion, each able to actually fail if the behaviour
regresses. **The bar is the four suites in SPEC §18 and nothing more.** Do not add a fifth suite
without a reason stated in the PR description.

## 5 · Push and let CI verify

CI runs the whole gate against a real PostgreSQL on the exact SHA that will merge. Push and read the
result rather than rebuilding locally; a local run needs Docker and the .NET SDK and tells you
nothing CI will not.

Run the gate locally only to iterate on a specific failure:

```
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test  -c Release
npm ci --prefix web && npm run build --prefix web
```

**Green before a PR exists.** If it is red, fix it — do not open a PR and hope CI sorts it out.

> The .NET 9 SDK is not installed on this machine (SPEC §5). Until it is, the local gate cannot run
> at all and CI is the only verification. Report that as *not verified locally* — never as passing.

## 6 · Pull request

```
git push -u origin feat/<n>-<slug>
gh pr create --title "<n>: <milestone title>" --body "..."
```

The body must contain:

- `Closes #<n>`
- the acceptance criteria as a ticked checklist, **each naming the test that covers it**
- what was actually verified, and how
- anything that could **not** be verified, and why
- any SPEC.md amendment made, and why

## 7 · Review

**Before and after every review agent, check the working tree:**

```
git status --porcelain
```

A reviewer mutates files to prove a test can fail and restores them afterwards. An agent that dies
mid-run — an API error, a timeout — leaves the mutation behind. If the tree is dirty when you did
not edit anything, **read the diff before doing anything else.** Do not stage it and do not assume
it is yours.

Run the `pr-reviewer` subagent on the PR. Tell it that CI has already run the gate on this SHA and
that it should read `gh pr checks` rather than rebuild a database to repeat it — its budget belongs
in mutation testing, breaking the implementation to confirm a named test goes red.

Post the verdict to the PR:

```
gh pr review <n> --comment --body "<the verdict report>"
```

**Only four things may block a merge:** red CI · a test that cannot fail · a violation of a
SPEC §9 authorization invariant · a HIGH security finding. Everything else is advisory and gets
filed as a follow-up issue rather than holding up the milestone.

## 8 · The gate

Both the reviewer and CI must be green:

```
gh pr checks <n>
```

**On `PASS` with green CI, merge. Do not ask first.** The verdict and the green checks *are* the
approval — pausing to ask for it again is the thing this loop exists to remove. From the feature
branch:

```
node .claude/bin/record-verdict.mjs --pr <n> --verdict PASS --summary "<one line>"
gh pr merge <n> --squash --delete-branch
```

The merge is blocked unless that verdict exists **and its `headSha` matches the current HEAD**. Do
not write the verdict file by hand; `record-verdict.mjs` stamps the SHA itself, which is the whole
point. If the hook blocks you, it is telling you something true — do not route around it.

**On `FAIL`:** fix, push, re-run the reviewer. A new commit invalidates the verdict, so it must run
again. **Two rounds maximum** — after a second failure, stop and report to the human with the
reviewer's findings.

**On `BLOCKED`:** stop and report immediately. Do not merge.

**The only things that stop a merge are the four in SPEC §21.7** — red CI, a test that cannot fail,
a violated SPEC §9 invariant, and a HIGH security finding. An advisory finding does not, however
tempting: file it as an issue and merge. Neither does an unanswered question about a later
milestone. If a reviewer's advisory makes something in the PR body or SPEC.md *false*, correct that
before merging — a merge record naming a test that does not exist is worse than no record — but
correcting it is not a reason to ask, either.

## 9 · Advance

```
git checkout main
git pull --ff-only
```

Confirm the SPEC §20 roadmap row is now `☑` — it should have been ticked in the PR, not after the
merge. Then report: what merged, what the reviewer said, what the next unblocked issue is, and
anything filed as a follow-up.

## Reporting honestly

If the gate failed, say so with the real output. If a step was skipped, say which and why. If
something could not be verified — no SDK, Docker down, a missing dependency — report it as **not
verified**, never as passing.

The whole value of an automated loop is that its reports can be trusted without re-checking every
one.
