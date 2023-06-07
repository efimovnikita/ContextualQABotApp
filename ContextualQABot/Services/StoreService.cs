using System.Reflection;
using ContextualQABot.Abstract;
using ContextualQABot.Models;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace ContextualQABot.Services;

public class StoreService : IStoreService
{
    private const string FilesCollectionName = "files";
    private const string ChunksCollectionName = "chunks";
    private const string UsersCollectionName = "users";
    private readonly ILogger<StoreService> _logger;
    private readonly string _connection;
    public StoreService(ILogger<StoreService> logger)
    {
        _logger = logger;
        
        string? execPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        string? dbDir = Environment.GetEnvironmentVariable("DB_DIR");
        string? dbName = Environment.GetEnvironmentVariable("DB_NAME");

        if (new [] { dbDir, dbName }.Any(String.IsNullOrEmpty))
        {
            throw new ArgumentException("DB_DIR or DB_NAME env vars didn't set");
        }

        string connection = Path.Combine(execPath!, dbDir!, dbName!);
        _logger.LogInformation("DB connection string is: {Con}", connection);
        _connection = connection;
    }

    public string GetUserInfo(int userId)
    {
        string UserInfo()
        {
            string key = GetKey();
            string fileName = GetFileName();
            return $"*Your Open AI API Key:* ```{key}```\n" + $"*Current file:* ```{fileName}```";
        }

        using LiteDatabase db = new(_connection);
        ILiteCollection<BotUser>? col = db.GetCollection<BotUser>(UsersCollectionName);
        
        _logger.LogInformation("Trying to find user with id - {ID}", userId);
        BotUser? botUser = col.FindOne(user => user.Id.Equals(userId));
        if (botUser != null)
        {
            return UserInfo();
        }

        _logger.LogInformation("User didn't found. Creating new user");
        botUser = new BotUser(userId);
        col.Insert(botUser);

        return UserInfo();

        string GetKey()
        {
            return String.IsNullOrWhiteSpace(botUser.OpenAiApiKey) == false ?
                botUser.OpenAiApiKey : 
                "<not set>";
        }

        string GetFileName()
        {
            ILiteStorage<string>? fs = db.GetStorage<string>(FilesCollectionName, ChunksCollectionName);
            LiteFileInfo<string>? fileInfo = GetUserFiles(fs, userId).FirstOrDefault();
            return fileInfo == null ? "<not set>" : fileInfo.Metadata["name"];
        }
    }

    public void SetOpenAiKey(int userId, string key)
    {
        SetKey(userId, key);
    }

    private void SetKey(int userId, string key)
    {
        using LiteDatabase db = new(_connection);
        ILiteCollection<BotUser>? col = db.GetCollection<BotUser>(UsersCollectionName);        
        _logger.LogInformation("Trying to find user with id - {ID}", userId);
        BotUser? botUser = col.FindOne(user => user.Id.Equals(userId));
        if (botUser == null)
        {
            botUser = new BotUser(userId, key);
            col.Insert(botUser);

            return;
        }

        botUser.OpenAiApiKey = key;
        col.Update(botUser);
    }

    public void ResetOpenAiKey(int userId)
    {
        SetKey(userId, "");
    }

    public string GetOpenAiKey(int userId)
    {
        using LiteDatabase db = new(_connection);
        ILiteCollection<BotUser>? col = db.GetCollection<BotUser>(UsersCollectionName);        
        _logger.LogInformation("Trying to find user with id - {ID}", userId);
        BotUser? botUser = col.FindOne(user => user.Id.Equals(userId));
        if (botUser != null)
        {
            return botUser.OpenAiApiKey;
        }

        botUser = new BotUser(userId);
        col.Insert(botUser);

        return "";
    }

    public void SetFile(int userId, string label, FileInfo fileInfo)
    {
        using LiteDatabase db = new(_connection);
        ILiteCollection<BotUser>? col = db.GetCollection<BotUser>(UsersCollectionName);      
        _logger.LogInformation("Trying to find user with id - {ID}", userId);
        BotUser? botUser = col.FindOne(user => user.Id.Equals(userId));
        if (botUser == null)
        {
            botUser = new BotUser(userId);
            col.Insert(botUser);
        }
        
        ILiteStorage<string>? fs = db.GetStorage<string>(FilesCollectionName, ChunksCollectionName);
        DeleteExistingFiles(fs, userId);

        LiteFileInfo<string> liteFileInfo = fs.Upload(id: $"$/files/{userId}/{fileInfo.Name}", filename: fileInfo.FullName);
        fs.SetMetadata(liteFileInfo.Id, new BsonDocument { { "name", new BsonValue(label)} });
    }

    private void DeleteExistingFiles(ILiteStorage<string> liteStorage, int userId)
    {
        LiteFileInfo<string>[] userFiles = GetUserFiles(liteStorage, userId);
        foreach (LiteFileInfo<string> file in userFiles)
        {
            liteStorage.Delete(file.Id);
        }
    }

    public void ResetFile(int userId)
    {
        using LiteDatabase db = new(_connection);
        ILiteStorage<string>? fs = db.GetStorage<string>(FilesCollectionName, ChunksCollectionName);
        DeleteExistingFiles(fs, userId);
    }

    public bool IsUserFileExist(int userId)
    {
        using LiteDatabase db = new(_connection);
        ILiteStorage<string>? fs = db.GetStorage<string>(FilesCollectionName, ChunksCollectionName);
        LiteFileInfo<string>? fileInfo = GetUserFiles(fs, userId).FirstOrDefault();
        return fileInfo != null;
    }

    public bool SaveUserFile(int userId, string fullPath)
    {
        using LiteDatabase db = new(_connection);
        ILiteStorage<string>? fs = db.GetStorage<string>(FilesCollectionName, ChunksCollectionName);
        LiteFileInfo<string>? fileInfo = GetUserFiles(fs, userId).FirstOrDefault();
        if (fileInfo == null)
        {
            return false;
        }
        
        fileInfo.SaveAs(fullPath);
        return File.Exists(fullPath);
    }

    private static LiteFileInfo<string>[] GetUserFiles(ILiteStorage<string> liteStorage, int userId)
    {
        return liteStorage.Find(info => info.Id.Contains(userId.ToString())).ToArray();
    }
}