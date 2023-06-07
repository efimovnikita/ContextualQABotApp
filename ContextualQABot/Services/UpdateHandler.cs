using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using ContextualQABot.Abstract;
using Microsoft.Extensions.Logging;
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
            _                                              => UnknownUpdateHandlerAsync(update, cancellationToken)
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
            _ => await SendFileUnsupportedMessage(_botClient, message, cancellationToken)
        };
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
            "/inline_keyboard" => SendInlineKeyboard(_botClient, message, cancellationToken),
            "/keyboard" => SendReplyKeyboard(_botClient, message, cancellationToken),
            "/remove" => RemoveKeyboard(_botClient, message, cancellationToken),
            "/photo" => SendFile(_botClient, message, cancellationToken),
            "/request" => RequestContactAndLocation(_botClient, message, cancellationToken),
            "/inline_mode" => StartInlineQuery(_botClient, message, cancellationToken),
            "/info" => GetUserInfo(_botClient, message, cancellationToken),
            "/reset_key" => ResetUserKey(_botClient, message, cancellationToken),
            "/set_key" => SetUserKey(_botClient, message, split.Length > 1 ? split[1] : "", cancellationToken),
            "/reset_file" => ResetUserFile(_botClient, message, cancellationToken),
            "/throw" => FailingHandler(_botClient, message, cancellationToken),
            _ => Usage(_botClient, message, cancellationToken)
        };

        async Task<Message> ResetUserFile(ITelegramBotClient botClient, Message msg, CancellationToken token)
        {
            int fromId = (int) msg.From!.Id;
            _storeService.ResetFile(fromId);
            
            return await botClient.SendTextMessageAsync(
                chatId: msg.Chat.Id,
                text: "Current file was deleted",
                cancellationToken: token);
        }

        async Task<Message> SetUserKey(ITelegramBotClient botClient, Message message1, string keyComponent,
            CancellationToken cancellationToken1)
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

        async Task<Message> ResetUserKey(ITelegramBotClient botClient, Message message1,
            CancellationToken cancellationToken1)
        {
            int fromId = (int) message1.From!.Id;
            _storeService.ResetOpenAiKey(fromId);

            return await botClient.SendTextMessageAsync(
                chatId: message1.Chat.Id,
                text: "Open AI API key was reset",
                cancellationToken: cancellationToken1);
        }

        async Task<Message> GetUserInfo(ITelegramBotClient botClient, Message msg, CancellationToken token)
        {
            int fromId = (int) msg.From!.Id;
            string userInfo = _storeService.GetUserInfo(fromId);

            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: userInfo,
                cancellationToken: token);
        }

        Message sentMessage = await action;
        _logger.LogInformation("The message was sent with id: {SentMessageId}", sentMessage.MessageId);

        // Send inline keyboard
        // You can process responses in BotOnCallbackQueryReceived handler
        static async Task<Message> SendInlineKeyboard(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            await botClient.SendChatActionAsync(
                chatId: message.Chat.Id,
                chatAction: ChatAction.Typing,
                cancellationToken: cancellationToken);

            // Simulate longer running task
            await Task.Delay(500, cancellationToken);

            InlineKeyboardMarkup inlineKeyboard = new(
                new[]
                {
                    // first row
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("1.1", "11"),
                        InlineKeyboardButton.WithCallbackData("1.2", "12"),
                    },
                    // second row
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("2.1", "21"),
                        InlineKeyboardButton.WithCallbackData("2.2", "22"),
                    },
                });

            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Choose",
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);
        }

        static async Task<Message> SendReplyKeyboard(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            ReplyKeyboardMarkup replyKeyboardMarkup = new(
                new[]
                {
                    new KeyboardButton[] {"1.1", "1.2"},
                    new KeyboardButton[] {"2.1", "2.2"},
                })
            {
                ResizeKeyboard = true
            };

            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Choose",
                replyMarkup: replyKeyboardMarkup,
                cancellationToken: cancellationToken);
        }

        static async Task<Message> RemoveKeyboard(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Removing keyboard",
                replyMarkup: new ReplyKeyboardRemove(),
                cancellationToken: cancellationToken);
        }

        static async Task<Message> SendFile(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            await botClient.SendChatActionAsync(
                message.Chat.Id,
                ChatAction.UploadPhoto,
                cancellationToken: cancellationToken);

            const string filePath = "Files/tux.png";
            await using FileStream fileStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string fileName = filePath.Split(Path.DirectorySeparatorChar).Last();

            return await botClient.SendPhotoAsync(
                chatId: message.Chat.Id,
                photo: new InputFileStream(fileStream, fileName),
                caption: "Nice Picture",
                cancellationToken: cancellationToken);
        }

        static async Task<Message> RequestContactAndLocation(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            ReplyKeyboardMarkup requestReplyKeyboard = new(
                new[]
                {
                    KeyboardButton.WithRequestLocation("Location"),
                    KeyboardButton.WithRequestContact("Contact"),
                });

            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Who or Where are you?",
                replyMarkup: requestReplyKeyboard,
                cancellationToken: cancellationToken);
        }

        static async Task<Message> StartInlineQuery(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            InlineKeyboardMarkup inlineKeyboard = new(
                InlineKeyboardButton.WithSwitchInlineQueryCurrentChat("Inline Mode"));

            return await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: "Press the button to start Inline Query",
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken);
        }

#pragma warning disable RCS1163 // Unused parameter.
#pragma warning disable IDE0060 // Remove unused parameter
        static Task<Message> FailingHandler(ITelegramBotClient botClient, Message message,
            CancellationToken cancellationToken)
        {
            throw new IndexOutOfRangeException();
        }
#pragma warning restore IDE0060 // Remove unused parameter
#pragma warning restore RCS1163 // Unused parameter.
        return;
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
                             "/inline_keyboard - send inline keyboard\n" +
                             "/keyboard    - send custom keyboard\n" +
                             "/remove      - remove custom keyboard\n" +
                             "/photo       - send a photo\n" +
                             "/request     - request location or contact\n" +
                             "/info        - request user info\n" +
                             "/set_key     - set Open AI API key\n" +
                             "/reset_key   - reset Open AI API key\n" +
                             "/reset_file  - reset file\n" +
                             "/inline_mode - send keyboard with Inline Query";

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
    private Task UnknownUpdateHandlerAsync(Update update, CancellationToken cancellationToken)
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
