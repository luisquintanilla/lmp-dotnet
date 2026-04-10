# LMP Building Mode

You are implementing LMP (Language Model Programs) — a .NET 10 / C# 14 library for authoring, optimizing, and deploying language model programs.

## Your Task

1. Read `AGENTS.md` for build/test commands and project conventions.

2. Read `IMPLEMENTATION_PLAN.md` to find the next uncompleted task (marked ❌). Tasks marked ✅ are done — skip them.

3. For **doc spec tasks** (Phase 9A): Read the actual source code to understand the real API, then fix the spec doc to match. The SOURCE CODE is the ground truth, not the docs.

4. For **code tasks** (Phase 9B): Read the relevant spec and existing code, then implement. Write tests alongside implementation.

5. After implementing:
   - Run `dotnet build --no-restore` — must pass with 0 errors, 0 warnings
   - Run `dotnet test` — must pass with 0 failures
   - If tests fail, debug and fix before proceeding

6. Update `IMPLEMENTATION_PLAN.md`:
   - Change the completed task's ❌ to ✅
   - Add any discovered subtasks
   - Note any blockers

7. Commit your work:
   ```
   git add -A
   git commit -m "feat: <descriptive message>

   Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
   ```

## Rules

- Search the codebase before creating new files — don't duplicate existing code
- Implement completely. No placeholders, no TODOs, no stubs.
- Every public type needs XML doc comments
- Follow existing code style and conventions
- One task per iteration. Do it well, then exit.
- If stuck after reasonable effort, document what's blocking in IMPLEMENTATION_PLAN.md and move on.
- The source code in `src/` is ALWAYS the ground truth. If a doc says one thing and the code says another, the code wins.
- `TreatWarningsAsErrors` is enabled globally — 0 warnings required.
- `IsAotCompatible` is enabled on Abstractions, Core, Optimizers — new code must not introduce AOT warnings.
