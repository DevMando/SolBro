using Microsoft.SemanticKernel;
using System.ComponentModel;

namespace SolBro
{
    public class RugCheckApiPlugin
    {
        private const string MostViewedTokensApiUrl = "https://api.rugcheck.xyz/v1/stats/recent";
        private static readonly HttpClient _httpClient = new();

        [KernelFunction("get_most_viewed_solana_meme_tokens")]
        [Description("When a user asks about the most viewed cryptocurrency solana meme tokens/coins at the moment, this function returns a list of the most viewed meme tokens.")]
        public async Task<string> GetMostViewedTokens()
        {
            try
            {
                var response = await _httpClient.GetAsync(MostViewedTokensApiUrl);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync();
                }

                Console.WriteLine($"Network Error: {response.StatusCode}");
                return "I'm unable to retrieve that info at the moment. Try again later.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
                return "I'm unable to retrieve that info at the moment. Try again later.";
            }
        }
    }
}
