---
spec_id: SPEC-YYYY-MM-DD-short-slug
title: <one line>
status: draft
branch: feat/<short-slug>
owner: <handle>
capabilities: []
created: YYYY-MM-DD
updated: YYYY-MM-DD
---

# <Title>

## Why
<The problem and the motivation. What forces this change?>

## What changes
- ADDED <capability> — <behaviour> (FR-xx)
- MODIFIED <capability> — <behaviour> (breaking: no)

---

## Requirements

### Requirement: <name>
`capability: <cap>` · `delta: ADDED (<branch>)`

The system SHALL <observable behaviour>.

#### Scenario: <name>
- **WHEN** <condition>
- **THEN** <expected outcome>
- **AND** <additional expectation>

---

## Design

### Architectural decision
<The key decision and its rationale.>

### Target architecture
<Diagram or prose of the components and how they interact.>

---

## Tasks
- [ ] <task>

### Testing
- Unit: <…>
- Integration: <…>

### Definition of done
All scenarios pass; typecheck and build are green; a reviewer has signed off.
