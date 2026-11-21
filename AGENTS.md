# Studio branch and collaboration rules

- Use `codex/main` as the default branch for every Codex session. Synchronize it with `origin/codex/main` before starting work.
- Never merge or push this work into `main` or `claude/faceless-studio-dotnet`.
- Work directly on `codex/main` by default. When the user requests a new work branch, create it from the latest `origin/codex/main`. Open its pull request against `codex/main` and merge there after verification.
- Keep credentials out of tracked files, logs, test artifacts, and job snapshots.
- Preserve persisted project/provider IDs and SQLite data. Schema changes require migration and backup.
- Verify with the pinned .NET SDK, tests, and the applicable browser/Docker checks.
