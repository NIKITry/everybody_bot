using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Microsoft.Extensions.Configuration;
using Serilog;
using Dapper;
using Microsoft.Data.Sqlite;
using System.IO;
using System;
using System.Linq;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateLogger();

var dbPath = "data/bot.db";
using (var connection = new SqliteConnection($"Data Source={dbPath}"))
{
    connection.Open();
    connection.Execute(@"
        CREATE TABLE IF NOT EXISTS users (
            chat_id INTEGER NOT NULL,
            username TEXT NOT NULL,
            PRIMARY KEY (chat_id, username)
        )");
}

const string COMMANDS_MESSAGE = "Команды:\n" +
                              "/add - добавить пользователей\n" +
                              "Пример: /add @username1 @username2\n" +
                              "/all - тегнуть всех\n" +
                              "/clear - очистить список\n" +
                              "А больше ничего тупые эникейщики в меня не засунули";

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()
    .Build();

string? botToken = configuration["TelegramBotToken"];
string? targetWord = configuration["TARGET_WORD"] ?? "людей";
int probability = int.TryParse(configuration["PROBABILITY"], out var prob) ? prob : 5;

if (string.IsNullOrEmpty(botToken))
{
    Log.Warning("Токен бота не найден ни в переменных окружения, ни в User Secrets");
    return;
}

var botClient = new TelegramBotClient(botToken);
using CancellationTokenSource cts = new();
ReceiverOptions receiverOptions = new() { AllowedUpdates = [] };

botClient.StartReceiving(
    HandleUpdateAsync,
    HandlePollingErrorAsync,
    receiverOptions,
    cts.Token
);

var me = await botClient.GetMe(cts.Token);
Log.Information("Начал слушать @{Username}", me.Username);
await Task.Delay(Timeout.Infinite);

async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
    if (update.Message is not { } message || message.Text is not { } messageText) return;
    var chatId = message.Chat.Id;

    if (!messageText.StartsWith("/"))
    {
        if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
        {
            var random = new Random();
            if (random.Next(1, 101) <= probability)
            {
                var longWords = messageText.Split(' ')
                    .Where(word => word.Length >= 5)
                    .ToList();

                if (longWords.Any())
                {
                    var randomWord = longWords[random.Next(longWords.Count)];
                    
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: $"{randomWord} для {targetWord}",
                        replyParameters: new ReplyParameters { MessageId = message.MessageId },
                        cancellationToken: cancellationToken
                    );
                    return;
                }
            }
        }
    }

    if (messageText.StartsWith("/start"))
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: COMMANDS_MESSAGE,
            cancellationToken: cancellationToken
        );
    }
    else if (messageText.StartsWith("/add"))
    {
        if (messageText.Length <= 5)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Укажите никнеймы: /add @username1 @username2\nили ответьте на это сообщение никнеймами",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        var usernames = messageText.Substring(5).Split(' ')
            .Where(u => u.StartsWith("@"))
            .ToList();

        if (usernames.Count == 0)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Укажите никнеймы: /add @username1 @username2\nили ответьте на это сообщение никнеймами",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        var addedUsers = new List<string>();
        foreach (var username in usernames)
        {
            try
            {
                await connection.ExecuteAsync(
                    "INSERT INTO users (chat_id, username) VALUES (@ChatId, @Username)",
                    new { ChatId = chatId, Username = username }
                );
                addedUsers.Add(username);
            }
            catch (SqliteException)
            {
                continue;
            }
        }

        await botClient.SendMessage(
            chatId: chatId,
            text: addedUsers.Any() ? $"Добавлены: {string.Join(", ", addedUsers)}" : "Все пользователи уже добавлены",
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            cancellationToken: cancellationToken
        );
    }
    else if (message.ReplyToMessage?.From?.Id == botClient.BotId && message.ReplyToMessage?.Text?.Contains("Укажите никнеймы") == true)
    {
        var usernames = messageText.Split(' ')
            .Where(u => u.StartsWith("@"))
            .ToList();

        if (usernames.Count == 0)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Укажите никнеймы через пробел, например: @username1 @username2",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        var addedUsers = new List<string>();
        foreach (var username in usernames)
        {
            try
            {
                await connection.ExecuteAsync(
                    "INSERT INTO users (chat_id, username) VALUES (@ChatId, @Username)",
                    new { ChatId = chatId, Username = username }
                );
                addedUsers.Add(username);
            }
            catch (SqliteException)
            {
                continue;
            }
        }

        await botClient.SendMessage(
            chatId: chatId,
            text: $"Добавлены: {string.Join(", ", addedUsers)}",
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            cancellationToken: cancellationToken
        );
    }
    else if (messageText.StartsWith("/all"))
    {
        if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
        {
            var chatMember = await botClient.GetChatMember(chatId, botClient.BotId, cancellationToken);

            if (chatMember.Status != ChatMemberStatus.Administrator)
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Сделайте бота администратором",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
            }
            else
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                var users = await connection.QueryAsync<string>(
                    "SELECT username FROM users WHERE chat_id = @ChatId",
                    new { ChatId = chatId }
                );

                if (users.Any())
                {
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: string.Join(" ", users),
                        cancellationToken: cancellationToken
                    );
                }
                else
                {
                    await botClient.SendMessage(
                        chatId: chatId,
                        text: "Список пуст. Используйте /add",
                        replyParameters: new ReplyParameters { MessageId = message.MessageId },
                        cancellationToken: cancellationToken
                    );
                }
            }
        }
        else
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Только для групп",
                cancellationToken: cancellationToken
            );
        }
    }
    else if (messageText.StartsWith("/clear"))
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        var deleted = await connection.ExecuteAsync(
            "DELETE FROM users WHERE chat_id = @ChatId",
            new { ChatId = chatId }
        );

        await botClient.SendMessage(
            chatId: chatId,
            text: deleted > 0 ? "Список очищен" : "Список уже пуст",
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            cancellationToken: cancellationToken
        );
    }
    else if (message.Entities != null && message.Entities.Any(e => e.Type == MessageEntityType.Mention && messageText.Substring(e.Offset, e.Length) == $"@{botClient.GetMe().Result.Username}"))
    {
        await botClient.SendMessage(
            chatId: chatId,
            text: COMMANDS_MESSAGE,
            replyParameters: new ReplyParameters { MessageId = message.MessageId },
            cancellationToken: cancellationToken
        );
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    Log.Error(exception, "Ошибка при получении обновлений");
    return Task.CompletedTask;
}
