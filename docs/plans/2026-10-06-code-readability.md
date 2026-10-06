# Code Readability Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make existing source easier to read and prevent compressed code from returning, without changing business behavior.

**Architecture:** Keep the current hosts, services, transactions and API contracts. Use EditorConfig and selected StyleCop layout rules for C#, and Prettier plus ESLint for Vue/TypeScript. Generated source is excluded from formatting checks.

**Tech Stack:** .NET 10, StyleCop.Analyzers, Vue 3, TypeScript, ESLint, Prettier.

---

### Task 1: Establish and verify formatting

- Add `.editorconfig`; update `Directory.Build.props` and NuGet lock files.
- Enable braces, formatting, one statement per line, multiline elements and member spacing.
- Add pinned frontend tools, scripts and configurations; format authored frontend source.
- Add a CI workflow to restore locked dependencies, verify formatting, build and test.
- Verify with `dotnet format PlantOps.sln --verify-no-changes`, `dotnet build PlantOps.sln`, frontend format/lint/build/test commands and existing backend tests.
- Commit the verified tooling and formatting stage.

### Task 2: Refactor the maintenance service

- Update `src/PlantOps.Infrastructure/Maintenance/WorkOrderService.cs`: expand business names, extract cursor and transition-note helpers, explain checks after acquiring the plant lock.
- Preserve permission checks, query semantics, version checks, audit events and transaction order.
- Run existing work-order tests against the dedicated `plantops_tests` database, then the full suite. Report explicitly if database tests cannot run.
- Document conventions and verification commands in `docs/CODE_STYLE.md` and link from README.
- Commit after checks pass.

## Verification results

- Formatting/tooling stage: all 88 backend tests passed on a fresh schema in `plantops_tests`, with no skips; frontend format, lint, type check, build and realtime test passed.
- Maintenance refactor: all 10 work-order cases passed. Added an API-level regression for pagination with equal timestamps and invalid cursors. Final full backend run passed all 89 cases, with no skips.
- Final `dotnet format --verify-no-changes` passed. Dependency restore uses the committed locks.
- The old test schema had 200 North-plant data sources and hit the existing configuration limit. Verification used fresh test schemas without deleting retained data or changing the business database.
- The GitHub Actions workflow is added; its hosted run remains to be verified after pushing.
