-- =====================================================
-- V20261007 — Đổi thương hiệu trong văn bản chính sách: Tutora → TopTutor (quyết định 2026-09-29;
-- 2026-09-30: đổi cả tên Zalo Official Account trong văn bản, OA sẽ được đổi tên sau).
--   * Áp dụng cho 8 văn bản: terms, privacy, cookies, community-guidelines, tutor-agreement,
--     about, privacy-app, data-deletion — cột content_markdown, title, summary.
--   * Thứ tự thay trong content_markdown:
--       1. email: tutoravn@gmail.com, support@tutora.vn → support@toptutor.ai;
--       2. tên miền: tutora.vn → toptutor.ai (chỉ còn dạng đường dẫn tutora.vn/policies/…);
--       3. tên thương hiệu: "Tutora" nguyên từ → "TopTutor" (kể cả "Ví Tutora", "Tutora's",
--          "Zalo Official Account của Tutora").
--   * Vì sao thay nguyên từ bằng regexp_replace('\mTutora\M') thay vì từng cụm replace():
--     8 văn bản có ~93 lần nhắc "Tutora", viết từng cặp sẽ dài và dễ sót. Đã đọc toàn văn bản
--     đang chạy (api.toptutor.ai/api/policies/<slug>, 2026-09-30): mọi lần "Tutora" đều là tên
--     sản phẩm/thương hiệu, đứng riêng một từ; không có tên nội bộ, không có đường dẫn cần giữ.
--   * Giữ nguyên "Dream Lab AI". Không đổi văn bản đồng ý của phụ huynh trong code — migration này
--     chỉ sửa policy_documents.
--   * Không đụng bản đã lưu trữ (status = 'archived'): đó là văn bản người dùng đã đồng ý lúc trước.
--   * Không nâng version: chỉ đổi tên, không đổi nội dung cam kết; nâng version sẽ buộc người dùng
--     đồng ý lại (user_policy_acceptances khoá theo slug + version).
--   * Chạy lại không đổi gì thêm: sau lần đầu không còn chuỗi nào khớp.
-- =====================================================
BEGIN;

UPDATE policy_documents
   SET content_markdown =
       regexp_replace(
       replace(replace(replace(content_markdown,
           'tutoravn@gmail.com', 'support@toptutor.ai'),
           'support@tutora.vn', 'support@toptutor.ai'),
           'tutora.vn', 'toptutor.ai'),
           '\mTutora\M', 'TopTutor', 'g'),
       title   = regexp_replace(title, '\mTutora\M', 'TopTutor', 'g'),
       summary = regexp_replace(summary, '\mTutora\M', 'TopTutor', 'g')
 WHERE slug IN ('terms', 'privacy', 'cookies', 'community-guidelines', 'tutor-agreement',
                'about', 'privacy-app', 'data-deletion')
   AND status <> 'archived';

COMMIT;
