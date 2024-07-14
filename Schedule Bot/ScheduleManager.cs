using System.Globalization;

public class ScheduleManager
{
    private readonly ScheduleFetcher _scheduleFetcher;
    private readonly Dictionary<string, string> _headers;

    public ScheduleManager(ScheduleFetcher scheduleFetcher, Dictionary<string, string> headers)
    {
        _scheduleFetcher = scheduleFetcher;
        _headers = headers;
    }

    public async Task<List<Dictionary<string, string>>> GetScheduleForDay(string date)
    {
         return await _scheduleFetcher.FetchSchedule("/schedule/operations/get-by-date",
            new Dictionary<string, string> { { "date_filter", date } });
    }

    public async Task<string> GetWeekdayByDate(string date)
    {
        var dateObj = DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return dateObj.ToString("dddd", new CultureInfo("ru-RU"));
    }
}