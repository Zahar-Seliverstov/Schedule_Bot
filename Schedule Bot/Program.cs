using Newtonsoft.Json.Linq;
using Schedule_Bot;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http.Headers;

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

    //
    // Bot run
    //

    private static async Task StartBot()
    {
        try
        {
            InitializeBot();

            using var cts = new CancellationTokenSource();
            var me = await botClient.GetMeAsync();

            Console.Write($"\n# Бот информация:\n" +
                $" - id       [{me.Id}]\n" +
                $" - username [@{me.Username}]\n" +
                $" - APIToken [{APIToken}]\n" +
                $" - status:  ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("OK");
            Console.ResetColor();

            await Task.Run(() => ScheduleDailyMessages(cts.Token));

            Console.ReadLine();
            cts.Cancel();
        }
        catch (Exception ex) { Console.WriteLine($"\n~ ERROR - метод StartBot [Не удалось запустить бота]\n{ex.Message}"); }
    }
    private static void InitializeBot()
    {
        try
        {
            botClient = new TelegramBotClient(APIToken);
            botClient.StartReceiving(
                HandleUpdate,
                HandleError,
                new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() }
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод InitializeBot [Не удалось инициализировать бота]\n{ex.Message}");
        }
    }

    //
    //  Parse schedule
    //

    private static async Task StartParseSchedule(long chatId, string flag)
    {
        try
        {
            string url = "https://msapi.top-academy.ru/api/v2";
            string refresh_token = "";

            Dictionary<string, string> headers = new Dictionary<string, string>
            {
                { "authorization", "" },
                { "referer", "https://journal.top-academy.ru/" }
            };

            if (string.IsNullOrEmpty(refresh_token) || !await CheckTokenValidity(url, headers))
            {
                if (await IsUserRegistered(chatId))
                {
                    string username = "";
                    string password = "";

                    using var connection = new SqlConnection(connectionString);
                    await connection.OpenAsync();
                    string selectQuery = "SELECT [Login], [Password] FROM [dbo].[Users] WHERE [ChatId] = @ChatId";

                    using (SqlCommand command = new SqlCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("@ChatId", chatId);

                        using (SqlDataReader reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                username = reader.GetString(0);
                                password = reader.GetString(1);
                            }
                            else { Console.WriteLine("\n~ WARNING - метод StartParseSchedule [Не удалось прочитать данные из базы данных и запарсить расписание]"); }
                        }
                    }

                    refresh_token = await GetRefreshToken(username, password);

                    if (string.IsNullOrEmpty(refresh_token))
                    {
                        await botClient.SendTextMessageAsync(
                             chatId,
                             text: $"{((flag == "today" || flag == "week") ? "⚠️ Не удалось получить расписание с использованием указанных данных:\n\n" : "⚠️ Не удалось отправить ежедневное расписание с использованием указанных данных:\n\n")}" +
                             $"👤 login: <b>{username}</b>\n" +
                             $"🔒 password: <b>{password}</b>\n" +
                             $"\n" +
                             $"Возможно, введенные вами данные неверны или не актуальны. 🤔\n" +
                             $"Если вы уверены в их корректности, пожалуйста, свяжитесь с разработчиками:\n\n" +
                             $"📩 @Worton1720\r\n📩 @Suchmypin\n\nИзвините за доставленные неудобства 😔",
                             cancellationToken: default,
                             parseMode: ParseMode.Html
                             );
                        return;
                    }
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

            headers["authorization"] = $"Bearer {refresh_token}";

            var httpClient = new HttpClient();
            ScheduleFetcher scheduleFetcher = new ScheduleFetcher(httpClient, url, headers);
            ScheduleManager scheduleManager = new ScheduleManager(scheduleFetcher, headers);

            string currentDate = DateTime.Today.ToString("yyyy-MM-dd");
            string inputDate = currentDate; // 2024-05-03

            List<Dictionary<string, string>> scheduleForDay = await scheduleManager.GetScheduleForDay(inputDate);

            if (scheduleForDay != null)
            {
                await SchedulePrinter.PrintSchedule(scheduleForDay, chatId, botClient, flag);
                await SchedulePrinter.SaveToJson(scheduleForDay, $"schedule_{inputDate}.json");
                if (scheduleForDay.Count != 0) { Console.WriteLine($"\n* Расписание на [{inputDate}] для чата [{chatId}] успешно получено"); }
                else { Console.WriteLine($"\n* Расписание на [{inputDate}] для чата [{chatId}] успешно получено НО оно пустое"); }
            }
            else { Console.WriteLine($"\n~ WARNING - метод StartParseSchedule [Не удалось получить расписание на [{inputDate}]]"); }
        }
        catch (Exception ex) { Console.WriteLine($"\n~ ERROR - метод StartParseSchedule\n{ex.Message}"); }
    }
    private static async Task<bool> CheckTokenValidity(string url, Dictionary<string, string> headers)
    {
        try
        {
            using (var client = new HttpClient())
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var header in headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                var response = client.SendAsync(request).Result;
                return response.IsSuccessStatusCode;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - метод CheckTokenValidity [Не удалось проверить токен]\n{ex.Message}");
            return false;
        }
    }
    private static async Task<string> GetRefreshToken(string username, string password)
    {
        var urlLogin = "https://msapi.top-academy.ru/api/v2/auth/login";
        var payload = new
        {
            application_key = ConfigurationManager.AppSettings["aplKey"],
            id_city = (object)null,
            username = username,
            password = password
        };

        var headersLogin = new Dictionary<string, string> { { "Referer", "https://journal.top-academy.ru/" } };

        using (var client = new HttpClient())
        {
            try
            {
                var jsonPayload = JsonSerializer.Serialize(payload);
                var request = new HttpRequestMessage(HttpMethod.Post, urlLogin);

                foreach (var header in headersLogin)
                {
                    request.Headers.Add(header.Key, header.Value);
                }

                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = client.SendAsync(request).Result;
                var responseData = response.Content.ReadAsStringAsync().Result;

                if (response.IsSuccessStatusCode)
                {
                    var responseObject = JObject.Parse(responseData);

                    if (responseObject["refresh_token"] != null)
                    {
                        return responseObject["refresh_token"].ToString();
                    }
                    else
                    {
                        Console.WriteLine("Refresh token не найден в ответе.");
                    }
                }
                else
                {
                    Console.WriteLine($"\n~ ERROR - метод GetRefreshToken [Ошибка при отправке запроса {response.StatusCode}]");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"\n~ ERROR - метод GetRefreshToken [Произошла ошибка при отправке запроса]\n{ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n~ ERROR - метод GetRefreshToken [Произошла непредвиденная ошибка]\n{ex.Message}");
            }
        }
        return null;
    }
    public class LoginResponse
    {
        public string access_token { get; set; }
        public string refresh_token { get; set; }
        public int expires_in_refresh { get; set; }
        public int expires_in_access { get; set; }
        public int user_type { get; set; }
        public CityData city_data { get; set; }
        public string user_role { get; set; }
    }
    public class CityData
    {
        public int id_city { get; set; }
        public string prefix { get; set; }
        public string translate_key { get; set; }
        public string timezone_name { get; set; }
        public string country_code { get; set; }
        public int market_status { get; set; }
        public string name { get; set; }
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
            Console.WriteLine($"\n~ ERROR - метод HandleUpdate [Ошибка обработки обновления]\n{ex.Message}");
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
            string selectQuery = "SELECT [IsReloginInProgress] FROM [dbo].[Users] WHERE [ChatId] = @ChatId";

            using (SqlCommand command = new SqlCommand(selectQuery, connection))
            {
                command.Parameters.AddWithValue("@ChatId", chatId);

                using (SqlDataReader reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        processReloginActivate = reader.GetBoolean(0);
                    }
                    else
                    {
                        Console.WriteLine($"\n~ WARNING - метод HandleMessageAsync [Данные с заданным ChatId {chatId} не найдены в базе данных. Скорее всего пользователь не авторизован]\n");
                    }
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
                    case "/exit":
                        await ExitCommand(chatId, message.Chat.FirstName);
                        break;
                    case "/relogin":
                        await ReloginCommand(chatId, message.Chat.Username);
                        break;
                    case "/run":
                        await EnabeleOrDisableSendScheduleCommand(chatId, message.Chat.Username, true);
                        break;
                    case "/stop":
                        await EnabeleOrDisableSendScheduleCommand(chatId, message.Chat.Username, false);
                        break;
                    case "На сегодня":
                        await HandleButtonAsync(message);
                        await StartParseSchedule(message.Chat.Id, "today");
                        break;
                    case "На неделю":
                        await HandleButtonAsync(message);
                        break;
                    default:
                        await ClearInlineKeyboard(chatId, message.MessageId);
                        if (message.Text.StartsWith("/") && message.Text.Length > 1)
                        {
                            await botClient.SendTextMessageAsync(
                                message.Chat.Id,
                                $"⚠️ Команда - {message.Text} не распознана.\n" +
                                "Возможно команда введена не коректно.",
                                cancellationToken: default);
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(
                               chatId,
                               text: await GetRandomEmojiAsync(),
                               cancellationToken: default);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод HandleMessageAsync\n{ex.Message}");
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
                var inlineKeyboard = new InlineKeyboardMarkup(new[]{
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_credentials"),
                    InlineKeyboardButton.WithCallbackData("Отменить", "cancel_credentials")
                }});
                if (await ValidateCredentials(userInfo.Login, userInfo.Password))
                {
                    await botClient.SendTextMessageAsync(
                    chatId,
                    $"Авторизоваться используя:\n\n" +
                    $"👤 login: <b>{userInfo.Login}</b>\n" +
                    $"🔒 password: <b>{userInfo.Password}</b>",
                    parseMode: ParseMode.Html,
                    replyMarkup: inlineKeyboard,
                    cancellationToken: default);
                }
                else
                {
                    InlineKeyboardMarkup inlineKeyboardMarkup = new InlineKeyboardMarkup(new[]{
                        new [] {
                            InlineKeyboardButton.WithCallbackData("Авторизоваться еще раз", "start_registration")
                    }});
                    await botClient.SendTextMessageAsync(
                        chatId,
                        text: $"{"⚠️ Не удалось авторизоваться с использованием указанных данных:\n\n"}" +
                        $"👤 login: <b>{userInfo.Login}</b>\n" +
                        $"🔒 password: <b>{userInfo.Password}</b>\n" +
                        $"\n" +
                        $"Возможно, введенные вами данные неверны или не актуальны. 🤔\n" +
                        $"Если вы уверены в их корректности, пожалуйста, свяжитесь с разработчиками:\n\n" +
                        $"📩 @Worton1720\r\n📩 @Suchmypin\n\nИзвините за доставленные неудобства 😔",
                        replyMarkup: inlineKeyboardMarkup,
                        cancellationToken: default,
                        parseMode: ParseMode.Html);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод HandleUserCredentialsInput\n{ex.Message}");
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
                case "confirm_exit":
                    await UpdateUserReloginStatus(false, chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    await RemoveUser(chatId);
                    break;
                case "cancel_exit":
                    await UpdateUserReloginStatus(false, chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    await botClient.SendTextMessageAsync(chatId, "Выход из профиля отменен.",
                        cancellationToken: default, parseMode: ParseMode.Html);
                    break;
                case "confirm_relogin":
                    await UpdateUserReloginStatus(false, chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    tempUserCredentials[chatId] = new UserInfo { ChatId = chatId };
                    await botClient.SendTextMessageAsync(chatId, "Введите новый <b>login</b>:",
                        cancellationToken: default, parseMode: ParseMode.Html);
                    break;
                case "cancel_relogin":
                    await UpdateUserReloginStatus(false, chatId);
                    await ClearInlineKeyboard(chatId, messageId);
                    await botClient.SendTextMessageAsync(chatId, "Переавторизация <b>отменена</b>.",
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
            Console.WriteLine($"\n~ ERROR - метод HandleCallbackQueryAsync\n{ex.Message}");
        }
    }
    private static async Task HandleButtonAsync(Message message)
    {
        try
        {
            string responseText = message.Text switch
            {
                "На сегодня" => "🗓 Расписание на <b>сегодня</b>",
                "На неделю" => "🗓 Расписание на <b>неделю</b>",
                _ => "Неизвестная кнопка"
            };

            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: responseText,
                parseMode: ParseMode.Html,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод HandleButtonAsync\n{ex.Message}");
        }
    }
    private static async Task HandleError(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"\n~ ERROR - метод HandleError [Telegram API {apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
    }

    //
    // Commands
    //

    private static async Task StartCommand(long chatId, string firstName, string userName)
    {
        try
        {
            Console.WriteLine($"\n# Использована команда 'START':\n" +
                 $" - id       [{chatId}]\n" +
                 $" - username [{userName}]\n" +
                 $" - date     [{DateTime.Now}]");

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
        catch (Exception ex) { Console.WriteLine($"\n~ ERROR - метод StartCommand\n{ex}"); }
    }
    private static async Task ReloginCommand(long chatId, string userName)
    {
        try
        {
            Console.WriteLine($"\n# Использована команда 'RELOGIN':\n" +
                 $" - id       [{chatId}]\n" +
                 $" - username [{userName}]\n" +
                 $" - date     [{DateTime.Now}]");

            if (await IsUserRegistered(chatId))
            {
                await UpdateUserReloginStatus(true, chatId);
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
        catch (Exception ex) { Console.WriteLine($"\n~ ERROR - метод ReloginCommand\n{ex}"); }
    }
    private static async Task EnabeleOrDisableSendScheduleCommand(long chatId, string userName, bool mode)
    {
        try
        {
            if (await IsUserRegistered(chatId))
            {
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                string selectQuery = "SELECT [SendSchedule] FROM [dbo].[Users] WHERE [ChatId] = @ChatId";
                string updateQuery = "UPDATE [dbo].[Users] SET [SendSchedule] = @SendSchedule WHERE [ChatId] = @ChatId";
                bool? sendSchedule = null;

                using (SqlCommand command = new SqlCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("@ChatId", chatId);


                    using (SqlDataReader reader = await command.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            sendSchedule = reader.GetBoolean(0);
                        }
                    }
                }
                if (mode == sendSchedule)
                {
                    await botClient.SendTextMessageAsync(
                           chatId,
                           text: (mode ? "✔️ Авто-расписание уже <b>включено</b>." : "✖️ Авто-расписание уже <b>отключено</b>."),
                           cancellationToken: default,
                           parseMode: ParseMode.Html
                       );
                    return;
                }

                using (SqlCommand command = new SqlCommand(updateQuery, connection))
                {
                    command.Parameters.AddWithValue("@ChatId", chatId);
                    command.Parameters.AddWithValue("@SendSchedule", mode);

                    if (await command.ExecuteNonQueryAsync() > 0)
                    {
                        Console.WriteLine($"\nПользователь {userName} {(mode ? "включил" : "отключил")} авто-расписание");
                        await botClient.SendTextMessageAsync(
                            chatId,
                            text: (mode ? "Авто-расписание <b>включено</b> 🟢" : "Авто-расписание <b>отключено</b> 🔴"),
                            cancellationToken: default,
                            parseMode: ParseMode.Html
                        );
                    }
                    else
                    {
                        Console.WriteLine("\n~ WARNING - метод EnabeleOrDisableSendScheduleCommand [Не удалось обновить SendSchedule]");
                    }
                }
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
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - метод EnabeleOrDisableSendScheduleCommand\n{ex.Message}");
        }
    }
    private static async Task ExitCommand(long chatId, string username)
    {
        try
        {
            Console.WriteLine($"\n# Использована команда 'EXIT':\n" +
                $" - id       [{chatId}]\n" +
                $" - username [{username}]\n" +
                $" - date     [{DateTime.Now}]");

            if (await IsUserRegistered(chatId))
            {
                await UpdateUserReloginStatus(true, chatId);
                var inlineKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("Подтвердить", "confirm_exit"),
                        InlineKeyboardButton.WithCallbackData("Отменить", "cancel_exit")
                    }
                });

                await botClient.SendTextMessageAsync(
                    chatId,
                    "⚠️ Вы действительно хотите <b>выйти</b>?",
                    replyMarkup: inlineKeyboard,
                    parseMode: ParseMode.Html,
                    cancellationToken: default
                );
            }
            else
            {
                await botClient.SendTextMessageAsync(
                    chatId,
                    "⚠️ Вы еще <b>не авторизованы</b>.\nДля начала авторизации используйте команду /start.",
                    parseMode: ParseMode.Html,
                    cancellationToken: default
                );
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод ExitCommand\n{ex.Message}");
        }
    }

    //
    // Helpers
    //

    private static async Task RemoveUser(long chatId)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            string deleteQuery = "DELETE FROM dbo.Users WHERE ChatId = @ChatId";
            using (var command = new SqlCommand(deleteQuery, connection))
            {
                command.Parameters.AddWithValue("@ChatId", chatId);
                if (await command.ExecuteNonQueryAsync() > 0)
                {
                    Console.WriteLine($"\n<< Пользователь [{chatId}] был успешно удален");
                    await botClient.SendTextMessageAsync(
                        chatId,
                        text: "✅ <b>Вы успешно вышли из системы</b>.\n\n" +
                        "🗑️ Ваши данные были <b>полностью удалены</b>.\n\n" +
                        "Если у вас есть какие-либо вопросы, пожалуйста, свяжитесь с разработчиками напрямую:\n\n" +
                        "📩 @Worton1720\n📩 @Suchmypin\n\n" +
                        "✨ Спасибо, что были с нами!",
                        parseMode: ParseMode.Html,
                        cancellationToken: default
                    );

                }
                else
                {
                    Console.WriteLine($"\n~ WARNING - метод ExitCommand [Не удалось удалить пользователя [{chatId}]");
                    await botClient.SendTextMessageAsync(
                        chatId,
                        text: "❌ <b>Произошла ошибка</b> ❌\n\n" +
                              "⚠️ К сожалению, не удалось выйти из системы и удалить ваши данные.\n\n" +
                              "Пожалуйста, свяжитесь с разработчиками для решения этой проблемы.\n\n" +
                              "📩 @Worton1720\r\n📩 @Suchmypin\n\n" +
                              "Извините за неудобства 😔",
                        parseMode: ParseMode.Html,
                        cancellationToken: default
                    );
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - метод RemoveUser\n{ex.Message}");
        }

    }
    private static async Task<bool> ValidateCredentials(string username, string password)
    {
        string refresh_token = await GetRefreshToken(username, password);

        if (string.IsNullOrEmpty(refresh_token))
        {
            return false;
        }
        else
        {
            return true;
        }
    }
    private static async Task UpdateUserReloginStatus(bool state, long chatId)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            string updateQuery = "UPDATE [dbo].[Users] SET [IsReloginInProgress] = @IsReloginInProgress WHERE [ChatId] = @ChatId";

            using (SqlCommand command = new SqlCommand(updateQuery, connection))
            {
                command.Parameters.AddWithValue("@IsReloginInProgress", state);
                command.Parameters.AddWithValue("@ChatId", chatId);

                if (await command.ExecuteNonQueryAsync() < 0)
                {
                    Console.WriteLine($"\n~ WARNING - метод HandleCallbackQueryAsync [IsReloginInProgress не обновлено]");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n! ERROR - метод UpdateUserReloginStatus [Не удалось обновить IsReloginInProgress в чате {chatId}]\n{ex.Message}");
        }
    }
    private static Task<string> GetRandomEmojiAsync()
    {
        return Task.Run(() =>
        {
            string[] emojis = new string[]
            {
            "😊", "😃", "😁", "😄", "😆", "😉", "😍", "🥳", "😎", "🤗",
            "😇", "🙂", "🙃", "😌", "😋", "😜", "😝", "🤪", "🤩", "🥰",
            "😏", "😶", "😐", "😑", "😒", "🙄", "🤔", "🤨", "😬", "🤥",
            "😌", "😔", "😪", "🤤", "😴", "😷", "🤒", "🤕", "🤢", "🤮",
            "🤧", "🥵", "🥶", "🥴", "😵", "🤯", "🤠", "🥳", "😎", "🤓",
            "🧐", "😕", "😟", "🙁", "😮", "😯", "😲", "😳", "🥺", "😦",
            "😧", "😨", "😩", "😫", "🥱", "😤", "😡", "😠", "🤬", "😈",
            "👿", "💀", "☠️", "💩", "🤡", "👹", "👺", "👻", "👽", "👾",
            "🤖", "😺", "😸", "😹", "😻", "😼", "😽", "🙀", "😿", "😾",
            "🙈", "🙉", "🙊", "💋", "💌", "💘", "💝", "💖", "💗", "💓",
            "💞", "💕", "💟", "❣️", "💔", "❤️", "🧡", "💛", "💚", "💙",
            "💜", "🤎", "🖤", "🤍", "💯", "💢", "💥", "💫", "💦", "💨",
            "🕳️", "💣", "💬", "👁️‍🗨️", "🗨️", "🗯️", "💭", "🌀", "🌁", "🌃",
            "🌄", "🌅", "🌆", "🌇", "🌉", "🎠", "🎡", "🎢", "💈", "🎪",
            "🚂", "🚃", "🚄", "🚅", "🚆", "🚇", "🚈", "🚉", "🚊", "🚝",
            "🚞", "🚋", "🚌", "🚍", "🚎", "🚐", "🚑", "🚒", "🚓", "🚔",
            "🚕", "🚖", "🚗", "🚘", "🚙", "🚚", "🚛", "🚜", "🚲", "🛴",
            "🛵", "🚏", "🛣️", "🛤️", "🛢️", "⛽", "🚨", "🚥", "🚦", "🛑"
            };
            Random random = new Random();
            int randomIndex = random.Next(emojis.Length);
            return emojis[randomIndex];
        });
    }
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
                text: "<b>🛑 Важная информация 🛑</b>\n\n" +
                      "Вот что я могу предложить:\n\n" +
                      "🕖 <b>Ежедневное расписание</b>\n" +
                      "Каждое утро в <b>7:30</b> я буду присылать вам актуальное расписание. Управляйте этой функцией с помощью команд /run и /stop.\n\n" +
                      "📅 <b>Проверка расписания</b>\n" +
                      "Вы можете узнать расписание на сегодня или на всю неделю, используя кнопки на клавиатуре.\n\n" +
                      "🛠️ <b>Редактирование профиля</b>\n" +
                      "Чтобы изменить информацию профиля, воспользуйтесь командой /relogin.\n\n" +
                      "🚪 <b>Выход из профиля</b>\n" +
                      "Чтобы выйти из профиля, используйте команду /exit.",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - send info menu\n{ex.Message}");
        }
    }
    private static async Task ScheduleDailyMessages(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var delay = GetDelayUntilNextRun();
                Console.WriteLine($"\n# Авто-расписание информация:\n" +
                    $" - Рассыл расписания через [{delay:hh\\:mm\\:ss}]");

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
            Console.WriteLine($"\n~ ERROR - метод ScheduleDailyMessages\n{ex.Message}");
        }
    }
    private static TimeSpan GetDelayUntilNextRun()
    {
        try
        {
            var now = DateTime.UtcNow;
            var moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
            var moscowNow = TimeZoneInfo.ConvertTimeFromUtc(now, moscowTimeZone);

            var nextRunTime = moscowNow.Date.AddHours(20).AddMinutes(55);
            if (nextRunTime < moscowNow)
            {
                nextRunTime = nextRunTime.AddDays(1);
            }

            return nextRunTime - moscowNow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод GetDelayUntilNextRun\n{ex.Message}");
            return TimeSpan.FromHours(24);
        }
    }
    private static async Task SendDailyMessagesToAllUsers(CancellationToken token)
    {
        try
        {
            var users = await GetAllUsers();
            foreach (var user in users)
            {
                if (user.SendSchedule)
                {
                    await StartParseSchedule(user.ChatId, "all");

                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод SendDailyMessagesToAllUsers\n{ex.Message}");
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
                    Console.WriteLine($"\n~ ERROR - метод WelcomeMessage не получилось обработать {imagePath}\n{ex.Message}");
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
            Console.WriteLine($"\n~ ERROR - метод ClearInlineKeyboard [Очистка клавиатуры]\n{ex.Message}");
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
                "⚠️ Это необходимо для доступа к функциям бота 🤖\n\n" +
                "Для входа используйте данные вашего личного кабинета: <a href=\"https://journal.top-academy.ru/\">Journal</a>.",

                parseMode: ParseMode.Html,
                replyMarkup: inlineKeyboard,
                cancellationToken: default
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод PromptUserToStartRegistration\n{ex.Message}");
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
            Console.WriteLine($"\n~ ERROR - метод PromptUserToEnterLogin\n{ex.Message}");
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
                    var updateUserQuery = "UPDATE Users SET Login = @Login, Password = @Password, IsReloginInProgress = @IsReloginInProgress , SendSchedule = @SendSchedule WHERE ChatId = @ChatId";
                    using (var updateCommand = new SqlCommand(updateUserQuery, connection))
                    {
                        updateCommand.Parameters.AddWithValue("@Login", userInfo.Login);
                        updateCommand.Parameters.AddWithValue("@Password", userInfo.Password);
                        updateCommand.Parameters.AddWithValue("@ChatId", userInfo.ChatId);
                        updateCommand.Parameters.AddWithValue("@IsReloginInProgress", false);
                        updateCommand.Parameters.AddWithValue("@SendSchedule", true);
                        await updateCommand.ExecuteNonQueryAsync();
                    }
                }
                else
                {
                    var insertUserQuery = "INSERT INTO Users (ChatId, Login, Password, IsReloginInProgress, SendSchedule) VALUES (@ChatId, @Login, @Password, @IsReloginInProgress, @SendSchedule)";
                    using (var insertCommand = new SqlCommand(insertUserQuery, connection))
                    {
                        insertCommand.Parameters.AddWithValue("@ChatId", userInfo.ChatId);
                        insertCommand.Parameters.AddWithValue("@Login", userInfo.Login);
                        insertCommand.Parameters.AddWithValue("@Password", userInfo.Password);
                        insertCommand.Parameters.AddWithValue("@IsReloginInProgress", false);
                        insertCommand.Parameters.AddWithValue("@SendSchedule", true);
                        await insertCommand.ExecuteNonQueryAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод RegisterOrUpdateUser [Внесение или изменение данных пользователя]\n {ex.Message}");
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
            Console.WriteLine($"\n~ ERROR - метод IsUserRegistered\n{ex.Message}");
            return false;
        }
    }
    private static async Task<List<UserInfo>> GetAllUsers()
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var query = "SELECT ChatId, Login, Password, IsReloginInProgress, SendSchedule FROM Users";
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
                        Password = reader.GetString(2),
                        IsReloginInProgress = reader.GetBoolean(3),
                        SendSchedule = reader.GetBoolean(4),
                    });
                }
                return users;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n~ ERROR - метод GetAllUsers \n{ex.Message}");
            return new List<UserInfo>();
        }
    }
}