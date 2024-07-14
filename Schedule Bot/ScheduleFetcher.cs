using Newtonsoft.Json;
using System.Net;
using System.Text;

public class ScheduleFetcher
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly Dictionary<string, string> _headers;

    public ScheduleFetcher(HttpClient httpClient, string url, Dictionary<string, string> headers)
    {
        _httpClient = httpClient;
        _url = url;
        _headers = headers;
    }

    public async Task<List<Dictionary<string, string>>> FetchSchedule(string endpoint, Dictionary<string, string> parameters)
    {
        try
        {
            var fullUrl = _url + endpoint;
            if (parameters != null)
            {
                var queryString = new StringBuilder();
                foreach (var param in parameters)
                {
                    queryString.Append($"{param.Key}={WebUtility.UrlEncode(param.Value)}&");
                }
                fullUrl += "?" + queryString.ToString().TrimEnd('&');
            }

            var request = (HttpWebRequest)WebRequest.Create(fullUrl);
            foreach (var header in _headers)
            {
                request.Headers.Add(header.Key, header.Value);
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var streamReader = new StreamReader(response.GetResponseStream()))
            {
                var responseText = streamReader.ReadToEnd();
                return JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(responseText);
            }
        }
        catch (WebException ex)
        {
            Console.WriteLine($"Ошибка при выполнении запроса: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Произошла непредвиденная ошибка: {ex.Message}");
            return null;
        }
    }
}