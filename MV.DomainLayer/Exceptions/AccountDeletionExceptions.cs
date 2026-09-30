namespace MV.DomainLayer.Exceptions
{
    /// <summary>Thiếu hoặc sai mật khẩu xác nhận khi tự xoá tài khoản (400).</summary>
    public class AccountDeletionPasswordException : BadRequestException
    {
        public AccountDeletionPasswordException(string message) : base(message) { }
    }

    /// <summary>Tài khoản nội bộ (Admin/Staff) không tự xoá qua app (403).</summary>
    public class AccountDeletionForbiddenException : Exception
    {
        public AccountDeletionForbiddenException(string message) : base(message) { }
    }

    /// <summary>
    /// Còn nghĩa vụ tiền/học chưa xong (ví còn số dư, khoá học chưa tất toán, rút tiền đang chờ,
    /// khiếu nại chưa đóng) — xoá lúc này sẽ bỏ rơi tiền/buổi học của người khác (409).
    /// </summary>
    public class AccountDeletionBlockedException : Exception
    {
        public IReadOnlyList<string> Blockers { get; }

        public AccountDeletionBlockedException(IReadOnlyList<string> blockers)
            : base("Chưa thể xoá tài khoản. " + string.Join(" ", blockers)
                   + " Vui lòng xử lý xong hoặc liên hệ hỗ trợ (support@toptutor.ai).")
        {
            Blockers = blockers;
        }
    }
}
