namespace ContextualQABot.Models;

public class BotUser
{
    public BotUser(int id, string openAiApiKey = "")
    {
        Id = id;
        OpenAiApiKey = openAiApiKey;
    }
    public int Id { get; }
    public string OpenAiApiKey { get; set; }
}