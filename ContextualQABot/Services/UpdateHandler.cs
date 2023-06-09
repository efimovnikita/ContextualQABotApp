using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ContextualQABot.Abstract;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using File = Telegram.Bot.Types.File;

namespace ContextualQABot.Services;

public class UpdateHandler : IUpdateHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<UpdateHandler> _logger;
    private readonly IStoreService _storeService;

    public UpdateHandler(ITelegramBotClient botClient, ILogger<UpdateHandler> logger, IStoreService storeService)
    {
        _botClient = botClient;
        _logger = logger;
        _storeService = storeService;
    }

    public async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken cancellationToken)
    {
        Task handler = update switch
        {
            // UpdateType.Unknown:
            // UpdateType.ChannelPost:
            // UpdateType.EditedChannelPost:
            // UpdateType.ShippingQuery:
            // UpdateType.PreCheckoutQuery:
            // UpdateType.Poll:
            { Message: { } message }                       => BotOnMessageReceived(message, cancellationToken),
            { EditedMessage: { } message }                 => BotOnMessageReceived(message, cancellationToken),
            { CallbackQuery: { } callbackQuery }           => BotOnCallbackQueryReceived(callbackQuery, cancellationToken),
            { InlineQuery: { } inlineQuery }               => BotOnInlineQueryReceived(inlineQuery, cancellationToken),
            { ChosenInlineResult: { } chosenInlineResult } => BotOnChosenInlineResultReceived(chosenInlineResult, cancellationToken),
            _                                              => UnknownUpdateHandlerAsync(update)
        };

        await handler;
    }

    private async Task BotOnMessageReceived(Message message, CancellationToken cancellationToken)
    {
        MessageType messageType = message.Type;
        _logger.LogInformation("Receive message type: {MessageType}", messageType);

        switch (messageType)
        {
            case MessageType.Text:
            {
                await ProcessText(message, cancellationToken);
                return;
            }
            case MessageType.Document:
                await ProcessDocument(message, cancellationToken);
                break;
            default:
                await Usage(_botClient, message, cancellationToken);
                break;
        }
    }

    private async Task ProcessDocument(Message message, CancellationToken cancellationToken)
    {
        if (message.Document is not { } messageDocument)
        {
            return;
        }
        
        int fromId = (int) message.From!.Id;
        string openAiKey = _storeService.GetOpenAiKey(fromId);
        if (String.IsNullOrWhiteSpace(openAiKey) || IsValidPattern(openAiKey) == false)
        {
            await _botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "You must set an Open AI API key",
                cancellationToken: cancellationToken);
            return;
        }

        string? mimeType = messageDocument.MimeType;
        _logger.LogInformation("Received document type is: {DocType}", mimeType);
        _ = mimeType switch
        {
            "text/plain" => await ProcessPlainTextDocument(_botClient, message, openAiKey, cancellationToken),
            "application/vnd.ms-htmlhelp" => await ProcessChmDocument(_botClient, message, openAiKey, cancellationToken),
            "application/pdf" => await ProcessPdfDocument(_botClient, message, openAiKey, cancellationToken),
            _ => await SendFileUnsupportedMessage(_botClient, message, cancellationToken)
        };
    }

    private async Task<Message> ProcessPdfDocument(ITelegramBotClient botClient, Message msg, string key, CancellationToken token)
    {
        try
        {
            File file = await botClient.GetFileAsync(msg.Document!.FileId, cancellationToken: token);

            string execAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            string tempDirectoryPath;
    #if RELEASE
                tempDirectoryPath = Path.GetTempPath();
    #endif
    #if DEBUG
            tempDirectoryPath = execAssemblyDir;
    #endif
            string randomSubfolderName = Path.GetRandomFileName(); // Generate a random folder name
            string subDirectoryPath = Path.Combine(tempDirectoryPath, randomSubfolderName);

            // Create the subdirectory.
            Directory.CreateDirectory(subDirectoryPath);
                
            // Create sources folder inside temp dir
            string sourcesDir = Path.Combine(subDirectoryPath, "sources");
            Directory.CreateDirectory(sourcesDir);

            string filePath = Path.Combine(sourcesDir, msg.Document.FileName!);

            using (FileStream fileStream = System.IO.File.Open(filePath, FileMode.Create))
            {
                await botClient.DownloadFileAsync(file.FilePath!, fileStream, token);
            }

            if (System.IO.File.Exists(filePath) == false)
            {
    #if RELEASE
                    Directory.Delete(subDirectoryPath, true);
    #endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }

            const string scriptFilename = "create_storage_from_pdf.py";
            const string scriptsFolderName = "Scripts";
            string scriptSourcePath = Path.Combine(execAssemblyDir, scriptsFolderName, scriptFilename);
                
            if (System.IO.File.Exists(scriptSourcePath) == false)
            {
    #if RELEASE
                    Directory.Delete(subDirectoryPath, true);
    #endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }
                
            string scriptDestPath = Path.Combine(subDirectoryPath, scriptFilename);
            System.IO.File.Copy(scriptSourcePath, scriptDestPath);

            string? pythonExec = Environment.GetEnvironmentVariable("PYTHON");
            if (String.IsNullOrWhiteSpace(pythonExec))
            {
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Python env var bot set. Try again",
                    cancellationToken: token);
            }

            Command cmd = Cli.Wrap("/bin/bash")
                .WithWorkingDirectory(subDirectoryPath)
                .WithArguments(
                    $"-c \"{pythonExec} {scriptFilename} --filename '{msg.Document.FileName}' --key '{key}'\"");
            await cmd.ExecuteBufferedAsync();

            const string dbFolderName = "db";
            string dbFolderPath = Path.Combine(subDirectoryPath, dbFolderName);
                
            if (Directory.Exists(dbFolderPath) == false)
            {
    #if RELEASE
                    Directory.Delete(subDirectoryPath, true);
    #endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }

            string dbArchivePath = Path.Combine(subDirectoryPath, $"{dbFolderName}.zip");
            ZipFile.CreateFromDirectory(dbFolderPath, dbArchivePath);

            if (System.IO.File.Exists(dbArchivePath) == false)
            {
    #if RELEASE
                    Directory.Delete(subDirectoryPath, true);
    #endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }
                
            _storeService.SetFile((int) msg.From!.Id, 
                Path.GetFileName(filePath), 
                new FileInfo(dbArchivePath));
                
    #if RELEASE
                Directory.Delete(subDirectoryPath, true);
    #endif

            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "File was set",
                cancellationToken: token);
        }
        catch (Exception e)
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: e.Message,
                cancellationToken: token);
        }
    }

    private async Task<Message> ProcessChmDocument(ITelegramBotClient botClient, Message msg, string key, CancellationToken token)
    {
        try
        {
            File file = await botClient.GetFileAsync(msg.Document!.FileId, cancellationToken: token);

            string execAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            string tempDirectoryPath;
#if RELEASE
            tempDirectoryPath = Path.GetTempPath();
#endif
#if DEBUG
            tempDirectoryPath = execAssemblyDir;
#endif
            string randomSubfolderName = Path.GetRandomFileName(); // Generate a random folder name
            string subDirectoryPath = Path.Combine(tempDirectoryPath, randomSubfolderName);

            // Create the subdirectory.
            Directory.CreateDirectory(subDirectoryPath);
            
            // Create sources folder inside temp dir
            string sourcesDir = Path.Combine(subDirectoryPath, "sources");
            Directory.CreateDirectory(sourcesDir);

            string filePath = Path.Combine(sourcesDir, msg.Document.FileName!);

            using (FileStream fileStream = System.IO.File.Open(filePath, FileMode.Create))
            {
                await botClient.DownloadFileAsync(file.FilePath!, fileStream, token);
            }

            if (System.IO.File.Exists(filePath) == false)
            {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }

            const string scriptFilename = "create_storage_from_htms.py";
            const string scriptsFolderName = "Scripts";
            string scriptSourcePath = Path.Combine(execAssemblyDir, scriptsFolderName, scriptFilename);
            
            if (System.IO.File.Exists(scriptSourcePath) == false)
            {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }
            
            string scriptDestPath = Path.Combine(subDirectoryPath, scriptFilename);
            System.IO.File.Copy(scriptSourcePath, scriptDestPath);

            string? pythonExec = Environment.GetEnvironmentVariable("PYTHON");
            if (String.IsNullOrWhiteSpace(pythonExec))
            {
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Python env var bot set. Try again",
                    cancellationToken: token);
            }

            string decompiledDirName = "decompiled";
            string decompiledDir = Path.Combine(sourcesDir, decompiledDirName);

            Command decompileCommand = Cli.Wrap("/bin/bash")
                .WithWorkingDirectory(sourcesDir)
                .WithArguments(
                    $"-c \"extract_chmLib {msg.Document.FileName} {decompiledDirName}\"");
            await decompileCommand.ExecuteBufferedAsync();

            if (Directory.Exists(decompiledDir) == false)
            {
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }

            const string pureHtmlDirName = "pure_html";
            string pureHtmlDir = Path.Combine(sourcesDir, pureHtmlDirName);

            CopyHtmlFiles(decompiledDir, pureHtmlDir);

            Command cmd = Cli.Wrap("/bin/bash")
                .WithWorkingDirectory(subDirectoryPath)
                .WithArguments(
                    $"-c \"{pythonExec} {scriptFilename} --folder '{pureHtmlDir}' --key '{key}'\"");
            await cmd.ExecuteBufferedAsync();
        
            const string dbFolderName = "db";
            string dbFolderPath = Path.Combine(subDirectoryPath, dbFolderName);
            
            if (Directory.Exists(dbFolderPath) == false)
            {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }

            string dbArchivePath = Path.Combine(subDirectoryPath, $"{dbFolderName}.zip");
            ZipFile.CreateFromDirectory(dbFolderPath, dbArchivePath);

            if (System.IO.File.Exists(dbArchivePath) == false)
            {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
                return await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: "Error setting file. Try again",
                    cancellationToken: token);
            }
            
            _storeService.SetFile((int) msg.From!.Id, 
                Path.GetFileName(filePath), 
                new FileInfo(dbArchivePath));
            
#if RELEASE
            Directory.Delete(subDirectoryPath, true);
#endif

            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "File was set",
                cancellationToken: token);
        }
        catch (Exception e)
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: e.Message,
                cancellationToken: token);
        }
    }

    private static void CopyHtmlFiles(string sourceDir, string targetDir)
    {
        // Ensure target directory exists
        Directory.CreateDirectory(targetDir);

        // Copy all the .html and .htm files & Replaces any files with the same name
        foreach (string file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                     .Where(file => new[] { ".html", ".htm" }.Contains(Path.GetExtension(file))))
        {
            string fileName = Path.GetFileName(file);
            System.IO.File.Copy(file, Path.Combine(targetDir, fileName), true);
        }
    }

    private async Task<Message> SendFileUnsupportedMessage(ITelegramBotClient botClient, Message msg, CancellationToken token)
    {
        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "This file type is unsupported",
            cancellationToken: token);
    }

    private async Task<Message> ProcessPlainTextDocument(ITelegramBotClient botClient, Message msg, string key, CancellationToken token)
    {
        File file = await botClient.GetFileAsync(msg.Document!.FileId, cancellationToken: token);

        string execAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string tempDirectoryPath;
#if RELEASE
            tempDirectoryPath = Path.GetTempPath();
#endif
#if DEBUG
        tempDirectoryPath = execAssemblyDir;
#endif
        string randomSubfolderName = Path.GetRandomFileName(); // Generate a random folder name
        string subDirectoryPath = Path.Combine(tempDirectoryPath, randomSubfolderName);

        // Create the subdirectory.
        Directory.CreateDirectory(subDirectoryPath);
            
        // Create sources folder inside temp dir
        string sourcesDir = Path.Combine(subDirectoryPath, "sources");
        Directory.CreateDirectory(sourcesDir);

        string filePath = Path.Combine(sourcesDir, msg.Document.FileName!);

        using (FileStream fileStream = System.IO.File.Open(filePath, FileMode.Create))
        {
            await botClient.DownloadFileAsync(file.FilePath!, fileStream, token);
        }

        if (System.IO.File.Exists(filePath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error setting file. Try again",
                cancellationToken: token);
        }

        const string scriptFilename = "create_storage_from_txt.py";
        const string scriptsFolderName = "Scripts";
        string scriptSourcePath = Path.Combine(execAssemblyDir, scriptsFolderName, scriptFilename);
            
        if (System.IO.File.Exists(scriptSourcePath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error setting file. Try again",
                cancellationToken: token);
        }
            
        string scriptDestPath = Path.Combine(subDirectoryPath, scriptFilename);
        System.IO.File.Copy(scriptSourcePath, scriptDestPath);

        string? pythonExec = Environment.GetEnvironmentVariable("PYTHON");
        if (String.IsNullOrWhiteSpace(pythonExec))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error setting file. Python env var bot set. Try again",
                cancellationToken: token);
        }

        Command cmd = Cli.Wrap("/bin/bash")
            .WithWorkingDirectory(subDirectoryPath)
            .WithArguments(
                $"-c \"{pythonExec} {scriptFilename} --filename '{msg.Document.FileName}' --key '{key}'\"");
        await cmd.ExecuteBufferedAsync();

        const string dbFolderName = "db";
        string dbFolderPath = Path.Combine(subDirectoryPath, dbFolderName);
            
        if (Directory.Exists(dbFolderPath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error setting file. Try again",
                cancellationToken: token);
        }

        string dbArchivePath = Path.Combine(subDirectoryPath, $"{dbFolderName}.zip");
        ZipFile.CreateFromDirectory(dbFolderPath, dbArchivePath);

        if (System.IO.File.Exists(dbArchivePath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error setting file. Try again",
                cancellationToken: token);
        }
            
        _storeService.SetFile((int) msg.From!.Id, 
            Path.GetFileName(filePath), 
            new FileInfo(dbArchivePath));
            
#if RELEASE
            Directory.Delete(subDirectoryPath, true);
#endif

        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "File was set",
            cancellationToken: token);
    }

    private async Task ProcessText(Message message, CancellationToken cancellationToken)
    {
        if (message.Text is not { } messageText)
        {
            return;
        }

        string[] split = messageText.Split(' ');
        Task<Message> action = split[0] switch
        {
            "/info" => GetUserInfo(_botClient, message, cancellationToken),
            "/reset_key" => ResetUserKey(_botClient, message, cancellationToken),
            "/set_key" => SetUserKey(_botClient, message, split.Length > 1 ? split[1] : "", cancellationToken),
            "/ask" => AskLlm(_botClient, message, split.Length > 1 ? String.Join(" ", split.Skip(1)) : "", cancellationToken),
            "/search_few" => SearchSimilar(_botClient, message, split.Length > 1 ? String.Join(" ", split.Skip(1)) : "", "/search_few", 4, cancellationToken),
            "/search_many" => SearchSimilar(_botClient, message, split.Length > 1 ? String.Join(" ", split.Skip(1)) : "", "/search_many", 8, cancellationToken),
            "/reset_file" => ResetUserFile(_botClient, message, cancellationToken),
            "/usage" => Usage(_botClient, message, cancellationToken),
            "/formats" => Formats(_botClient, message, cancellationToken),
            "/help" => Help(_botClient, message, cancellationToken),
            _ => Usage(_botClient, message, cancellationToken)
        };

        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);
    }

    private async Task<Message> Help(ITelegramBotClient botClient, Message msg, CancellationToken token)
    {
        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "*Basic bot usage*:\n1\\. Set OpenAI API key: `/set_key <key>`\n2\\. Upload your file \\(drag&drop the file or select from the disk\\)\n3\\. Ask your question: `/ask <question>`\n4\\. Search something: `/search_few <query>`\n5\\. Search in the wider context: `/search_many <query>`",
            parseMode: ParseMode.MarkdownV2,
            replyMarkup: 
            new InlineKeyboardMarkup(InlineKeyboardButton
                .WithUrl("Watch the video about basic bot usage",
                url: "https://youtu.be/oUmGs-An5rI")),
            cancellationToken: token);
    }

    private async Task<Message> Formats(ITelegramBotClient botClient, Message msg, CancellationToken token)
    {
        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "Bot supports next file formats: *.txt, *.pdf, *.chm",
            cancellationToken: token);
    }

    private async Task<Message> GetUserInfo(ITelegramBotClient botClient, Message msg, CancellationToken token)
    {
        int fromId = (int) msg.From!.Id;
        string userInfo = _storeService.GetUserInfo(fromId);

        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            parseMode: ParseMode.MarkdownV2,
            text: userInfo,
            cancellationToken: token);
    }

    private async Task<Message> ResetUserKey(ITelegramBotClient botClient, Message message1, CancellationToken cancellationToken1)
    {
        int fromId = (int) message1.From!.Id;
        _storeService.ResetOpenAiKey(fromId);

        return await botClient.SendTextMessageAsync(
            chatId: message1.Chat.Id,
            text: "Open AI API key was reset",
            cancellationToken: cancellationToken1);
    }

    private async Task<Message> SetUserKey(ITelegramBotClient botClient, Message message1, string keyComponent, CancellationToken cancellationToken1)
    {
        if (IsValidPattern(keyComponent) == false)
        {
            return await botClient.SendTextMessageAsync(
                chatId: message1.Chat.Id,
                text: "Open AI API key has invalid format. Try again",
                cancellationToken: cancellationToken1);
        }

        int fromId = (int) message1.From!.Id;
        _storeService.SetOpenAiKey(fromId, keyComponent);

        return await botClient.SendTextMessageAsync(
            chatId: message1.Chat.Id,
            text: "Open AI API key was set",
            cancellationToken: cancellationToken1);
    }

    private async Task<Message> ResetUserFile(ITelegramBotClient botClient, Message msg, CancellationToken token)
    {
        int fromId = (int) msg.From!.Id;
        _storeService.ResetFile(fromId);
            
        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "Current file was deleted",
            cancellationToken: token);
    }

    private async Task<Message> AskLlm(ITelegramBotClient botClient, Message msg, string query, CancellationToken token)
    {
        int fromId = (int) msg.From!.Id;
        if (_storeService.IsUserFileExist(fromId) == false)
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "You must upload file first",
                cancellationToken: token);
        }

        if (String.IsNullOrWhiteSpace(query))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Type your query after '/ask' keyword. Example '/ask give me an answer'",
                cancellationToken: token);
        }

        string key = _storeService.GetOpenAiKey(fromId);
        if (String.IsNullOrWhiteSpace(key))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "You must set Open AI API key first",
                cancellationToken: token);
        }

        string execAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string tempDirectoryPath;
#if RELEASE
            tempDirectoryPath = Path.GetTempPath();
#endif
#if DEBUG
        tempDirectoryPath = execAssemblyDir;
#endif
        string randomSubfolderName = Path.GetRandomFileName(); // Generate a random folder name
        string subDirectoryPath = Path.Combine(tempDirectoryPath, randomSubfolderName);

        // Create the subdirectory.
        Directory.CreateDirectory(subDirectoryPath);
            
        const string scriptFilename = "load_and_ask.py";
        const string scriptsFolderName = "Scripts";
        string scriptSourcePath = Path.Combine(execAssemblyDir, scriptsFolderName, scriptFilename);
            
        if (System.IO.File.Exists(scriptSourcePath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking LLM. Try again",
                cancellationToken: token);
        }
            
        string scriptDestPath = Path.Combine(subDirectoryPath, scriptFilename);
        System.IO.File.Copy(scriptSourcePath, scriptDestPath);

        string archivePath = Path.Combine(subDirectoryPath, "db.zip");
        bool saveStatus = _storeService.SaveUserFile(fromId, archivePath);
        if (saveStatus == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking LLM. Try again",
                cancellationToken: token);
        }

        string dbDirPath = Path.Combine(subDirectoryPath, "db");
            
        ZipFile.ExtractToDirectory(archivePath, dbDirPath);
        if (Directory.Exists(dbDirPath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking LLM. Try again",
                cancellationToken: token);
        }
            
        string? pythonExec = Environment.GetEnvironmentVariable("PYTHON");
        if (String.IsNullOrWhiteSpace(pythonExec))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking LLM. Python env var bot set. Try again",
                cancellationToken: token);
        }

        BufferedCommandResult cmd = await Cli.Wrap("/bin/bash")
            .WithWorkingDirectory(subDirectoryPath)
            .WithArguments(
                $"-c \"{pythonExec} {scriptFilename} --query '{query}' --key '{key}'\"")
            .ExecuteBufferedAsync();

        string llmOutput = cmd.StandardOutput;

#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif

        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: llmOutput,
            replyToMessageId: msg.MessageId,
            cancellationToken: token);
    }

    private async Task<Message> SearchSimilar(ITelegramBotClient botClient, Message msg, string query, string commandName, int numberOfPieces, CancellationToken token)
    {
        int fromId = (int) msg.From!.Id;
        if (_storeService.IsUserFileExist(fromId) == false)
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "You must upload file first",
                cancellationToken: token);
        }

        if (String.IsNullOrWhiteSpace(query))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: $"Type your query after '{commandName}' keyword. Example '{commandName} give me the similar term'",
                cancellationToken: token);
        }

        string key = _storeService.GetOpenAiKey(fromId);
        if (String.IsNullOrWhiteSpace(key))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "You must set Open AI API key first",
                cancellationToken: token);
        }

        string execAssemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string tempDirectoryPath;
#if RELEASE
            tempDirectoryPath = Path.GetTempPath();
#endif
#if DEBUG
        tempDirectoryPath = execAssemblyDir;
#endif
        string randomSubfolderName = Path.GetRandomFileName(); // Generate a random folder name
        string subDirectoryPath = Path.Combine(tempDirectoryPath, randomSubfolderName);

        // Create the subdirectory.
        Directory.CreateDirectory(subDirectoryPath);
            
        const string scriptFilename = "load_and_find_similarity.py";
        const string scriptsFolderName = "Scripts";
        string scriptSourcePath = Path.Combine(execAssemblyDir, scriptsFolderName, scriptFilename);
            
        if (System.IO.File.Exists(scriptSourcePath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            _logger.LogError("Python script for similarity search didn't found");

            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking for similarity. Try again",
                cancellationToken: token);
        }
            
        string scriptDestPath = Path.Combine(subDirectoryPath, scriptFilename);
        System.IO.File.Copy(scriptSourcePath, scriptDestPath);

        string archivePath = Path.Combine(subDirectoryPath, "db.zip");
        bool saveStatus = _storeService.SaveUserFile(fromId, archivePath);
        if (saveStatus == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking for similarity. Try again",
                cancellationToken: token);
        }

        string dbDirPath = Path.Combine(subDirectoryPath, "db");
            
        ZipFile.ExtractToDirectory(archivePath, dbDirPath);
        if (Directory.Exists(dbDirPath) == false)
        {
#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking for similarity. Try again",
                cancellationToken: token);
        }
            
        string? pythonExec = Environment.GetEnvironmentVariable("PYTHON");
        if (String.IsNullOrWhiteSpace(pythonExec))
        {
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Error asking for similarity. Python env var bot set. Try again",
                cancellationToken: token);
        }

        BufferedCommandResult cmd = await Cli.Wrap("/bin/bash")
            .WithWorkingDirectory(subDirectoryPath)
            .WithArguments(
                $"-c \"{pythonExec} '{scriptFilename}' --query '{query}' --key '{key}' --number {numberOfPieces}\"")
            .ExecuteBufferedAsync();

        string llmOutput = cmd.StandardOutput;

#if RELEASE
                Directory.Delete(subDirectoryPath, true);
#endif
            
        // Deserialize JSON string to a List of dictionary.
        List<Dictionary<string, string>>? deserializedResult = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(llmOutput);

        if (deserializedResult != null)
        {
            foreach (Dictionary<string, string> page in deserializedResult)
            {
                await botClient.SendTextMessageAsync(
                    chatId: msg.Chat.Id,
                    text: page["page_content"],
                    replyToMessageId: msg.MessageId,
                    cancellationToken: token);
            }
        }

        return await botClient.SendTextMessageAsync(
            chatId: msg.Chat.Id,
            text: "... end.",
            replyToMessageId: msg.MessageId,
            cancellationToken: token);
    }

    private static bool IsValidPattern(string input)
    {
        string pattern = @"^sk-[a-zA-Z0-9_-]{48}$"; // Corrected regular expression pattern

        // Create a new Regex object
        Regex r = new(pattern, RegexOptions.None);

        // Validate input string with pattern
        return r.IsMatch(input);
    }

    private static async Task<Message> Usage(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        const string usage = "Usage:\n" +
                             "/info        - request user info\n" +
                             "/set_key     - set Open AI API key\n" +
                             "/reset_key   - reset Open AI API key\n" +
                             "/ask         - ask about something in a context of your file\n" +
                             "/search_few  - command allows you to find a few (4) pieces of text that are similar to your prompt\n" +
                             "/search_many - command enables you to search for many (8) pieces of text similar to your prompt\n" +
                             "/usage       - how to use this bot\n" +
                             "/formats     - list of supported formats\n" +
                             "/help        - help and 'how-to' info\n" +
                             "/reset_file  - reset file\n";

        return await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: usage,
            replyMarkup: new ReplyKeyboardRemove(),
            cancellationToken: cancellationToken);
    }

    // Process Inline Keyboard callback data
    private async Task BotOnCallbackQueryReceived(CallbackQuery callbackQuery, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received inline keyboard callback from: {CallbackQueryId}", callbackQuery.Id);

        await _botClient.AnswerCallbackQueryAsync(
            callbackQueryId: callbackQuery.Id,
            text: $"Received {callbackQuery.Data}",
            cancellationToken: cancellationToken);

        await _botClient.SendTextMessageAsync(
            chatId: callbackQuery.Message!.Chat.Id,
            text: $"Received {callbackQuery.Data}",
            cancellationToken: cancellationToken);
    }

    #region Inline Mode

    private async Task BotOnInlineQueryReceived(InlineQuery inlineQuery, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received inline query from: {InlineQueryFromId}", inlineQuery.From.Id);

        InlineQueryResult[] results = {
            // displayed result
            new InlineQueryResultArticle(
                id: "1",
                title: "TgBots",
                inputMessageContent: new InputTextMessageContent("hello"))
        };

        await _botClient.AnswerInlineQueryAsync(
            inlineQueryId: inlineQuery.Id,
            results: results,
            cacheTime: 0,
            isPersonal: true,
            cancellationToken: cancellationToken);
    }

    private async Task BotOnChosenInlineResultReceived(ChosenInlineResult chosenInlineResult, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Received inline result: {ChosenInlineResultId}", chosenInlineResult.ResultId);

        await _botClient.SendTextMessageAsync(
            chatId: chosenInlineResult.From.Id,
            text: $"You chose result with Id: {chosenInlineResult.ResultId}",
            cancellationToken: cancellationToken);
    }

    #endregion

#pragma warning disable IDE0060 // Remove unused parameter
#pragma warning disable RCS1163 // Unused parameter.
    private Task UnknownUpdateHandlerAsync(Update update)
#pragma warning restore RCS1163 // Unused parameter.
#pragma warning restore IDE0060 // Remove unused parameter
    {
        _logger.LogInformation("Unknown update type: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }

    public async Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        string errorMessage = exception switch
        {
            ApiRequestException apiRequestException => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        _logger.LogInformation("HandleError: {ErrorMessage}", errorMessage);

        // Cooldown in case of network connection error
        if (exception is RequestException)
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
