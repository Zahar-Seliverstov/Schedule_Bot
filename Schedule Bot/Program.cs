using Schedule_Bot;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class Program
{
    private static ITelegramBotClient botClient;
    private static readonly string APIToken = ConfigurationManager.AppSettings["APIToken"];
    private static readonly string connectionString = ConfigurationManager.ConnectionStrings["connectString"].ConnectionString;
    private static readonly Dictionary<long, UserInfo> tempUserCredentials = new Dictionary<long, UserInfo>();

    public static async Task Main(string[] args)
    {
        await StartBot();
    }
    private static async Task StartBot()
    {
        try
        {
            InitializeBot();
            using var cts = new CancellationTokenSource();
            var me = await botClient.GetMeAsync();

            Console.WriteLine($"# Bot info:\n" +
                $" - id   [{me.Id}]\n" +
                $" - name [@{me.Username}]\n" +
                $"Status: _Start_\n\n");

            await Task.Run(() => ScheduleDailyMessages(cts.Token));

            Console.ReadLine();
            cts.Cancel();
        }
        catch (Exception ex) { Console.WriteLine($"~ ERROR - start bot:\n{ex}"); }
    }
    private static void InitializeBot()
    {
        botClient = new TelegramBotClient(APIToken);
        botClient.StartReceiving(
            HandleUpdate,
            HandleError,
            new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }
        );
    }

    //
    //  Handles
    //

    private static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        try
        {
            if (update.Type == UpdateType.Message && update.Message?.Text != null)
            {
                await HandleMessageAsync(update.Message);
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery?.Data != null)
            {
                await HandleCallbackQueryAsync(update.CallbackQuery);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"*** Ошибка обработки обновления: {ex.Message}");
        }
    }
    private static async Task HandleMessageAsync(Message message)
    {
        try
        {
            var chatId = message.Chat.Id;
            bool processReloginActivate = false;
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            string selectQuery = "SELECT [ReloginProcess] FROM [dbo].[Users] WHERE [ChatId] = @ChatId";

            using (SqlCommand command = new SqlCommand(selectQuery, connection))
            {
                command.Parameters.AddWithValue("@ChatId", chatId);

                bool? reloginProcess = null;

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        reloginProcess = reader.GetBoolean(0);
                    }
                }

                if (reloginProcess.HasValue)
                {
                    Console.WriteLine($"ReloginProcess value: {reloginProcess.Value}");
                    processReloginActivate = reloginProcess.Value;
                }
                else
                {
                    Console.WriteLine("No record found for the given ChatId.");
                }
            }
            if (processReloginActivate)
            {
                await botClient.SendTextMessageAsync(message.Chat.Id, "⚠️ Пожалуйста, завершите действие перед использованием команд.", cancellationToken: default);
                return;
            }
            if (tempUserCredentials.ContainsKey(message.Chat.Id))
            {
                if (message.Text.StartsWith("/") || message.Text == "На сегодня" || message.Text == "На неделю")
                {
                    await botClient.SendTextMessageAsync(message.Chat.Id, "⚠️ Пожалуйста, завершите действие перед использованием команд.", cancellationToken: default);
                    return;
                }
                await HandleUserCredentialsInput(message.Chat.Id, message.Text);
            }
            else
            {
                switch (message.Text)
                {
                    case "/start":
                        await StartCommand(chatId, message.Chat.FirstName, message.Chat.Username);
                        break;
                    case "/relogin":
                        await ReloginCommand(chatId, message.Chat.Username);
                        break;
                    case "На сегодня":
                        await HandleButtonAsync(message);
                        break;
                    case "На неделю":
                        await HandleButtonAsync(message);
                        break;
                    default:
                        await ClearInlineKeyboard(chatId, message.MessageId);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - handling messages\n{ex.Message}");
        }
    }
    private static async Task HandleUserCredentialsInput(long chatId, string input)
    {
        try
        {
            var userInfo = tempUserCredentials[chatId];
            var isUserRegistered = await IsUserRegistered(chatId);

            if (string.IsNullOrEmpty(userInfo.Login))
            {
                if (input.Length > 40)
                {
                    await botClient.SendTextMessageAsync(chatId,
                        "⚠️ <b>Login</b> не может быть длиннее 40 символов.\n" +
                        "Пожалуйста, введите корректный login:",
                        cancellationToken: default);
                    return;
                }

                userInfo.Login = input;
                await botClient.SendTextMessageAsync(chatId,
                    (!isUserRegistered ? "Введите <b>password</b>:" : "Введите новый <b>password</b>:"),
                    cancellationToken: default, parseMode: ParseMode.Html);
            }
            else if (string.IsNullOrEmpty(userInfo.Password))
            {
                if (input.Length > 40)
                {
                    await botClient.SendTextMessageAsync(
                        chatId,
                        text: "⚠️ <b>Password</b> не может быть длиннее 40 символов. Пожалуйста, введите корректный password:",
                        parseMode: ParseMode.Html,
                        cancellationToken: default);
                    return;
                }

                userInfo.Password = input;
                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_credentials"),
                    InlineKeyboardButton.WithCallbackData("Отменить", "cancel_credentials")
                }
            });

                await botClient.SendTextMessageAsync(
                    chatId,
                    $"Авторизоваться используя:\n" +
                    $"👤 login: <b>{userInfo.Login}</b>\n" +
                    $"🔒 password: <b>{userInfo.Password}</b>",
                    parseMode: ParseMode.Html,
                    replyMarkup: inlineKeyboard,
                    cancellationToken: default
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - record entry processing\n{ex.Message}");
        }
    }
    private static async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        try
        {
            var chatId = callbackQuery.Message.Chat.Id;
            var messageId = callbackQuery.Message.MessageId;

            switch (callbackQuery.Data)
            {
                case "start_registration":
                    await ClearInlineKeyboard(chatId, messageId);
                    await PromptUserToEnterLogin(chatId);
                    break;
                case "confirm_relogin":
                    {
                        using var connection = new SqlConnection(connectionString);
                        await connection.OpenAsync();

                        string updateQuery = "UPDATE [dbo].[Users] SET [ReloginProcess] = @ReloginProcess WHERE [ChatId] = @ChatId";

                        using (SqlCommand command = new SqlCommand(updateQuery, connection))
                        {
                            command.Parameters.AddWithValue("@ReloginProcess", false); // or false, depending on your needs
                            command.Parameters.AddWithValue("@ChatId", chatId);

                            int rowsAffected = await command.ExecuteNonQueryAsync();

                            if (rowsAffected > 0)
                            {
                                Console.WriteLine($"{rowsAffected} row(s) updated.");
                            }
                            else
                            {
                                Console.WriteLine("No rows were updated.");
                            }
                        }
                    }
                    await ClearInlineKeyboard(chatId, messageId);
                    tempUserCredentials[chatId] = new UserInfo { ChatId = chatId };
                    await botClient.SendTextMessageAsync(chatId, "Введите новый <b>login</b>:",
                        cancellationToken: default, parseMode: ParseMode.Html);
                    break;
                case "cancel_relogin":
                    await ClearInlineKeyboard(chatId, messageId);
                    await botClient.SendTextMessageAsync(chatId, "Перерегистрация <b>отменена</b>.",
                        cancellationToken: default, parseMode: ParseMode.Html);
                    break;
                case "confirm_credentials":
                    await RegisterOrUpdateUser(tempUserCredentials[chatId]);
                    tempUserCredentials.Remove(chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    await botClient.SendTextMessageAsync(chatId, "✅ Вы успешно авторизовались!", cancellationToken: default);
                    await SendInfoMenu(chatId);
                    break;
                case "cancel_credentials":
                    tempUserCredentials.Remove(chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    await botClient.SendTextMessageAsync(chatId, "Авторизация <b>отменена.</b>",
                        cancellationToken: default, parseMode: ParseMode.Html);
                    break;
                default:
                    if (callbackQuery.Message.ReplyMarkup is InlineKeyboardMarkup inlineKeyboard)
                    {
                        await ClearInlineKeyboard(chatId, callbackQuery.Message.MessageId);
                    }
                    break;
            }

            await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: default);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - handling callback\n{ex.Message}");
        }
    }
    private static async Task HandleButtonAsync(Message message)
    {
        try
        {
            string responseText = message.Text switch
            {
                "На сегодня" => "Вы нажали на Кнопку 1",
                "На неделю" => "Вы нажали на Кнопку 2",
                _ => "Неизвестная кнопка"
            };

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: responseText,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - button handling\n{ex.Message}");
        }
    }
    private static async Task HandleError(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            var errorMessage = exception switch
            {
                ApiRequestException apiRequestException
                    => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
                _ => exception.ToString()
            };

            Console.WriteLine(errorMessage);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - polling error handling\n{ex.Message}");
        }
    }

    //
    // Commands
    //

    private static async Task StartCommand(long chatId, string firstName, string userName)
    {
        try
        {
            Console.WriteLine($"# Used the command '/START':\n" +
                 $" - id   [{chatId}]\n" +
                 $" - name [{userName}]\n" +
                 $" - date [{DateTime.Now}]");

            if (await IsUserRegistered(chatId))
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "⚠️ Вы уже зарегистрированы.\nВы можете изменить данные учетной записи, используя /relogin.",
                    cancellationToken: default
                );
            }
            else
            {
                await WelcomeMessage(chatId, firstName);
                await PromptUserToStartRegistration(chatId);
            }
        }
        catch (Exception ex) { Console.WriteLine($"~ ERROR - command '/START'\n{ex}"); }
    }
    private static async Task ReloginCommand(long chatId, string userName)
    {
        try
        {
            Console.WriteLine($"# Used the command '/RELOGIN':\n" +
                  $" - id   [{chatId}]\n" +
                  $" - name [{userName}]\n" +
                  $" - date [{DateTime.Now}]");

            if (await IsUserRegistered(chatId))
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();

                string updateQuery = "UPDATE [dbo].[Users] SET [ReloginProcess] = @ReloginProcess WHERE [ChatId] = @ChatId";

                using (SqlCommand command = new SqlCommand(updateQuery, connection))
                {
                    command.Parameters.AddWithValue("@ReloginProcess", true); // or false, depending on your needs
                    command.Parameters.AddWithValue("@ChatId", chatId);

                    int rowsAffected = await command.ExecuteNonQueryAsync();

                    if (rowsAffected > 0)
                    {
                        Console.WriteLine($"{rowsAffected} row(s) updated.");
                    }
                    else
                    {
                        Console.WriteLine("No rows were updated.");
                    }
                }
                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_relogin"),
                        InlineKeyboardButton.WithCallbackData("Отменить", "cancel_relogin")
                    }
                });

                await botClient.SendTextMessageAsync(
                    chatId,
                    "⚠️ Вы действительно хотите изменить учетную запись?",
                    replyMarkup: inlineKeyboard,
                    cancellationToken: default
                );
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "⚠️ Вы еще не авторизованы.\nДля начала авторизации используйте команду /start.",
                    cancellationToken: default
                );
            }
        }
        catch (Exception ex) { Console.WriteLine($"~ ERROR - command '/RELOGIN'\n{ex}"); }
    }

    //
    // Helpers
    //

    private static async Task SendInfoMenu(long chatId)
    {
        try
        {
            var keyboard = new ReplyKeyboardMarkup(new[]
            {
                new KeyboardButton[] { "На сегодня", "На неделю" }})
            {
                ResizeKeyboard = true
            };

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: "<b>Важная информация</b>\n\n" +
                      "Ежедневно в 7:30 утра вы будете получать расписание.\n\n" +
                      "⏰ Также вы можете проверить расписание на сегодня или на неделю с помощью кнопок на клавиатуре.\n\n" +
                      "Чтобы отредактировать профиль, воспользуйтесь командой /relogin.",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - отправки главного меню\n{ex.Message}");
        }
    }
    private static async Task ScheduleDailyMessages(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var delay = GetDelayUntilNextRun();
                Console.WriteLine($"# Schedule info:\n" +
                    $" - next shipment in - [{((float)delay.TotalSeconds)}] second");

                try
                {
                    await Task.Delay(delay, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                await SendDailyMessagesToAllUsers(token);
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - scheduling daily messages\n{ex.Message}");
        }
    }
    private static TimeSpan GetDelayUntilNextRun()
    {
        try
        {
            var now = DateTime.UtcNow;
            var moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
            var moscowNow = TimeZoneInfo.ConvertTimeFromUtc(now, moscowTimeZone);

            var nextRunTime = moscowNow.Date.AddHours(11).AddMinutes(58); // 18:00 по МСК
            if (nextRunTime < moscowNow)
            {
                nextRunTime = nextRunTime.AddDays(1);
            }

            return nextRunTime - moscowNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - calculating the delay until the next start\n{ex.Message}");
            return TimeSpan.FromHours(24); // Вернуть задержку на 24 часа в случае ошибки
        }
    }
    private static async Task SendDailyMessagesToAllUsers(CancellationToken token)
    {
        try
        {
            var users = await GetAllUsers();
            foreach (var user in users)
            {
                await botClient.SendTextMessageAsync(
                    chatId: user.ChatId,
                    text: "Текущее расписание на сегодня.",
                    cancellationToken: token
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - send schedule all users\n{ex.Message}");
        }
    }
    private static async Task WelcomeMessage(long chatId, string firstName)
    {
        try
        {
            string imagePath = "Resources/Images/Logo.png";

            if (System.IO.File.Exists(imagePath))
            {
                try
                {
                    using (var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        await botClient.SendPhotoAsync(
                            chatId: chatId,
                            photo: new InputFileStream(stream, "Logo.png"),
                            caption: $"👋 <b>{firstName}</b>, добро пожаловать в Top Schedule!",
                            parseMode: ParseMode.Html,
                            cancellationToken: default
                        );
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"~ ERROR - no find image {imagePath}\n{ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"~ ERROR - no find image {imagePath}");

                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"👋 <b>{firstName}</b>, добро пожаловать в Top Schedule!",
                    parseMode: ParseMode.Html,
                    cancellationToken: default
                );
            }

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: $"🤖 Я ваш личный бот, который будет отправлять вам ежедневные, актуальное расписание.\n\n" +
                      $"🗓 Также я могу помочь вам проверить текущее расписание в любое время.\n\n" +
                      $"🔔 Не забудьте настроить уведомления!\n\n" +
                      $"🔧 Я в стадии разработки!\r\n\r\n" +
                      $"Если возникнут какие-либо проблемы с моей работой, пожалуйста, сообщите об этом напрямую моим разработчикам:\r\n\r\n" +
                      $"📩 @Worton1720\r\n📩 @Suchmypin\r\n\r\n" +
                      $"🙏 Заранее приношу извинения за возможные неудобства.\r\n\r\n" +
                      $"💙 Спасибо за понимание! 💙 ",
                parseMode: ParseMode.Html,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - welcome message\n{ex.Message}");
        }
    }
    private static async Task ClearInlineKeyboard(long chatId, int messageId)
    {
        try
        {
            await botClient.EditMessageReplyMarkupAsync(
                chatId: chatId,
                messageId: messageId,
                replyMarkup: null,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - clear inlinekeyboard\n{ex.Message}");
        }
    }
    private static async Task PromptUserToStartRegistration(long chatId)
    {
        try
        {
            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("Авторизоваться", "start_registration") }
            });

            await botClient.SendTextMessageAsync(
                chatId,
                text: "❗️<b>Пожалуйста, авторизуйтесь</b>❗\n\n" +
                "Для входа используйте данные вашего личного кабинета: <a href=\"https://journal.top-academy.ru/\">Journal</a>.\n\n" +
                "⚠️ Это необходимо для доступа к функциям бота 🤖",

                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - sending an authorization request\n{ex.Message}");
        }
    }
    private static async Task PromptUserToEnterLogin(long chatId)
    {
        try
        {
            await botClient.SendTextMessageAsync(chatId, "Введите <b>login</b>:", cancellationToken: default, parseMode: ParseMode.Html);
            tempUserCredentials[chatId] = new UserInfo { ChatId = chatId };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - sending a login prompt\n{ex.Message}");
        }
    }
    private static async Task RegisterOrUpdateUser(UserInfo userInfo)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var existingUserQuery = "SELECT COUNT(*) FROM Users WHERE ChatId = @ChatId";
            using (var command = new SqlCommand(existingUserQuery, connection))
            {
                command.Parameters.AddWithValue("@ChatId", userInfo.ChatId);
                var userExists = (int)await command.ExecuteScalarAsync() > 0;

                if (userExists)
                {
                    var updateUserQuery = "UPDATE Users SET Login = @Login, Password = @Password, ReloginProcess = @ReloginProcess WHERE ChatId = @ChatId";
                    using (var updateCommand = new SqlCommand(updateUserQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("@Login", userInfo.Login);
                        updateCommand.Parameters.AddWithValue("@Password", userInfo.Password);
                        updateCommand.Parameters.AddWithValue("@ChatId", userInfo.ChatId);
                        updateCommand.Parameters.AddWithValue("@ReloginProcess", false);
                        await updateCommand.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    var insertUserQuery = "INSERT INTO Users (ChatId, Login, Password, ReloginProcess) VALUES (@ChatId, @Login, @Password, @ReloginProcess)";
                    using (var insertCommand = new SqlCommand(insertUserQuery, connection))
                    {
                        insertCommand.Parameters.AddWithValue("@ChatId", userInfo.ChatId);
                        insertCommand.Parameters.AddWithValue("@Login", userInfo.Login);
                        insertCommand.Parameters.AddWithValue("@Password", userInfo.Password);
                        insertCommand.Parameters.AddWithValue("@ReloginProcess", false);
                        await insertCommand.ExecuteNonQueryAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - user authentication/reauthentication\n {ex.Message}");
        }
    }
    private static async Task<bool> IsUserRegistered(long chatId)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = "SELECT COUNT(*) FROM Users WHERE ChatId = @ChatId";
            using (var command = new SqlCommand(query, connection))
            {
                command.Parameters.AddWithValue("@ChatId", chatId);
                var userExists = (int)await command.ExecuteScalarAsync() > 0;
                return userExists;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - checking user authentication status\n{ex.Message}");
            return false;
        }
    }
    private static async Task<List<UserInfo>> GetAllUsers()
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = "SELECT ChatId, Login, Password FROM Users";
            using (var command = new SqlCommand(query, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                var users = new List<UserInfo>();
                while (await reader.ReadAsync())
                {
                    users.Add(new UserInfo
                    {
                        ChatId = reader.GetInt64(0),
                        Login = reader.GetString(1),
                        Password = reader.GetString(2)
                    });
                }
                return users;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ~ ERROR - loading users from the database\n{ex.Message}");
            return new List<UserInfo>();
        }
    }
}