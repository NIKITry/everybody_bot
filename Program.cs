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

namespace TelegramBot;

public class UserSettings
{
    public long ChatId { get; set; }
    public int RudeModeEnabled { get; set; }
    public string? RudeWord { get; set; }
}

class Program
{
    private static string dbPath = "data/bot.db";
    private static string targetWord = "людей";
    private static int probability = 5;
    private const string COMMANDS_MESSAGE = "Команды:\n" +
                                          "/add - добавить пользователей\n" +
                                          "Пример: /add @username1 @username2\n" +
                                          "/all - тегнуть всех\n" +
                                          "/clear - очистить список\n" +
                                          "/rude_mode_enable - включить режим грубости\n" +
                                          "/rude_mode_disable - выключить режим грубости\n" +
                                          "/set_rude_word - установить слово для ответа\n" +
                                          "А больше ничего тупые эникейщики в меня не засунули";

    static async Task Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .CreateLogger();

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            connection.Open();
            Log.Information("Создаем таблицу users");
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS users (
                    chat_id INTEGER NOT NULL,
                    username TEXT NOT NULL,
                    PRIMARY KEY (chat_id, username)
                )");
            
            Log.Information("Создаем таблицу user_settings");
            connection.Execute(@"
                CREATE TABLE IF NOT EXISTS user_settings (
                    chat_id INTEGER NOT NULL,
                    rude_mode_enabled INTEGER NOT NULL DEFAULT 0,
                    rude_word TEXT,
                    PRIMARY KEY (chat_id)
                )");

            Log.Information("Проверяем структуру таблицы user_settings");
            var tableInfo = connection.Query("PRAGMA table_info(user_settings)");
            foreach (var column in tableInfo)
            {
                Log.Information("Колонка: {Name}, Тип: {Type}, Nullable: {Nullable}", 
                    column.name, column.type, column.notnull == 0);
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        string? botToken = configuration["TelegramBotToken"];
        targetWord = configuration["TARGET_WORD"] ?? "людей";
        probability = int.TryParse(configuration["PROBABILITY"], out var prob) ? prob : 5;

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
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message || message.Text is not { } messageText) return;
        var chatId = message.Chat.Id;

        if (!messageText.StartsWith("/"))
        {
            if (message.Chat.Type == ChatType.Group || message.Chat.Type == ChatType.Supergroup)
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                Log.Information("Получаем настройки для чата {ChatId}", chatId);
                
                var settings = await connection.QueryFirstOrDefaultAsync<UserSettings>(
                    "SELECT chat_id as ChatId, rude_mode_enabled as RudeModeEnabled, rude_word as RudeWord FROM user_settings WHERE chat_id = @ChatId",
                    new { ChatId = chatId }
                );

                Log.Information("Настройки для чата {ChatId}: rude_mode_enabled={RudeModeEnabled}, rude_word={RudeWord}", 
                    chatId, settings?.RudeModeEnabled, settings?.RudeWord);

                if (settings?.RudeModeEnabled == 1)
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
                            var responseWord = settings.RudeWord ?? targetWord;
                            
                            Log.Information("Отправляем ответ: {RandomWord} для {ResponseWord}", randomWord, responseWord);
                            
                            await botClient.SendMessage(
                                chatId: chatId,
                                text: $"{randomWord} для {responseWord}",
                                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                                cancellationToken: cancellationToken
                            );
                            return;
                        }
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
        else if (messageText.StartsWith("/rude_mode_enable"))
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            
            await connection.ExecuteAsync(@"
                INSERT INTO user_settings (chat_id, rude_mode_enabled) 
                VALUES (@ChatId, 1)
                ON CONFLICT(chat_id) DO UPDATE SET rude_mode_enabled = 1",
                new { ChatId = chatId }
            );

            await botClient.SendMessage(
                chatId: chatId,
                text: "Режим грубости включен",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
        }
        else if (messageText.StartsWith("/rude_mode_disable"))
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            
            await connection.ExecuteAsync(@"
                INSERT INTO user_settings (chat_id, rude_mode_enabled) 
                VALUES (@ChatId, 0)
                ON CONFLICT(chat_id) DO UPDATE SET rude_mode_enabled = 0",
                new { ChatId = chatId }
            );

            await botClient.SendMessage(
                chatId: chatId,
                text: "Режим грубости выключен",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
        }
        else if (messageText.StartsWith("/set_rude_word"))
        {
            await HandleSetRudeWordCommand(botClient, message, cancellationToken);
        }
        else if (message.ReplyToMessage?.From?.Id == botClient.BotId && message.ReplyToMessage?.Text?.Contains("Ответьте на это сообщение словом") == true)
        {
            var word = messageText.Trim();
            
            if (string.IsNullOrEmpty(word))
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Укажите слово",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
                return;
            }

            if (word.Length > 20)
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Слово должно быть не длиннее 20 символов",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
                return;
            }

            if (!word.All(char.IsLetter))
            {
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Слово должно содержать только буквы",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
                return;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                await connection.OpenAsync();
                
                Log.Information("Сохраняем слово '{Word}' для чата {ChatId}", word, chatId);
                
                var existingSettings = await connection.QueryFirstOrDefaultAsync<UserSettings>(
                    "SELECT chat_id as ChatId, rude_mode_enabled as RudeModeEnabled, rude_word as RudeWord FROM user_settings WHERE chat_id = @ChatId",
                    new { ChatId = chatId }
                );
                
                if (existingSettings != null)
                {
                    Log.Information("Существующие настройки: rude_mode_enabled={RudeModeEnabled}, rude_word={RudeWord}", 
                        existingSettings.RudeModeEnabled, existingSettings.RudeWord);
                    
                    var result = await connection.ExecuteAsync(@"
                        UPDATE user_settings 
                        SET rude_word = @Word 
                        WHERE chat_id = @ChatId",
                        new { ChatId = chatId, Word = word }
                    );
                    
                    Log.Information("Слово обновлено, результат: {Result}", result);
                }
                else
                {
                    Log.Information("Настройки для чата {ChatId} не найдены, создаем новую запись", chatId);
                    
                    var result = await connection.ExecuteAsync(@"
                        INSERT INTO user_settings (chat_id, rude_mode_enabled, rude_word) 
                        VALUES (@ChatId, 0, @Word)",
                        new { ChatId = chatId, Word = word }
                    );
                    
                    Log.Information("Слово сохранено, результат: {Result}", result);
                }

                // Проверяем, что слово действительно сохранилось
                var rawSettings = await connection.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT * FROM user_settings WHERE chat_id = @ChatId",
                    new { ChatId = chatId }
                );
                
                Log.Information("Сырые данные из БД: {@RawSettings}", rawSettings);

                var updatedSettings = await connection.QueryFirstOrDefaultAsync<UserSettings>(
                    "SELECT chat_id as ChatId, rude_mode_enabled as RudeModeEnabled, rude_word as RudeWord FROM user_settings WHERE chat_id = @ChatId",
                    new { ChatId = chatId }
                );
                
                Log.Information("Обновленные настройки: rude_mode_enabled={RudeModeEnabled}, rude_word={RudeWord}", 
                    updatedSettings?.RudeModeEnabled, updatedSettings?.RudeWord);

                await botClient.SendMessage(
                    chatId: chatId,
                    text: $"Слово '{word}' установлено",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Ошибка при сохранении слова");
                await botClient.SendMessage(
                    chatId: chatId,
                    text: "Произошла ошибка при сохранении слова",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken
                );
            }
            return;
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

    static async Task HandleSetRudeWordCommand(ITelegramBotClient botClient, Message message, CancellationToken cancellationToken)
    {
        var chatId = message.Chat.Id;
        var word = message.Text?.Replace("/set_rude_word", "").Trim();

        if (string.IsNullOrEmpty(word))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Укажите слово после команды /set_rude_word",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        if (word.Length > 20)
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Слово должно быть не длиннее 20 символов",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        if (!word.All(char.IsLetter))
        {
            await botClient.SendMessage(
                chatId: chatId,
                text: "Слово должно содержать только буквы",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
            return;
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync();
            
            var existingSettings = await connection.QueryFirstOrDefaultAsync<UserSettings>(
                "SELECT chat_id as ChatId, rude_mode_enabled as RudeModeEnabled, rude_word as RudeWord FROM user_settings WHERE chat_id = @ChatId",
                new { ChatId = chatId }
            );
            
            if (existingSettings != null)
            {
                await connection.ExecuteAsync(@"
                    UPDATE user_settings 
                    SET rude_word = @Word 
                    WHERE chat_id = @ChatId",
                    new { ChatId = chatId, Word = word }
                );
            }
            else
            {
                await connection.ExecuteAsync(@"
                    INSERT INTO user_settings (chat_id, rude_mode_enabled, rude_word) 
                    VALUES (@ChatId, 0, @Word)",
                    new { ChatId = chatId, Word = word }
                );
            }

            await botClient.SendMessage(
                chatId: chatId,
                text: $"Слово '{word}' установлено",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка при сохранении слова");
            await botClient.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при сохранении слова",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: cancellationToken
            );
        }
    }

    static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        Log.Error(exception, "Ошибка при получении обновлений");
        return Task.CompletedTask;
    }
}
