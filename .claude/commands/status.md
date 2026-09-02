---
description: Show the state of the build — roadmap, issues, PRs, branch, CI, containers
---

Report the current state of the project. Gather in parallel where you can:

```
git rev-parse --abbrev-ref HEAD
git status --porcelain
gh issue list --state open --json number,title,labels,body --limit 100
gh pr list --state open --json number,title,headRefName,statusCheckRollup
gh run list --limit 5
docker compose -f infra/docker-compose.yml ps
```

Then report, compactly:

1. **Roadmap** — the `☐` / `◐` / `☑` counts from the SPEC.md §20 table, and which milestone is
   in progress.
2. **Next unblocked issue** — lowest-numbered open issue whose `Depends on` issues are all closed.
   If every open issue is blocked, say so explicitly; that is a problem, not a normal state.
3. **Open PRs** with their CI rollup, and whether a review verdict exists in `.claude/state/`.
4. **Branch and working tree.** If the tree is dirty and you did not edit anything this session,
   show the diff — a reviewer subagent that died mid-run can leave a mutation behind.
5. **Local infrastructure** — whether SQL Server and Azurite are up.
6. **Blockers** — anything that would stop `/next` from running right now. The .NET SDK not being
   installed is one of these (SPEC §5); say so rather than letting it surface as a build failure.

Do not start any work. This command only reports.
