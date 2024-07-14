namespace Schedule_Bot
{
    public class UserInfo
    {
        public long ChatId { get; set; }
        public string Login { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string FileId { get; set; } = string.Empty;
        public bool IsReloginInProgress { get; set; }
        public bool SendSchedule { get; set; }
    }
}
