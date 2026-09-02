---
name: Milestone
about: One milestone from the SPEC.md §20 roadmap — one branch, one PR, one squash merge
title: 'M?: <milestone title>'
labels: milestone
---

## Milestone

<!-- e.g. M4 — Submission, citizen views, public tracking -->

**Features:** <!-- e.g. F4, F5 — the SPEC.md §14 sections that define this work -->

**Depends on:** <!-- e.g. #3 — or "none". The `/next` loop reads this line to pick work. -->

## Requirements

The authoritative requirements are the `F*` sections named above in
[SPEC.md §14](../../SPEC.md#14-feature-specifications), plus this milestone's row in
[SPEC.md §20](../../SPEC.md#20-delivery-roadmap).

**This issue is a convenience copy. Where the two disagree, SPEC.md wins and this issue gets
corrected.**

## Acceptance criteria

<!-- Copied from the F* sections. Each one must end up covered by a test or a
     demonstrable UI path, named in the PR description. -->

- [ ]
- [ ]
- [ ]

## Definition of done

<!-- Copied verbatim from this milestone's SPEC §20 row. -->

## Out of scope for this milestone

<!-- Anything a reader might reasonably assume is included but is not — especially
     anything listed in SPEC §22, or deferred to a later milestone. -->

---

<sub>Worked by `/next`, which runs the
[feature-cycle](../../.claude/skills/feature-cycle/SKILL.md) loop: claim → branch → implement +
test → CI → PR → review → verdict-gated squash merge. Tick the SPEC §20 roadmap row in the PR
itself, only once the Definition of Done actually passes.</sub>
