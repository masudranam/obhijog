# 1. Record architecture decisions

Date: 2026-09-02

## Status

Accepted

## Context

This project is built by an agent loop, one milestone per pull request, over a long run. Decisions
made in a single session are invisible to the next one unless they are written down, and the most
expensive kind of rework is re-litigating a choice that was already settled for a good reason.

`SPEC.md` §23 already carries a decision log — a one-row summary of each significant choice, its
rejected alternative, and why. That table is the right home for decisions that fit in a row.

Some decisions do not. A choice with real trade-offs, a migration path, or consequences that need a
page of explanation deserves more than a table cell, and burying it in `SPEC.md` would bloat a
document that is meant to be scannable during implementation.

## Decision

Two homes, by size:

- **`SPEC.md` §23** — every significant decision gets a row: what was decided, what was rejected,
  why. This is the index. A reader should be able to answer "why is it like this?" from the table
  alone for most questions.
- **`docs/adr/NNNN-title.md`** — a full ADR for a decision that is costly to reverse, has a
  non-obvious migration path, or needs more than a row to justify. The §23 row then links to it.

ADRs are numbered sequentially, never renumbered, and never edited once accepted. A decision that
changes gets a **new** ADR that supersedes the old one, and the old one's Status becomes
`Superseded by NNNN`.

## Consequences

- Every decision is discoverable from one table, so a session does not have to read the whole
  `docs/` tree to find out whether something was already settled.
- `SPEC.md` stays scannable, because the long-form reasoning lives elsewhere.
- The rule "a decision that changes gets a new ADR" means the history of a reversal survives, which
  is usually the part worth having.
- One more file to keep in sync — mitigated by the rule that the §23 row is mandatory and the ADR is
  optional, so the index can never be stale even if nobody writes the long form.

Following the format described by Michael Nygard in *Documenting Architecture Decisions*.
