namespace OF_DL.Helpers
{
    public interface IDBHelper
    {
        Task AddMessage(string folder, long post_id, string message_text, string price, bool is_paid, bool is_archived, DateTime created_at, int user_id);
        Task AddPost(string folder, long post_id, string message_text, string price, bool is_paid, bool is_archived, DateTime created_at);
        Task AddStory(string folder, long post_id, string message_text, string price, bool is_paid, bool is_archived, DateTime created_at);
        Task<DateTime> CreateUserDB(string folder);
        Task CreateUsersDB(Dictionary<string, int> users);
        Task CheckUsername(KeyValuePair<string, int> user, string path);
        Task AddMedia(string folder, long media_id, long post_id, string link, string? directory, string? filename, long? size, string api_type, string media_type, bool preview, bool downloaded, DateTime? created_at);
        Task UpdateMedia(string folder, long media_id, string api_type, string directory, string filename, long size, bool downloaded, DateTime created_at);
        Task<long> GetStoredFileSize(string folder, long media_id, string api_type);
        Task<bool> CheckDownloaded(string folder, long media_id, string api_type);
        Task<DateTime?> GetMostRecentPostDate(string folder);
        Task<string?> GetMostRecentMessageId(string folder);
    }
}
