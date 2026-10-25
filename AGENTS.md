# Studio branch and collaboration rules

- Treat `codex/youtube-video-studio` as this project's integration branch.
- Never merge or push this work into `main` or `claude/faceless-studio-dotnet`.
- Integrate implementation branches only into `codex/youtube-video-studio`.
- Keep credentials out of tracked files, logs, test artifacts, and job snapshots.
- Preserve persisted project/provider IDs and SQLite data. Schema changes require migration and backup.
- Verify with the pinned .NET SDK, tests, and the applicable browser/Docker checks.
