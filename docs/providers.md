# Provider integration notes

Mọi adapter trong app gửi request thật hoặc gọi engine thật. Các smoke test cloud đòi credential do chủ tài khoản cung cấp; không có key được commit. Model mặc định là điểm khởi đầu cấu hình, không phải cam kết rằng provider còn cung cấp model đó cho mọi tài khoản.

## Text: Gemini / Ollama / OpenAI

- Gemini dùng `v1beta/models/{model}:generateContent`, JSON output, `x-goog-api-key` header. Chọn project free tier và xác nhận billing/quota tại Google AI Studio. Không tự đổi sang tài khoản/gói paid.
- Ollama dùng `/api/generate`, JSON format, non-streaming. Mặc định `qwen3:4b`, có thể thay model đa ngôn ngữ. Pull model trước; chỉ kiểm tra `/api/tags` chưa chứng minh model tạo được tiếng Việt.
- OpenAI dùng Chat Completions JSON mode với giới hạn output token. Nhập riêng USD/million input/output tokens đúng model. Dự toán input dùng số byte UTF-8 cộng overhead để giữ ngân sách theo phía bảo thủ.
- Kịch bản được kiểm tra JSON/scenes/narration. Phản hồi rỗng, sai shape hoặc bị cắt không được biến thành placeholder thành công. Nội dung factual vẫn cần người dùng kiểm duyệt.

## Voice: Azure F0 / Piper / ElevenLabs

- Azure dùng endpoint TTS theo region và SSML escaped. Voice tự chọn `vi-VN-HoaiMyNeural` hoặc `en-US-JennyNeural` nếu không ghi đè. App không đọc được SKU resource từ speech key: **chỉ xác nhận free tier sau khi kiểm tra F0 trong Azure Portal**.
- Piper gọi executable với `--model` và `--output_file`; narration đưa qua stdin. Cấu hình riêng model vi và en. Tham khảo [Piper maintained project](https://github.com/OHF-Voice/piper1-gpl) và model card/giấy phép tại nguồn phân phối voice. Một bundle CLI Linux độc lập hoặc bản cài tương thích phải tồn tại trong container; Studio không cài Python runtime/voice package ngầm.
- ElevenLabs dùng `/v1/text-to-speech/{voiceId}`, mặc định `eleven_v3`; chọn voice ID từ tài khoản và kiểm tra model hỗ trợ ngôn ngữ. Nhập giá USD/1,000 ký tự theo gói. Không giả định free trial vẫn còn.
- Mỗi cảnh có audio thật được chuẩn hóa PCM WAV mono 16 kHz. FFprobe đo thời lượng; chữ không được phân bố đều để giả mốc phụ đề. Thời lượng cảnh sau synthesis mặc định bằng thời lượng lời đọc; editor có thể thêm thời gian giữ hình.

## Media và thumbnail

- Pexels: model `photos` hoặc `videos`. Dùng query từ visual prompt, ưu tiên orientation theo định dạng. Lưu attribution tác giả/source cùng asset và trong metadata export. Giấy phép Pexels và quyền đối với nội dung cụ thể cần được kiểm tra trước xuất bản.
- Cloudflare: account ID + token có quyền Workers AI, mặc định `@cf/black-forest-labs/flux-1-schnell`. Free allocation tùy gói, model và tài khoản. Adapter xử lý cả body ảnh và base64 trong JSON.
- ComfyUI: endpoint HTTP, checkpoint name chính xác như trong `CheckpointLoaderSimple`. Workflow chuẩn SDXL với CLIP encode → KSampler → VAE → SaveImage. Checkpoint mặc định `sd_xl_base_1.0.safetensors`; cần tự cài model và kiểm tra giấy phép. Workflow này không hỗ trợ tùy ý mọi loại model (Flux cần workflow khác). Prompt ID được lưu để tiếp tục polling sau restart, không submit lại.
- Hủy ComfyUI chỉ xóa prompt của tác vụ khỏi hàng đợi; không gọi global interrupt làm dừng tác vụ khác. Job đã chạy trên engine/cloud có thể tiếp tục sau khi Studio ngừng chờ, vì vậy giữ checkpoint và đối soát kết quả.
- OpenAI Images: `gpt-image-1`, `quality=low`, portrait 1024×1536 hoặc landscape 1536×1024. Giá cấu hình mỗi ảnh phải bao phủ kích thước/quality tương ứng; model hỗ trợ API khác cần adapter tương ứng, không chỉ đổi tên.
- Thư viện local: upload JPG/PNG/WebP/MP4, tối đa 200 MB/file. Media được đọc qua FFprobe trước khi lưu; serving yêu cầu đăng nhập và không nhận arbitrary filesystem path.
- Thumbnail ghép ảnh nền + tiêu đề bằng FFmpeg và font hỗ trợ dấu tiếng Việt, 1280×720 JPG. V1 có một template; sửa tiêu đề và tạo lại để thay text, không có editor layer tự do.

## Captions

- Groq/OpenAI gọi compatible audio transcription API với `verbose_json`, `timestamp_granularities[]=word`. Dùng `whisper-large-v3` / `whisper-1`. Audio được gửi theo cảnh để tránh giới hạn file; nếu cảnh vượt 24 MB, app yêu cầu chia nhỏ.
- whisper.cpp dùng `whisper-cli -m … -f … -l vi|en -ojf -ml 1 -sow`. Cần build có word segmentation/timestamps và model multilingual (ví dụ `ggml-base.bin`, không phải `base.en`). Kiểm tra model checksum từ nguồn chính thức [whisper.cpp](https://github.com/ggml-org/whisper.cpp).
- Căn theo audio thực; editor cho sửa text/start/end. Mốc âm, chồng lấn, NaN hoặc vượt video bị từ chối. Word timing do speech recognition tạo nên không bảo đảm chính xác tuyệt đối; nên xem lại tên riêng/tiếng Việt.
- SRT/VTT xuất từng word cue; burn-in dùng ASS karaoke-style hoặc single-word. Các ký tự điều khiển ASS được neutralize.

## Rendering và YouTube

- FFmpeg render local không gọi API, libx264/AAC/30fps, phụ đề libass. Transition hỗ trợ cut hoặc fade through black. Mỗi cảnh giữ ít nhất hết lời đọc; không âm thầm cắt audio để ép thời lượng.
- Shotstack phân biệt key `stage` và `v1`. Upload media qua ingest signed upload, poll source ready, submit render, lưu render ID trước polling. Cùng scene/audio/word timing được ánh xạ thành timeline cloud. Sandbox có thể watermark/expire output. Cloud HTML caption typography và fade có thể khác đôi chút FFmpeg. Giới hạn clip count/timeline tùy tài khoản, cần thử với video ngắn trước dự án dài.
- Google OAuth dùng authorization code + PKCE, state cookie và state một lần có expiry; refresh token lưu được bảo vệ. Scope upload + readonly, danh sách kênh lấy từ chính tài khoản đã cấp quyền. Resumable upload gửi chunks 8 MiB, hỏi offset trước khi tiếp tục; URL session được bảo vệ trong database.
- Upload luôn cần review/approval rõ ràng và mặc định private. Google OAuth testing/consent, quota, project audit và xác minh kênh là yêu cầu bên ngoài app. V1 không hứa upload public cho project chưa đủ quyền. Thumbnail upload lỗi không làm mất video ID và có nút retry riêng.

## Những gì “kiểm tra kết nối” chứng minh

Đó là request read-only tới model/voice/status endpoint hoặc `--help`/`--version` cho CLI. Nó không chứng minh giá/quota, ngôn ngữ, synthesis, quyền xuất bản hay chất lượng output. Để xác nhận live, tạo project thử ngắn, đặt trần ngân sách nhỏ, duyệt từng bước, kiểm tra artifact và billing dashboard; YouTube chọn private.
