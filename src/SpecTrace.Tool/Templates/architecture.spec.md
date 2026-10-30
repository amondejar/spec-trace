---
spec: ARC
title: Architecture
kind: architecture
---
# Architecture

> Created by `dotnet spectrace init --architecture`. Replace the examples with how this project is built.
> Everything outside the rules is guidance for whoever writes code (people and agents get it with get_architecture).
> Rules (## ARC-n) are checked by the build (ST0015) once their status is approved.

## Principles

- Business rules live in the domain ({{root}}.Domain); endpoints only translate HTTP to domain calls and back.
- The domain gets its dependencies through the constructor (time, storage, options), so it is tested without HTTP.
- Prefer the existing types and patterns of the code over new ones; a new pattern is a decision, write it here.

## ARC-001: The domain does not depend on the web
Status: proposed
In: {{root}}.Domain
Must not use: Microsoft.AspNetCore

The domain must be usable and testable without HTTP: requests, responses and status codes belong to the endpoints.

## ARC-002: Time is injected
Status: proposed
In: {{root}}.Domain
Must not use: System.DateTime.Now, System.DateTime.UtcNow, System.DateTime.Today, System.DateTimeOffset.Now, System.DateTimeOffset.UtcNow
Use instead: TimeProvider

Rules that depend on the time ("has not started", "in the past") must be testable with a fixed clock.
