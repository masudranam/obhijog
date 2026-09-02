---
description: Run one complete milestone cycle on the next unblocked issue
argument-hint: '[issue number — optional, defaults to the next unblocked one]'
---

Run the `feature-cycle` skill for a single milestone.

Target issue: $ARGUMENTS

If no issue number was given, pick the lowest-numbered open issue whose `Depends on` issues are all
closed. If one was given, use it, but check its dependencies first and warn if any are still open.

Before writing any code, read that milestone's row in SPEC.md §20 and every `F*` feature section it
names in §14 — those acceptance criteria are the requirements, not the issue body.
