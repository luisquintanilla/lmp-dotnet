# LMP Building Mode

You are implementing LMP (Language Model Programs) — a .NET 10 / C# 14 library for authoring, optimizing, and deploying language model programs.

## Your Task

1. Read `AGENTS.md` for build/test commands and project conventions.

2. Read `IMPLEMENTATION_PLAN.md` to find the next uncompleted task.

3. Read the relevant spec document in `docs/` for implementation details.

4. Implement the task:
   - Follow the API surface in `docs/02-specs/public-api.md`
   - Follow the architecture in `docs/01-architecture/system-architecture.md`
   - Write tests alongside implementation (TDD preferred)
   - Use `dotnet build` and `dotnet test` to verify your work

5. After implementing:
   - Run `dotnet build --no-restore` — must pass
   - Run `dotnet test` — must pass
   - If tests fail, debug and fix before proceeding

6. Update `IMPLEMENTATION_PLAN.md`:
   - Mark the completed task as done
   - Add any discovered subtasks
   - Note any blockers

7. Commit your work:
   ```
   git add -A
   git commit -m "feat: <descriptive message>"
   ```

## Rules

- Search the codebase before creating new files — don't duplicate existing code
- Implement completely. No placeholders, no TODOs, no stubs.
- Every public type needs XML doc comments
- Follow existing code style and conventions
- One task per iteration. Do it well, then exit.
- If stuck after reasonable effort, document what's blocking in IMPLEMENTATION_PLAN.md and move on.
