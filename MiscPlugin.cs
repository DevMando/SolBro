using ByteDev.Giphy;
using ByteDev.Giphy.Contract.Request;
using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace SolBro
{
    internal class MiscPlugin
    {
        private static readonly HttpClient _httpClient = new();
        private static readonly Random _random = new();

        private readonly string _weatherApiKey;
        private readonly string _giphyApiKey;

        public MiscPlugin(string weatherApiKey, string giphyApiKey)
        {
            _weatherApiKey = weatherApiKey ?? "";
            _giphyApiKey = giphyApiKey ?? "";
        }

        [KernelFunction("get_weather_report_by_location")]
        [Description("When a user asks about the weather in a specific location, this function returns. They must provide a location or coordinates. Location example: London,UK")]
        public async Task<string> GetWeatherReportByLocation(string location)
        {
            if (string.IsNullOrWhiteSpace(_weatherApiKey))
                return "Weather lookup is not configured. Set Weather__ApiKey in .env to enable it.";

            string url = $"https://weather.visualcrossing.com/VisualCrossingWebServices/rest/services/timeline/{location}?key={_weatherApiKey}";

            try
            {
                var response = await _httpClient.GetAsync(url);
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                return $"Weather lookup failed: {ex.Message}";
            }
        }

        [KernelFunction("get_current_Time")]
        [Description("When a user asks what time it is or anything related to time, provide or use the current moment in time.")]
        public Task<string> GetCurrentTime()
        {
            return Task.FromResult(DateTime.Now.ToString());
        }

        [KernelFunction("get_giphy")]
        [Description("When you feel like its a good time to show a gif or a user asks for a meme/gif provide a gif with any search term related to the conversation or of your liking. randomly use gifs thorughout moments of high interest in a converstaion")]
        public async Task<string> GetGiphy(string? searchTerm = null)
        {
            if (string.IsNullOrWhiteSpace(_giphyApiKey))
                return "GIF search is not configured. Set Giphy__ApiKey in .env to enable it.";

            try
            {
                var client = new GiphyApiClient(new HttpClient());

                if (searchTerm is null)
                {
                    var randomRequest = new RandomRequest(_giphyApiKey);
                    var response = await client.GetRandomAsync(randomRequest);
                    return response.Gif.BitlyUrl.ToString();
                }
                else
                {
                    var request = new SearchRequest(_giphyApiKey) { Query = searchTerm, Limit = 15 };
                    var response = await client.SearchAsync(request);
                    var gifs = response.Gifs.ToList();
                    if (gifs.Count == 0)
                        return $"No GIFs found for: {searchTerm}";
                    var randomIndex = _random.Next(0, gifs.Count);
                    return gifs[randomIndex].BitlyUrl.ToString();
                }
            }
            catch (Exception ex)
            {
                return $"GIF lookup failed: {ex.Message}";
            }
        }
    }
}
