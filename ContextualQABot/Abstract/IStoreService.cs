namespace ContextualQABot.Abstract;

public interface IStoreService
{
    string GetUserInfo(int userId);
    void SetOpenAiKey(int userId, string key);
    void ResetOpenAiKey(int userId);
    string GetOpenAiKey(int userId);
    void SetFile(int userId, string label, FileInfo fileInfo);
    void ResetFile(int userId);
}