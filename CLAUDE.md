# CLAUDE.md

Hướng dẫn cho Claude Code khi làm việc với repo này.

## Quy ước Git (bắt buộc)

- **`claude/main` là nhánh chính** của repo cho mọi phiên làm việc với Claude. Coi nó như `main`.
- **Không dùng nhánh `main`**: không checkout làm việc, không commit, không push, không mở PR vào `main`.
- Mặc định: làm việc và commit trực tiếp trên `claude/main`, push lên `origin/claude/main`.
- Khi được yêu cầu **tạo branch mới**: tạo từ `claude/main` mới nhất
  (`git fetch origin claude/main && git checkout -b <tên-branch> origin/claude/main`),
  làm xong thì mở PR với **base = `claude/main`**.

## Tổng quan

Faceless Studio: ứng dụng Blazor Server (.NET 11) tạo video YouTube "faceless" tự động qua pipeline 7 bước
(chủ đề → kịch bản → giọng đọc → hình ảnh → phụ đề → dựng video → thumbnail), mỗi bước chọn được provider.

```
src/FacelessStudio.Core/       pipeline (Pipeline/VideoPipeline.cs), jobs, model, settings, phụ đề, FFmpeg
src/FacelessStudio.Providers/  provider theo vendor: Llm/, Tts/, Media/, Render/, Thumbnail/, Timing/, Auth/ (OAuth device code)
src/FacelessStudio.Web/        giao diện Blazor (Components/Pages), Program.cs wiring DI
tests/FacelessStudio.Tests/    xUnit; FakeHttp.cs giả lập HTTP cho provider cloud
```

- Interface các bước: `src/FacelessStudio.Core/Abstractions/Providers.cs`. Provider kế thừa `ProviderBase`.
- Đăng ký provider: `ServiceCollectionExtensions.AddFacelessProviders` (một dòng mỗi provider).
- LLM vendor: `services.AddLlmVendor(...)`; API kiểu OpenAI → thêm `CompatibleVendor` trong `Llm/Compatible/CompatibleRegistration.cs`.
- Key/cấu hình provider khai báo trong `SettingCatalog`; lưu ở `data/settings.json` (không commit) hoặc biến môi trường.

## Build & test

- SDK pin trong `global.json`; `TargetFramework` đặt chung ở `Directory.Build.props` (và lặp lại trong từng csproj — đổi thì đổi cả hai).
- Solution dùng định dạng `.slnx`.

```bash
dotnet build FacelessStudio.slnx
dotnet test                                    # unit test
FACELESS_E2E=1 dotnet test                     # e2e thật: Edge TTS + FFmpeg
dotnet run --project src/FacelessStudio.Web    # http://localhost:5080
docker compose -f docker/docker-compose.yml up -d --build   # http://localhost:8080
```

## Phong cách

- README, comment code và chuỗi giao diện viết bằng tiếng Việt; giữ nguyên phong cách đó.
- Khi đổi phiên bản .NET: cập nhật `global.json`, `Directory.Build.props`, các csproj, `docker/Dockerfile`, `README.md`, `scripts/setup-windows.ps1`.
