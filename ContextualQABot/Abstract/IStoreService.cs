namespace ContextualQABot.Abstract;

public interface IStoreService
{
    string GetUserInfo(int userId);
    void SetOpenAiKey(int userId, string key);
    void ResetOpenAiKey(int userId);
    void SetFile(int userId, FileInfo fileInfo);
    void ResetFile(int userId);
}