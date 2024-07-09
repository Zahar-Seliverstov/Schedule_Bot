// Program.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types.ReplyMarkups;
using static System.Net.Mime.MediaTypeNames;

class Program
{
    // Bot client
    private static ITelegramBotClient? botClient;

    // API Token
    private static string APIToken = "7360231050:AAEZF4oPEvfhMb5_cm4TOp2Kv8L-0DJh3x4";

    // List of users
    private static List<UserInfo> users = new List<UserInfo>();

    // Temporary storage for user credentials
    private static Dictionary<long, UserInfo> tempUserCredentials = new Dictionary<long, UserInfo>();

    static async Task Main(string[] args)
    {
        botClient = new TelegramBotClient(APIToken);
        using var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        botClient.StartReceiving(
            HandleUpdateAsync,
            HandlePollingErrorAsync,
            receiverOptions,
            cts.Token
        );

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"Start listening for @{me.Username}");

        // Schedule daily message sending
        Task.Run(() => ScheduleDailyMessages(cts.Token));

        Console.ReadLine();

        cts.Cancel();
    }


    private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
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

    private static async Task HandleMessageAsync(Message message)
    {
        var chatId = message.Chat.Id;
        if (message.Text.StartsWith("/start"))
        {
            Console.WriteLine($"Received a '/start' command from chat {chatId}.");

            var inlineKeyboard = new InlineKeyboardMarkup(new[]
            {
            new []
            {
                InlineKeyboardButton.WithCallbackData("Начать регистрацию", "start_registration"),
            }
        });
            await botClient.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"👋 {message.Chat.FirstName}, я рад приветствовать вас в Top Schedule!\n" +
                      "📅 Каждый день с 7:30 по МСК я буду отправлять вам актуальное расписание.\n" +
                      "🔔 Не забудьте настроить уведомления",
                cancellationToken: default);

            await botClient.SendTextMessageAsync(
                chatId,
                text: "Для получения расписания необходимо зарегистрироваться.",
                replyMarkup: inlineKeyboard,
                cancellationToken: default);
        }
        else if (tempUserCredentials.ContainsKey(chatId) && string.IsNullOrEmpty(tempUserCredentials[chatId].Login))
        {
            tempUserCredentials[chatId].Login = message.Text;
            await botClient.SendTextMessageAsync(chatId, "Введите ваш пароль:", cancellationToken: default);
        }
        else if (tempUserCredentials.ContainsKey(chatId) && string.IsNullOrEmpty(tempUserCredentials[chatId].Password))
        {
            tempUserCredentials[chatId].Password = message.Text;
            users.Add(tempUserCredentials[chatId]);
            tempUserCredentials.Remove(chatId);
            await botClient.SendTextMessageAsync(chatId, "Вы успешно зарегистрированы!", cancellationToken: default);
            await SendMainMenu(chatId);
        }
        else if (message.Text == "Button 1" || message.Text == "Button 2")
        {
            await HandleButtonAsync(message);
        }
    }

    private static async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery)
    {
        var chatId = callbackQuery.Message.Chat.Id;

        if (callbackQuery.Data == "start_registration")
        {
            await botClient.SendTextMessageAsync(chatId, "Введите ваш логин:", cancellationToken: default);
            tempUserCredentials[chatId] = new UserInfo { ChatId = chatId };
        }

        await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: default);
    }


    private static async Task HandleButtonAsync(Message message)
    {
        string responseText = message.Text switch
        {
            "Button 1" => "Вы нажали кнопку 1",
            "Button 2" => "Вы нажали кнопку 2",
            _ => "Неизвестная кнопка"
        };

        await botClient.SendTextMessageAsync(
            chatId: message.Chat.Id,
            text: responseText,
            cancellationToken: default
        );
    }

    private static Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }

    private static async Task SendMainMenu(long chatId)
    {
        var keyboard = new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "Button 1", "Button 2" }
        })
        {
            ResizeKeyboard = true
        };

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Я буду присылать вам рассписания каждый день в 7:30 утра.\n"+
            "Так же вы можете самостоятельно посмотреть рассписане на сегодня или на всю неделю используя кнопки появившиеся на клавиатуре",
            replyMarkup: keyboard,
            cancellationToken: default
        );
    }

    private static async Task ScheduleDailyMessages(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var moscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Russian Standard Time");
            var moscowNow = TimeZoneInfo.ConvertTimeFromUtc(now, moscowTimeZone);

            var nextRunTime = moscowNow.Date.AddHours(19).AddMinutes(11); // 7:30 MSK

            if (nextRunTime < moscowNow)
            {
                nextRunTime = nextRunTime.AddDays(1);
            }

            var delay = nextRunTime - moscowNow;
            Console.WriteLine($"Следующее сообщение прийдет: {nextRunTime} MSK (через {delay.TotalSeconds} секунд)");

            try
            {
                await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            foreach (var user in users)
            {
                await botClient.SendTextMessageAsync(
                    chatId: user.ChatId,
                    text: "Актуальное расписание на сегодня.",
                    cancellationToken: token
                );
            }

            // Delay for a minute to avoid rapid loop in case of errors
            await Task.Delay(TimeSpan.FromMinutes(1), token);
        }
    }
}

class UserInfo
{
    public long ChatId { get; set; }
    public string Login { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
