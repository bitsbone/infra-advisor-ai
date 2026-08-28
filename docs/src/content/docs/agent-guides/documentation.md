---
title: Documentation Approach
description: How contributors keep the documentation useful as a learning lab without forcing every topic into one template
docType: maintainer
audience:
  - contributor
maturity: stable
verifiedOn: 2026-08-27
---

The documentation site is a public learning experience built around a working application. It should help a reader understand why an observability capability exists, how the project uses it, and what evidence confirms that it works. It is not a chronological implementation log or a copy of Datadog's product reference.

## Start by deciding whether documentation should change

Update an existing page when a feature extends an established concept, workflow, service, or experiment. Create a page only when the change introduces a durable topic that readers would reasonably seek or share on its own.

A new implementation detail does not automatically need a new page or sidebar item. Small changes often belong in an existing explanation, verification step, reference table, or release note.

## Choose a shape that fits the reader's task

These are useful archetypes, not mandatory templates:

| Archetype | Use it when | Often benefits from |
|---|---|---|
| Lesson | A reader needs a mental model or progressive explanation | Objectives, examples, recap |
| Experiment | A claim should be tested against observable evidence | Question, procedure, expected signal, findings |
| Comparison | Differences between viable paths teach something important | Shared scenario, dimensions, tradeoffs |
| Guide | A reader wants to complete a task | Prerequisites, ordered steps, verification, recovery |
| Concept | The topic explains why a system behaves as it does | Diagram, boundaries, examples, related concepts |
| Reference | A reader needs precise lookup information | Stable headings, tables, signatures, links |
| Runbook | An operator must respond safely and repeatably | Preconditions, commands, validation, rollback |
| Maintainer note | Contributors need project state or implementation coordination | Ownership, status, decisions, next actions |

Combine or omit sections when that improves the page. A reference page should not pretend to be a lesson, and a conceptual explanation should not manufacture procedural steps.

## Keep the public learning signal strong

Prioritize:

- The problem or question the feature helps answer
- Project-specific implementation choices that affect behavior
- Observable outcomes in the application or Datadog
- Important differences, limitations, privacy boundaries, and failure modes
- A reproducible verification step when the topic makes a testable claim
- Links to canonical sources for details that change independently of this project

Avoid:

- Repeating the same architecture or setup explanation on several pages
- Copying exhaustive Datadog configuration or API documentation
- Narrating every file changed during implementation
- Presenting backlog items as working features
- Adding a sidebar entry for a small extension of an existing topic
- Retaining long historical sections in the main learner flow

## Use components selectively

The components in `docs/src/components` are composable aids:

- `LearningObjectives` helps when explicit outcomes orient the reader.
- `ImplementationPath` separates meaningful alternatives or system paths.
- `ObservationChecklist` creates a persistent field exercise when a reader must collect and account for several pieces of evidence.
- `TelemetryComparison` supports a genuine side-by-side comparison.
- `FlowExplorer` makes a consequential relationship, branch, lifecycle, or service boundary selectable and pairs each stage with its purpose and observable evidence.

None is required on every page. Use a component only when it clarifies structure, reduces repetition, or makes evidence easier to interpret. A learner action should change what they can inspect, compare, recall, or track; visual motion and card chrome alone do not make content interactive. Keep simple sequences in prose or an ordered list, and include a usable text path for graph content.

## Review before publishing

Ask:

1. Can the intended reader tell what this page is for quickly?
2. Is the content organized around their question rather than the order the code was written?
3. Does the page distinguish implemented, partial, experimental, and planned behavior?
4. Does it link instead of duplicating material with a different source of truth?
5. Is a new page and sidebar entry genuinely warranted?
6. Can a testable claim be verified through a concrete application or Datadog signal?

The documentation checks flag structural risks such as missing experiment objectives, stale review metadata, heading jumps, and unusually long prose. Warnings prompt editorial review; they are not substitutes for judgment.
