---
spec: EX
title: Example spec
---
# Example spec

Each requirement: a `## ID: Title` heading, an optional status, a statement (EARS recommended)
and acceptance criteria `- AC-n: ...`. Statuses: draft, proposed, approved (default), implemented, deprecated.

## EX-001: Greet the user
Status: proposed

When a user opens the home page, the system shall greet them by their first name.

- AC-1: Given a user named Ana, when she opens the home page, then she sees "Hello, Ana".
- AC-2: Given an anonymous visitor, when they open the home page, then they see "Hello".
