# Frame Studio — YouTube Video Studio

Studio cá nhân viết bằng **C#/.NET 11 RC2 + Blazor Interactive Server**, từ chủ đề đến MP4 và upload YouTube. Giao diện tiếng Việt/Anh, video tiếng Việt/Anh, Short 9:16 hoặc dài 16:9, chọn provider độc lập ở cả 10 công đoạn.

## Chạy bằng Docker

Yêu cầu Docker Engine/Desktop và Compose v2. Cổng mặc định chỉ bind loopback; model AI không được tải tự động.

```bash
mkdir -p .secrets
chmod 700 .secrets
read -r -s -p 'Mật khẩu quản trị (ít nhất 12 ký tự): ' studio_admin_password
printf '%s' "$studio_admin_password" > .secrets/admin-password
unset studio_admin_password
chmod 644 .secrets/admin-password
docker compose up --build -d studio
```

Compose secrets là file bind mount. Thư mục `.secrets` mode 700 chặn người dùng khác trên host; file mode 644 cho phép user `app` đọc bản mount read-only trong container. Có thể thay bằng ACL cho UID 1654 hoặc secret store của hệ thống triển khai. Không commit file này. Docker Desktop quản lý quyền mount theo cơ chế của Desktop.

Mở cổng **5080 trên máy chạy Studio** để đăng nhập. Xem [hướng dẫn triển khai](docs/deployment.md) cho VPS, HTTPS, Windows GPU, backup và reset mật khẩu.

## Chạy từ source

SDK được ghim trong `global.json`: `11.0.100-rc.2.26504.105`. Cần FFmpeg/ffprobe với libass, libx264, AAC và font DejaVu Sans hỗ trợ tiếng Việt. Không cần Node/npm cho ứng dụng.

```bash
dotnet restore YouTubeStudio.slnx --locked-mode
dotnet build YouTubeStudio.slnx --no-restore
read -r -s -p 'Admin password: ' STUDIO_ADMIN_PASSWORD
export STUDIO_ADMIN_PASSWORD
cd src/Studio
dotnet run --no-build
```

Mặc định nghe trên `127.0.0.1:5080`, dữ liệu ở `.studio-data/` trong repo khi chạy từ `src/Studio`. Có thể cấu hình `Studio__DataPath` bằng đường dẫn tuyệt đối. Không đặt password trong tham số dòng lệnh hoặc file tracked.

Trong cloud workspace có home chỉ đọc:

```bash
bash scripts/bootstrap.sh
source scripts/env.sh
```

Bootstrap cài đúng SDK từ Microsoft và kiểm tra SHA-512 công bố; `env.sh` đặt cache và runtime IPC vào `/workspace/.tools`. Có thể đặt `STUDIO_TOOLS_ROOT` vào thư mục ghi được khác. Script không thay đổi `HOME`, không tắt TLS và không tạo Git worktree.

## Quy trình sử dụng

1. Vào **Cài đặt & kết nối**: nhập key/endpoint, model và voice. Bấm kiểm tra kết nối. Free cloud cần xác nhận tài khoản/resource đúng free tier; paid cần nhập giá, nguồn giá và ngày đã kiểm tra.
2. **Tạo video mới**: ngôn ngữ, định dạng, thời lượng, audience/style, ngân sách USD/VND, preset provider và chế độ chạy.
3. **Tiếp tục quy trình** hoặc chạy từng bước. Chế độ duyệt dừng ở mỗi kết quả; chế độ tự động đi đến bản dựng, trừ khi lỗi, thiếu cấu hình hoặc ngân sách không đủ.
4. Sửa kịch bản/cảnh, thay stock hoặc upload media, chỉnh mốc phụ đề và duyệt. Sửa đầu vào đánh dấu các bước phụ thuộc cần cập nhật; giữ phiên bản cũ.
5. Xem preview, tải MP4/thumbnail/SRT/VTT/script/metadata hoặc kết nối YouTube và bấm duyệt đăng. Không có tự đăng nền khi chưa đồng ý.

Ngân sách bao gồm lần chạy lại. Các khoản ước tính hoặc chưa rõ kết quả vẫn chiếm ngân sách cho đến khi đối soát. Đây không phải giới hạn hóa đơn phía nhà cung cấp. Dự án đặt ngân sách 0 vẫn dùng được provider local/free đúng gói.

## Provider

| Công đoạn | Cloud miễn phí / sandbox | Self-hosted | Paid |
|---|---|---|---|
| Chủ đề / kịch bản / tiêu đề / mô tả / tag (chọn riêng) | Gemini | Ollama, Custom Endpoint | OpenAI, OpenRouter, Anthropic, z.ai, Kimi, MiniMax, DeepSeek, xAI; subscription: ChatGPT/Codex, Claude Code, SuperGrok thử nghiệm |
| Giọng đọc | Azure Speech F0 | Piper CLI | ElevenLabs |
| Ảnh / footage | Pexels, Cloudflare Workers AI | Media upload, ComfyUI | OpenAI Images |
| Phụ đề theo từ | Groq Whisper | whisper.cpp CLI | OpenAI Whisper |
| Dựng | Shotstack sandbox | FFmpeg | Shotstack production |
| Ảnh nền thumbnail | Cloudflare Workers AI | ComfyUI | OpenAI Images |

Thumbnail được ghép chữ cục bộ để giữ chính xác dấu tiếng Việt. Renderer cloud dùng ingest upload, không đòi public URL trỏ về máy của anh. Không có fallback tự động sang paid hoặc kết quả AI giả lập trong ứng dụng.

Xem [provider và self-hosted engines](docs/providers.md) cho endpoint, định dạng, giới hạn và các bước xác minh trước khi dùng thật.

## Kiểm thử

```bash
dotnet test YouTubeStudio.slnx --filter 'Category!=Browser' --logger trx
```

Kiểm thử browser dùng Playwright for .NET và Chromium. Dùng browser đã cài:

```bash
CHROMIUM_PATH=/usr/bin/chromium dotnet test YouTubeStudio.slnx --filter Category=Browser
```

Hoặc cài browser Playwright sau khi build:

```powershell
pwsh tests/Studio.Tests/bin/Debug/net11.0/playwright.ps1 install --with-deps chromium
dotnet test YouTubeStudio.slnx
```

`STUDIO_SCREENSHOTS` chỉ định thư mục lưu screenshot từ test. Bộ test media dựng và giải mã MP4 thực, ở cả hai ngôn ngữ và hai tỷ lệ; audio fixture là tín hiệu kiểm thử, không phải TTS thật. Kiểm thử provider dùng HTTP giả lập. Xem [bằng chứng kiểm chứng](docs/verification.md); không đồng nhất test giả lập với kết nối tài khoản thật.

## Cấu trúc

- `src/Studio`: Blazor UI, domain, adapter, job worker và SQLite/EF Core 11.
- `tests/Studio.Tests`: pipeline/budget, HTTP contract, FFmpeg và browser tests.
- `docs`: triển khai, provider, kiểm chứng.

V1 là single-instance/single-owner. Không có thanh toán SaaS, nhiều workspace, timeline nhiều track hay avatar. SQLite schema phiên bản 1; trước thay đổi schema phải bổ sung migration và backup, không xóa database để nâng cấp.

## Kết nối LLM bổ sung

Xem [hướng dẫn kết nối LLM](docs/llm-connections.md) cho API key, ChatGPT OAuth,
Claude Code subscription, SuperGrok thử nghiệm, model discovery và ngân sách extra usage.
Các kết nối này chỉ phục vụ năm bước văn bản. API key và subscription là các lựa chọn
riêng; ứng dụng không tự chuyển loại thanh toán khi hết quota.

Nhánh tích hợp của dự án là **`codex/youtube-video-studio`**. Không merge công việc
Studio vào `main` hoặc `claude/faceless-studio-dotnet`.
