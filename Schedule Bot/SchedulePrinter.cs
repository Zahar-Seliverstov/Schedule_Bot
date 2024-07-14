using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

public static class SchedulePrinter
{
    public async static Task PrintSchedule(List<Dictionary<string, string>> scheduleData, long chatId, ITelegramBotClient botClient, string flag)
    {
        try
        {
            if (scheduleData.Count != 0)
            {
                string strSchedule = "";
                int value = 0;
                foreach (var dateEntry in scheduleData)
                {
                    value++;
                    if (value == 1)
                        strSchedule += $"<b>• Дата:</b> {dateEntry["date"]} •\n\n";
                    strSchedule += $"<b>❖  Пара - </b> {dateEntry["lesson"]}\n";
                    strSchedule += $"🕘 <b>Время: </b>{dateEntry["started_at"]} - {dateEntry["finished_at"]}\n";
                    strSchedule += $"👤 <b>Преподаватель: </b>{dateEntry["teacher_name"]}\n";
                    strSchedule += $"📖 <b>Предмет: </b>{dateEntry["subject_name"]}\n";
                    strSchedule += $"🏠 <b>Аудитория: </b>{dateEntry["room_name"]}\n\n";
                    if (scheduleData.Count != value)
                        strSchedule += "✦~~~~~~~~~~~~~~~~~~~~~~✧\n\n";
                }

                await botClient.SendTextMessageAsync(
                    chatId,
                    text: strSchedule,
                    parseMode: ParseMode.Html,
                    cancellationToken: default
                );
            }
            else
            {
                if (flag == "today" || flag == "week")
                {
                    await botClient.SendTextMessageAsync(
                        chatId,
                        text: "🚫 Расписание на сегодня отсутствует 🚫",
                        parseMode: ParseMode.Html,
                        cancellationToken: default
                    );
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"~ ERROR - print schedule\n{ex.Message}");
        }
    }
    public static async Task SaveToJson(List<Dictionary<string, string>> scheduleData, string filename)
    {
        var json = JsonConvert.SerializeObject(scheduleData, Formatting.Indented);
        System.IO.File.WriteAllText(filename, json);
    }
}