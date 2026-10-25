# Triển khai và vận hành

## Dữ liệu và quyền truy cập

Volume `studio-data` chứa `studio.db` (WAL), `media/`, `temp/` và `keys/`. API keys, OAuth refresh tokens và resumable-upload URLs được Data Protection bảo vệ trước khi ghi database. Ai đọc được cả database và thư mục `keys` vẫn có thể giải mã; dùng disk encryption và giới hạn quyền volume theo yêu cầu triển khai.

Một instance Studio quản lý một database. Không scale replicas hoặc share SQLite qua NFS. Worker chạy một tác vụ nặng tại một thời điểm. Các scene render dùng hai FFmpeg threads. CPU 4+ cores / RAM 8 GB phù hợp cho app + FFmpeg; LLM/ComfyUI cần tài nguyên thêm. Chừa disk cho media và lịch sử; v1 không tự xóa artifact cũ.

Sao lưu: dừng `studio`, sao lưu **toàn bộ volume**, rồi khởi động lại. Chỉ copy `studio.db` đang chạy có thể bỏ sót WAL; copy riêng DB cũng mất protection keys. Khôi phục cùng thư mục cấu trúc và quyền UID của app. Các tác vụ đang chạy khi restart trở thành `NeedsAttention`, không tự gửi lại yêu cầu tạo nội dung. Job Shotstack/ComfyUI và resumable YouTube có checkpoint cho nút **Tiếp tục job đã gửi**.

Nếu không có checkpoint an toàn, đối chiếu dashboard/billing provider trước, ghi chi phí thực ở **Chi phí & tác vụ → Đối soát**. Các khoản chưa rõ kết quả không được giải phóng chỉ vì đã hủy HTTP request.

## Docker local / Linux VPS

Image chạy bằng user `app` (UID 1654), chứa .NET 11 RC2, FFmpeg và font. Thư mục model/engine trên host mount read-only. Chưa có API keys thì Studio vẫn khởi động và cho cấu hình dự án; không tự sinh credentials.

`STUDIO_ADMIN_PASSWORD_FILE` đọc mật khẩu lần khởi tạo đầu tiên. Đổi file không tự thay tài khoản đã có. Để reset, cập nhật file secret rồi dừng instance đang chạy và dùng:

```bash
docker compose stop studio
docker compose run --rm studio --reset-admin
docker compose up -d studio
```

Không xóa `keys/` khi đổi mật khẩu. Các cookie đang đăng nhập cần được thu hồi bằng xoay key/cookie theo quy trình vận hành riêng nếu có nghi ngờ lộ tài khoản; v1 password reset không tự thu hồi các cookie cũ.

Trên VPS, đặt Caddy/nginx trước cổng loopback 5080, cấu hình TLS và hỗ trợ WebSocket cho Blazor. Đặt `STUDIO_PUBLIC_URL=https://studio.example.com` và `STUDIO_ALLOWED_HOSTS=studio.example.com` trước `docker compose up`. Public URL quyết định callback OAuth và chế độ secure cookie. Không mở trực tiếp cổng HTTP ra Internet.

Không nhập API key qua HTTP công khai. Cấu hình giá phải khớp model; nếu đổi model trong dự án, cập nhật model và giá Settings trước khi chạy paid.

## Windows + NVIDIA GPU

1. Cài driver NVIDIA hiện hành hỗ trợ WSL2, bật WSL2 và Docker Desktop Linux containers.
2. Kiểm tra GPU trong WSL2 và Docker theo hướng dẫn NVIDIA/Docker của phiên bản đang dùng.
3. Chạy Studio như trên. Dùng profile GPU cho Ollama:

```bash
docker compose --profile ai-gpu up -d studio ollama-gpu
docker compose exec ollama-gpu ollama pull qwen3:4b
```

Chỉ lệnh `pull` mới tải model; nó tiêu thụ disk/network. Đặt endpoint Ollama trong Studio thành `http://ollama-gpu:11434`. Không bật đồng thời profile CPU và GPU vì cả hai share model volume.

CPU-only:

```bash
docker compose --profile ai-cpu up -d studio ollama
docker compose exec ollama ollama pull qwen3:4b
```

Endpoint: `http://ollama:11434`. Nếu Ollama/ComfyUI chạy native Windows, dùng `http://host.docker.internal:11434` hoặc `:8188`; cấu hình engine listen trên interface mà Docker truy cập được và giới hạn firewall ở local/private network. Không expose endpoint AI không xác thực ra Internet.

Piper và whisper.cpp được gọi bằng CLI trong cùng môi trường app. Khi Studio chạy Linux container, mount **bản Linux** tương ứng vào `engines/`, đầy đủ thư viện phụ thuộc; executable phải có quyền chạy. Không dùng `.exe` Windows trong Linux container. Có thể chạy app bằng `dotnet run` native Windows và cấu hình executable/model Windows cùng `Studio__FontFile` trỏ đến font hỗ trợ tiếng Việt.

## Managed cloud và network policy

Môi trường Codex dùng proxy/CA có sẵn. Build theo:

```bash
BUILDX_CONFIG=/workspace/.tools/buildx docker compose -f compose.yaml -f compose.cloud.yaml build studio
```

`compose.cloud.yaml` mount combined CA ở build/runtime; CA phiên làm việc không được bake vào image. Giữ proxy và auth Docker do nền tảng cung cấp, không tắt TLS verification. Có thể cần quyền đọc file secret từ UID trong container.

Các domain phục vụ tính năng chỉ cần mở khi dùng provider đó:

| Provider | API destinations |
|---|---|
| Gemini | `generativelanguage.googleapis.com` |
| OpenAI | `api.openai.com` |
| Azure Speech | `<region>.tts.speech.microsoft.com` |
| ElevenLabs | `api.elevenlabs.io` |
| Pexels | `api.pexels.com`, `images.pexels.com`, các CDN download do Pexels trả về |
| Cloudflare | `api.cloudflare.com` |
| Groq | `api.groq.com` |
| Shotstack | `api.shotstack.io`, các host S3/CDN trong ingest/render response |
| YouTube | `accounts.google.com`, `oauth2.googleapis.com`, `www.googleapis.com`, upload host Google APIs trả về |
| Ollama models | Registry/blob hosts do bản Ollama sử dụng; xem lỗi pull và mở đúng host |

Danh sách này không tự thay allowlist của môi trường. Nếu gặp 403 từ proxy, thêm đúng domain qua Environment settings, rồi thử lại. Không đổi provider hoặc bỏ proxy để né policy.
