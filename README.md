# Faceless Studio

Ứng dụng tạo video YouTube **faceless** (không lộ mặt) tự động, viết bằng **.NET 11 (RC2) + Blazor**.

Mỗi lần tạo video, anh vào giao diện và nhập tham số: chủ đề, ngôn ngữ, độ dài, giọng đọc, ngân sách. Sau đó chọn provider cho từng bước rồi bấm **Tạo video**. Chương trình tự chạy hết 7 bước và cho ra:

- `video.mp4`
- `thumbnail.jpg`
- `subtitles.srt` (phụ đề CC cho YouTube)
- tiêu đề, mô tả và tag để copy

## 7 bước, mỗi bước chọn được provider

| Bước | Có sẵn (không cần key) | Miễn phí – cloud | Miễn phí – self-hosted | Trả phí – cloud |
|---|---|---|---|---|
| 1. Chọn chủ đề | Tự nhập | Google Trends + Gemini / Groq | Google Trends + Ollama / Custom endpoint | Claude + web search, Grok + Web/X search, Google Trends + các LLM trả phí ở bước 2 |
| 2. Kịch bản, tiêu đề, mô tả, tag | Tự viết kịch bản | Gemini / Groq | Ollama / Custom endpoint (OpenAI-compatible) | Claude (`claude-opus-5-5`), Claude Code CLI (gói Pro/Max), ChatGPT / Codex (gói ChatGPT), xAI Grok (API key hoặc gói SuperGrok), OpenRouter, z.ai GLM, Kimi, MiniMax, DeepSeek |
| 3. Giọng đọc | | Microsoft Edge TTS | Piper | ElevenLabs |
| 4. Hình ảnh / footage | Nền màu chuyển động | Pexels / Pixabay | ComfyUI (SD/Flux) | OpenAI Images |
| 5. Phụ đề chạy từng chữ | Lấy timing từ giọng đọc | Groq Whisper | Whisper.net | OpenAI Whisper |
| 6. Dựng video | | Shotstack Sandbox (có watermark) | FFmpeg | Shotstack |
| 7. Thumbnail | | Cloudflare Workers AI (Flux) | Khung hình + chữ | OpenAI Images |

Đầu trang có 3 **gói ngân sách** để tự điền nhanh: *Miễn phí (cloud)*, *Miễn phí (self-hosted)*, *Trả phí*. Chọn gói xong vẫn đổi được provider của từng bước. Provider chưa có key sẽ bị làm mờ. Chi phí API ước tính hiện ngay trên trang.

Về các định dạng:
- **Ngôn ngữ:** Tiếng Việt hoặc English.
- **Độ dài:** Shorts 30 giây hoặc 60 giây (dọc 9:16); video 3, 5 hoặc 10 phút (ngang 16:9).
- **Phụ đề trên video:** kiểu karaoke, chữ đang đọc sáng màu vàng. Có khi dựng bằng FFmpeg; Shotstack chỉ hiện theo cụm chữ.

## Chạy trên Windows

```powershell
# Một lần: cài .NET 11 SDK, FFmpeg, Piper + giọng Việt/Anh
powershell -ExecutionPolicy Bypass -File scripts\setup-windows.ps1

# Mỗi lần dùng
dotnet run --project src\FacelessStudio.Web
```

Mở **http://localhost:5080**. Vào **Cài đặt** để nhập API key cho các provider anh muốn dùng.

## Chạy bằng Docker (Linux)

```bash
docker compose -f docker/docker-compose.yml up -d --build
# Muốn có thêm Ollama (LLM self-hosted):
docker compose -f docker/docker-compose.yml --profile ollama up -d --build
docker compose -f docker/docker-compose.yml exec ollama ollama pull qwen2.5:7b
```

Mở **http://localhost:8080**. Image đã có sẵn FFmpeg, Piper (giọng `vi_VN-vais1000`, `en_US-lessac`) và font tiếng Việt.

## Lấy API key

- **Gemini** (miễn phí): https://aistudio.google.com/apikey
- **Groq** (miễn phí): https://console.groq.com/keys
- **Pexels** (miễn phí): https://www.pexels.com/api/
- **Pixabay** (miễn phí): https://pixabay.com/api/docs/
- **Cloudflare Workers AI** (miễn phí theo hạn mức hằng ngày): cần Account ID và API token có quyền *Workers AI*.
- **Shotstack**: https://dashboard.shotstack.io. Key *Sandbox* miễn phí nhưng video có watermark; key *Production* trả phí.
- **Anthropic (Claude)**: https://platform.claude.com/settings/keys
- **ElevenLabs**: https://elevenlabs.io. Model mặc định `eleven_turbo_v2_5` hỗ trợ tiếng Việt.
- **OpenAI** (ảnh + Whisper): https://platform.openai.com/api-keys
- **OpenRouter** (một key, hàng trăm model): https://openrouter.ai/keys
- **z.ai / GLM**: https://z.ai/manage-apikey/apikey-list (gói GLM Coding Plan: đổi Base URL thành `https://api.z.ai/api/coding/paas/v4`)
- **Kimi / Moonshot**: https://platform.moonshot.ai/console/api-keys
- **MiniMax**: https://platform.minimax.io
- **DeepSeek**: https://platform.deepseek.com/api_keys
- **xAI (Grok)**: https://console.x.ai
- **Custom endpoint**: server bất kỳ có API kiểu OpenAI (`/v1/chat/completions`) như LM Studio, vLLM, LiteLLM, llama.cpp. Nhập Base URL, model, key nếu có.

## Dùng gói trả phí sẵn có thay cho API key

Trang **Cài đặt → Tài khoản đăng nhập** có nút **Đăng nhập** cho từng tài khoản. Bấm vào, mở link hiện ra, đăng nhập rồi nhập mã; trang tự cập nhật khi xong. Cách này dùng mã thiết bị (device code) nên chạy được cả trong Docker hay trên máy chủ, không cần mở cổng. Token lưu ở `data/oauth/` và tự gia hạn.

- **ChatGPT / Codex**: dùng hạn mức gói ChatGPT Plus/Pro/Business với model Codex (mặc định `gpt-5.5`). Nếu báo lỗi khi xin mã, bật *Device code login* trong ChatGPT → Settings → Security. Đã đăng nhập Codex CLI (`codex login`) thì có thể bấm **Nhập từ CLI**.
- **xAI Grok (SuperGrok / X Premium+)**: dùng gói SuperGrok, không cần API key.
- **Claude Pro/Max**: app không đăng nhập thẳng vào tài khoản Claude, vì điều khoản của Anthropic không cho dùng token gói Pro/Max trong ứng dụng khác. Thay vào đó, provider **Claude Code CLI** gọi `claude -p` của Claude Code chính chủ trên máy anh. Cài Claude Code, chạy `claude login` một lần, rồi bấm nút *Claude CLI* trong Cài đặt để kiểm tra. Image Docker không có sẵn Claude Code. Muốn trả theo token thì dùng provider Claude với Anthropic API key như cũ.

Có hai cách nhập key:
- Vào trang **Cài đặt**. Key được lưu ở `data/settings.json` trên máy anh; file này không được commit lên git.
- Đặt biến môi trường cùng tên, ví dụ `GEMINI_API_KEY`.

## Thư mục

```
src/FacelessStudio.Core/       pipeline 7 bước, model dữ liệu, phụ đề, cấu hình
src/FacelessStudio.Providers/  các provider (mỗi vendor một thư mục)
src/FacelessStudio.Web/        giao diện Blazor
tests/FacelessStudio.Tests/    unit test + e2e
data/                          cài đặt, model Whisper, cache footage (không commit)
output/<thời gian>-<chủ đề>/   kết quả từng video (không commit)
assets/music/                  bỏ file nhạc nền có giấy phép vào đây, app sẽ trộn nhỏ dưới giọng đọc
```

## Thêm provider mới

1. Tạo class kế thừa `ProviderBase` và implement interface của bước tương ứng (`ITtsProvider`, `IMediaProvider`...).
   Riêng LLM cho bước chủ đề/kịch bản: chỉ cần một `ILlmClient` rồi gọi `services.AddLlmVendor(new LlmVendorInfo(...), sp => client)`. Dịch vụ có API kiểu OpenAI thì thêm một `CompatibleVendor` trong `Llm/Compatible/CompatibleRegistration.cs`. Đăng nhập OAuth thì implement `IOAuthAccount` (xem `Llm/Codex`, `Llm/Xai`).
2. Nếu provider cần key, khai báo khoá trong `SettingCatalog`.
3. Đăng ký provider bằng một dòng trong `ServiceCollectionExtensions.AddFacelessProviders`.

Giao diện tự hiển thị provider mới.

## Kiểm thử

```bash
dotnet test                                   # unit test (HTTP giả lập cho các provider cloud)
FACELESS_E2E=1 dotnet test                    # chạy thật: Edge TTS + FFmpeg, tiếng Việt và tiếng Anh
FACELESS_E2E=1 FACELESS_E2E_WHISPER=1 WHISPER_MODEL=tiny dotnet test   # thêm Whisper.net
```

## Lưu ý

- **Edge TTS** là dịch vụ không chính thức của Microsoft, có thể đổi giao thức bất cứ lúc nào. Nếu hỏng, cài `pip install edge-tts`: app sẽ tự dùng CLI đó dự phòng, hoặc anh chuyển sang Piper / ElevenLabs.
- **Gói miễn phí** của Gemini và Groq giới hạn số lần gọi. Dữ liệu gửi qua gói miễn phí có thể được nhà cung cấp dùng để cải thiện sản phẩm.
- **Chính sách YouTube**: video sản xuất hàng loạt theo khuôn mẫu dễ bị từ chối bật kiếm tiền ("inauthentic content"). Nên xem lại kịch bản trước khi đăng. Nội dung AI trông như thật thì cần bật nhãn *altered or synthetic content*.
- Chỉ dùng nhạc nền và footage có giấy phép rõ ràng.
