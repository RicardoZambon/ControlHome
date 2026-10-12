# Architecture decision records

An ADR records one significant decision: the context, what was decided, and what follows from it. ADRs are never
edited after they are accepted — a later ADR **supersedes** an earlier one instead (update both statuses). The
current, consolidated design is in [`../architecture.md`](../architecture.md).

Write an ADR when a decision is hard to reverse, has real trade-offs, or someone will later ask "why did we do it this
way?" (a new runtime dependency, a storage or protocol choice, a security boundary, a connector strategy).

## Index

| # | Title | Status | Date |
|---|---|---|---|
| [0001](0001-stack-and-architecture.md) | Stack and architecture: .NET 10 modular monolith, Vue + shadcn-vue design tokens, SQL Server 2025, native connectors | Accepted | 2026-10-11 |

## How to add one

1. Copy the template below to `NNNN-short-kebab-title.md` (next free number, four digits).
2. Fill it in; keep it short — link to code, plans and issues instead of repeating them.
3. Add a row to the index above and update `architecture.md` in the same PR.

## Template

```markdown
# NNNN. Title in the imperative or as a noun phrase

- **Status:** Proposed | Accepted | Superseded by [NNNN](NNNN-....md) | Deprecated
- **Date:** YYYY-MM-DD
- **Deciders:** names / roles

## Context

What problem are we solving? Which forces, constraints and facts (with sources) matter?

## Decision

What we will do, stated plainly. Include the options that were considered and why they lost.

## Consequences

What becomes easier, what becomes harder, what we must now do (follow-up work, guard rails, tests), and what would
make us revisit this decision.
```
