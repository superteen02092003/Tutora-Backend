-- Rollback V20261007 — trả tên thương hiệu về Tutora.
--   * Thứ tự ngược lại: email trước, rồi tên miền, rồi tên thương hiệu.
--   * support@toptutor.ai trả về tutoravn@gmail.com (trước V20261007 văn bản chỉ dùng địa chỉ này,
--     không có support@tutora.vn).
--   * Lưu ý: mọi chữ "TopTutor"/"toptutor.ai" admin thêm qua CMS sau V20261007 cũng bị trả về.
BEGIN;

UPDATE policy_documents
   SET content_markdown =
       regexp_replace(
       replace(replace(content_markdown,
           'support@toptutor.ai', 'tutoravn@gmail.com'),
           'toptutor.ai', 'tutora.vn'),
           '\mTopTutor\M', 'Tutora', 'g'),
       title   = regexp_replace(title, '\mTopTutor\M', 'Tutora', 'g'),
       summary = regexp_replace(summary, '\mTopTutor\M', 'Tutora', 'g')
 WHERE slug IN ('terms', 'privacy', 'cookies', 'community-guidelines', 'tutor-agreement',
                'about', 'privacy-app', 'data-deletion')
   AND status <> 'archived';

COMMIT;
