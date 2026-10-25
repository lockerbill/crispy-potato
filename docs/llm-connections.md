# Kết nối LLM / LLM connections

Các provider bên dưới dùng cho Topic, Script, Title, Description và Tags. Mỗi bước
chọn provider và model độc lập; v1 lưu một kết nối cho mỗi provider. Các preset và
provider cũ vẫn giữ nguyên. Phạm vi subscription v1 là Studio cá nhân chạy native
hoặc Docker trên cùng máy với browser, không phải luồng đăng nhập VPS.

## API keys và endpoint

| Provider | API base URL mặc định | Models |
|---|---|---|
| OpenRouter | `https://openrouter.ai/api/v1` | Discovery + nhập thủ công |
| Anthropic | `https://api.anthropic.com/v1` | Discovery + nhập thủ công; Messages API |
| z.ai / GLM | `https://api.z.ai/api/paas/v4` | Nhập thủ công |
| Kimi / Moonshot | `https://api.moonshot.ai/v1` | Discovery + nhập thủ công |
| MiniMax | `https://api.minimax.io/v1` | Nhập thủ công |
| DeepSeek | `https://api.deepseek.com` | Discovery + nhập thủ công |
| xAI / Grok | `https://api.x.ai/v1` | Discovery + nhập thủ công |
| Custom Endpoint | Anh nhập API base URL | Discovery tùy server; luôn nhập thủ công được |

Các endpoint named provider là cố định. z.ai mặc định dùng **Standard API Key**,
không phải Coding Plan key. Kimi dùng key quốc tế, không phải Kimi Membership/Code
subscription. Không tự thử key trên nhiều vùng hoặc endpoint tính phí khác nhau.

Custom Endpoint nhận API tương thích OpenAI Chat Completions; nhập base URL chứa
prefix cần thiết, ví dụ `http://host.docker.internal:1234/v1`. Studio thêm
`/chat/completions` hoặc `/models`, không tự thêm `/v1`. Không nhập query, fragment
hoặc credentials trong URL. Bearer key không bắt buộc cho server local. Các giao
thức tùy ý, Azure authentication và custom Anthropic endpoints chưa thuộc v1.

1. Lưu key, model và bảng giá input/output đúng model; ô key để trống giữ key cũ.
2. **Kiểm tra xác thực** không gọi tạo nội dung. Discovery không có hoặc thất bại
   không tự suy ra inference không được hỗ trợ; anh vẫn nhập model thủ công.
3. **Thử tạo nội dung** gửi yêu cầu thật và kiểm tra JSON kết quả. Lần thử dùng quota
   hoặc tính phí ngoài ngân sách dự án. Chỉ bấm khi muốn thực hiện lần gọi đó.

Không có credentials thật thì test HTTP chỉ xác minh hợp đồng kỹ thuật, không chứng
minh model, quota hoặc tài khoản của anh có quyền inference.

## ChatGPT / Codex Subscription

Dùng [Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
và [Responses API](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
cho ứng dụng chạy cục bộ. Browser đăng nhập, token được lưu mã hóa phía Studio và
refresh khi cần. Model lấy theo tài khoản; không giả định mọi model API đều nằm trong gói.

- Mở **`http://127.0.0.1:5080`**, đăng nhập Studio, rồi chọn ChatGPT/Codex trong Settings.
- Callback là `http://127.0.0.1:5080/auth/callback`. `localhost` và `127.0.0.1`
  là hai cookie origin khác nhau; không đổi qua lại giữa luồng đăng nhập.
- Docker giữ `127.0.0.1:5080:8080`: port callback là port host, không phải port container.
- Nếu đổi port host, cấu hình `Studio__ChatGptRedirectUri` tương ứng; callback vẫn
  phải là HTTP loopback `127.0.0.1` và đường dẫn `/auth/callback`.
- Lưu khoản dự phòng subscription, đọc/chọn model và thử inference trước khi chạy pipeline.
- Disconnect xóa token khỏi Studio và hủy phiên đăng nhập đang chờ. Có thể thu hồi
  quyền phía tài khoản provider riêng. Đây không phải luồng OAuth YouTube.

Response streaming phải kết thúc thành công; stream bị cắt, incomplete hoặc failed
không được duyệt thành nội dung. Không dùng token subscription để gọi ảnh/giọng đọc.

## Claude subscription qua Claude Code

Studio chạy **binary Claude Code nguyên bản**, không nhập/đọc file token của Claude.
[Hướng dẫn chính thức](https://code.claude.com/docs/en/legal-and-compliance#authentication-and-credential-use)
phân biệt cách này với tích hợp API key. Extra usage do tài khoản Claude quản lý;
Studio không tự bật tính năng đó hoặc chuyển sang Anthropic API key.

- Native: cài Claude Code từ nguồn chính thức; chạy `claude auth login` bằng cùng
  OS user chạy Studio. Chọn executable trong Settings và kiểm tra trạng thái.
- Docker: dùng binary Linux đúng kiến trúc và các thành phần runtime cần thiết,
  đặt trong thư mục `engines/` được mount read-only tại `/opt/engines`. Bản `.exe`
  Windows không chạy trong Linux container. Image Studio không tự tải/cài Claude Code.
- Ví dụ executable `/opt/engines/bin/claude`: đăng nhập bằng
  `docker compose exec studio /opt/engines/bin/claude auth login`.
- Compose đặt `CLAUDE_CONFIG_DIR` và `Studio__ClaudeConfigDirectory` cùng là
  `/data/claude-code`, lưu bền vững trong volume Studio. Thư mục phải ghi được bởi
  user `app` (UID 1654). Tạo thư mục bằng user này nếu chưa có.
- Studio kiểm tra CLI hỗ trợ các isolation flags cần thiết. Nếu yêu cầu nâng cấp,
  nâng cấp binary nguyên bản; không tắt kiểm tra. Dùng safe mode, tools tắt, stdin
  và working directory riêng; không dùng `--bare` vì nó bỏ subscription credentials.
- Disconnect chỉ quên executable trong Studio; không đăng xuất Claude Code của anh.

## SuperGrok OAuth thử nghiệm

Dựa trên [Hermes commit 850efff](https://github.com/NousResearch/hermes-agent/blob/850efff14f0d2a6de205eb1363c35dac35906763/website/docs/guides/xai-grok-oauth.md).
Đây là luồng tương thích cộng đồng dùng public client của Grok CLI, **không phải**
OAuth registration riêng của Studio hoặc cam kết hỗ trợ third-party từ xAI.

- Bấm kết nối, mở link xác nhận, nhập mã nếu được yêu cầu và chờ trạng thái cập nhật.
- Hủy kết nối đang chờ không xóa tài khoản đã kết nối trước đó. Disconnect mới xóa
  credentials Studio đang giữ.
- Đăng nhập thành công không đảm bảo quyền inference: một số tài khoản bị **403**.
  Studio báo rõ, không lặp login và không tự dùng xAI API key thay thế.
- Server dùng `https://auth.x.ai/oauth2/device/code`, `/oauth2/token` và inference
  `https://api.x.ai/v1/responses`. Có thể override public client ID bằng
  `Studio__SuperGrokClientId` khi có client phù hợp; không thay endpoint tùy tiện.
- Cần kiểm thử inference bằng tài khoản thật trước khi coi kết nối sử dụng được.

## Ngân sách và lưu trữ

API trả phí yêu cầu giá input/output theo triệu token, nguồn/phiên bản và ngày xác
nhận. Model trong bước phải khớp model được định giá. Custom local endpoint có thể
nhập giá 0 với nguồn/ngày xác nhận. Kết nối subscription yêu cầu khoản dự phòng USD
**lớn hơn 0 mỗi lần gọi**; tổng dự phòng được giữ trong sổ ngân sách giống tác vụ khác.

Đây là dự toán để kiểm soát pipeline, **không phải spending cap phía provider**.
Subscription quota, phí gói và extra usage không thể suy ra chỉ từ API token usage.
Đối soát theo tài khoản/hóa đơn bằng chức năng hiện có; không tự fallback billing.

Token/key nằm trong `Settings.ProtectedSecret` được ASP.NET Data Protection bảo vệ;
metadata không chứa token. Job snapshot không chụp credentials. Sao lưu toàn bộ
volume `/data`, bao gồm protection keys; không chỉ sao chép SQLite. Settings mới là
các trường JSON bổ sung, không thay schema SQLite v1 hoặc xóa dữ liệu cũ.
