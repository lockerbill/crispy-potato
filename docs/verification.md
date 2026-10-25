# Verification record

Verified in the managed Linux x64 workspace on 2026-10-24, on branch `codex/youtube-video-studio`.

## Passed

| Check | Evidence / outcome |
|---|---|
| SDK | .NET `11.0.100-rc.2.26504.105`; downloaded from Microsoft and SHA-512 verified against release metadata |
| Locked restore | `dotnet restore YouTubeStudio.slnx --locked-mode` passed |
| Build / repeatable bootstrap | `bash scripts/bootstrap.sh` passed; 0 warnings, 0 errors |
| Automated tests | **45 executed, 45 passed, 0 failed, 0 skipped**, using `dotnet test YouTubeStudio.slnx --no-restore` with system Chromium |
| Real media | FFmpeg rendered and decoded 1080p H.264/AAC for VI/EN and 9:16/16:9; ASS word highlight, Vietnamese thumbnail text and output dimensions verified |
| Browser | Real Chromium: authentication, VI/EN UI toggle, project creation, provider preset, scene editing, approvals, script download, real video playback/MP4 download, disabled publication before approval, responsive 390px viewport |
| Pipeline / cost | Automatic and review workflows, no automatic YouTube upload, atomic budget reservations, USD/VND, missing price, repeated runs, queued/in-flight cancellation, restart uncertainty, stale edits and version restoration |
| Mock HTTP contracts | Gemini/Ollama/OpenAI text; Azure/ElevenLabs audio; Cloudflare/OpenAI image; Pexels attribution and credential separation; Groq/OpenAI word timestamps; ComfyUI polling/resume and targeted queue cancellation; Shotstack timeline mapping |
| YouTube protocol, mocked | Explicit approval gate, expired OAuth state, encrypted upload session, interrupted 8 MiB chunk upload resumes from confirmed offset without creating a second session |
| Container build | Pinned .NET SDK/runtime images, locked NuGet restore, CA secret for proxy trust, signature-verified distro packages; Release publish succeeded |
| Container runtime | Non-root startup, health, admin initialization, antiforgery login, authenticated dashboard, persistent authentication/database across restart, FFmpeg H.264/AAC and Vietnamese font |
| Compose | Default and managed-cloud Compose configuration validation passed; actual default Compose deployment initialized admin from a read-only password file and started successfully |
| Repository hygiene | `git diff --check` passed; test data, secrets, media, SDK caches and screenshots are excluded from Git |

Local evidence is in `.artifacts/test-results/studio.trx`, `.artifacts/docker-smoke.txt` and `.artifacts/screenshots/`. These generated artifacts are not source files and are not committed. Tests use isolated temporary databases; test credentials are deliberately non-production fixtures.

## Not executed against live external accounts

No cloud provider credentials, Google OAuth client/channel, self-hosted AI models or NVIDIA GPU were available in this workspace. Therefore:

- Cloud text, speech, image, transcription and Shotstack render/ingest were **not live-smoke-tested**. Implementations issue real requests, but mock contract tests do not establish current account permission, API compatibility, quota or price.
- A real Google OAuth consent and private YouTube upload were **not performed**. Resumable upload and recovery are verified with HTTP fixtures; Google project audit, account verification and publication permissions remain external prerequisites.
- Ollama, Piper, whisper.cpp and ComfyUI model inference were **not executed**. CLI/HTTP adapters and optional deployment profiles exist; models/checkpoints must be installed/configured and their licenses checked.
- Windows/WSL2 NVIDIA execution, a public HTTPS reverse proxy and GitHub Actions on a remote runner were **not tested here**. Linux CPU runtime and Chromium were tested locally.
- Current commercial prices and free-tier allocations were **not confirmed**. Several official documentation domains are blocked by this cloud environment's network policy. The app requires explicit free-plan confirmation or model-specific pricing configuration rather than silently assuming a paid account is free.

The rendering fixtures contain generated images and a sine-wave audio signal. They validate media processing, timestamps and playback, not TTS naturalness or factual quality. Cloud account/model validation should use a short project in review mode, a small budget and a private YouTube upload.

This record establishes the tested workspace and container behavior. It does not claim a published cloud environment snapshot, a deployment to the user's VPS, live provider authorization, or restoration in a new Codex task.
