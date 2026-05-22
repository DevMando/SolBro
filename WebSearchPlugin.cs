using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.SemanticKernel;

namespace SolBro;

/// <summary>
/// Provides web search and page fetching capabilities for the AI agent.
/// Primary search backend is the Tavily Search API (JSON, AI-friendly snippets).
/// Falls back to DuckDuckGo HTML scraping when Tavily is unavailable (no key, rate-limited, or transient error).
/// </summary>
public class WebSearchPlugin
{
    private static readonly HttpClient _httpClient;
    private readonly string _tavilyApiKey;

    private static bool _tavilyKeyDisabled;

    static WebSearchPlugin()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public WebSearchPlugin(string tavilyApiKey = "")
    {
        _tavilyApiKey = tavilyApiKey ?? "";
    }

    [KernelFunction("search_web")]
    [Description("Searches the web for information. Returns titles, URLs, and snippets. Use this to find current information, documentation, tutorials, or answers to questions.")]
    public async Task<string> SearchWeb(
        [Description("The search query")] string query,
        [Description("Maximum number of results to return (1-10, default 5)")] int maxResults = 5)
    {
        maxResults = Math.Clamp(maxResults, 1, 10);

        if (!string.IsNullOrWhiteSpace(_tavilyApiKey) && !_tavilyKeyDisabled)
        {
            var (tavilyResult, tavilyOk) = await SearchTavilyAsync(query, maxResults);
            if (tavilyOk)
            {
                return tavilyResult;
            }
            Console.WriteLine($"[WebSearch] Tavily failed ({tavilyResult}); falling back to DuckDuckGo.");
        }

        return await SearchDuckDuckGoAsync(query, maxResults);
    }

    private async Task<(string text, bool ok)> SearchTavilyAsync(string query, int maxResults)
    {
        try
        {
            var payload = new
            {
                api_key = _tavilyApiKey,
                query = query,
                search_depth = "basic",
                max_results = maxResults,
                include_answer = false,
                include_raw_content = false
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request);

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _tavilyKeyDisabled = true;
                return ($"Tavily auth rejected ({(int)response.StatusCode}); key disabled for this session", false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ($"Tavily returned HTTP {(int)response.StatusCode}", false);
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return ($"No results found for: {query}", true);
            }

            var lines = new List<string>();
            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                if (count >= maxResults) break;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var resultUrl = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                title = StripHtml(WebUtility.HtmlDecode(title));
                snippet = StripHtml(WebUtility.HtmlDecode(snippet));

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(resultUrl)) continue;

                count++;
                lines.Add($"{count}. {title}");
                lines.Add($"   URL: {resultUrl}");
                if (!string.IsNullOrEmpty(snippet))
                {
                    lines.Add($"   {snippet}");
                }
                lines.Add("");
            }

            if (lines.Count == 0)
            {
                return ($"No results found for: {query}", true);
            }

            return ($"Search results for \"{query}\":\n\n{string.Join("\n", lines).TrimEnd()}", true);
        }
        catch (TaskCanceledException)
        {
            return ("Tavily request timed out", false);
        }
        catch (Exception ex)
        {
            return ($"Tavily error: {ex.Message}", false);
        }
    }

    private async Task<string> SearchDuckDuckGoAsync(string query, int maxResults)
    {
        try
        {
            var formData = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("q", query),
                new KeyValuePair<string, string>("b", ""),
                new KeyValuePair<string, string>("kl", "us-en")
            });

            var response = await _httpClient.PostAsync("https://html.duckduckgo.com/html/", formData);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'result')]");
            if (resultNodes == null || resultNodes.Count == 0)
            {
                return $"No results found for: {query}";
            }

            var results = new List<string>();
            var count = 0;

            foreach (var node in resultNodes)
            {
                if (count >= maxResults) break;

                var linkNode = node.SelectSingleNode(".//a[contains(@class, 'result__a')]");
                var snippetNode = node.SelectSingleNode(".//*[contains(@class, 'result__snippet')]");

                if (linkNode == null) continue;

                var title = WebUtility.HtmlDecode(linkNode.InnerText.Trim());
                var rawUrl = linkNode.GetAttributeValue("href", "");
                var snippet = snippetNode != null
                    ? WebUtility.HtmlDecode(snippetNode.InnerText.Trim())
                    : "";

                var url = ExtractDuckDuckGoUrl(rawUrl);

                if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url)) continue;

                count++;
                results.Add($"{count}. {title}");
                results.Add($"   URL: {url}");
                if (!string.IsNullOrEmpty(snippet))
                {
                    results.Add($"   {snippet}");
                }
                results.Add("");
            }

            if (results.Count == 0)
            {
                return $"No results found for: {query}";
            }

            return $"Search results for \"{query}\":\n\n{string.Join("\n", results).TrimEnd()}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Search request timed out for query: {query}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Failed to search the web: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error searching the web: {ex.Message}";
        }
    }

    [KernelFunction("fetch_webpage")]
    [Description("Fetches a webpage URL and extracts its readable text content. Use this to read documentation pages, articles, blog posts, or any web page the user wants to learn about.")]
    public async Task<string> FetchWebpage(
        [Description("The URL of the webpage to fetch")] string url,
        [Description("Maximum characters of content to return (500-15000, default 5000)")] int maxCharacters = 5000)
    {
        maxCharacters = Math.Clamp(maxCharacters, 500, 15000);

        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return $"Error: Invalid URL: {url}. Must be an http or https URL.";
            }

            var response = await _httpClient.GetAsync(uri);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync();

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var titleNode = doc.DocumentNode.SelectSingleNode("//title");
            var title = titleNode != null
                ? WebUtility.HtmlDecode(titleNode.InnerText.Trim())
                : "";

            var tagsToRemove = new[] { "script", "style", "nav", "footer", "header", "aside", "iframe", "form", "noscript", "svg" };
            foreach (var tag in tagsToRemove)
            {
                var nodes = doc.DocumentNode.SelectNodes($"//{tag}");
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        node.Remove();
                    }
                }
            }

            var bodyNode = doc.DocumentNode.SelectSingleNode("//body");
            var rawText = bodyNode != null
                ? WebUtility.HtmlDecode(bodyNode.InnerText)
                : WebUtility.HtmlDecode(doc.DocumentNode.InnerText);

            var cleanText = CollapseWhitespace(rawText);

            if (string.IsNullOrWhiteSpace(cleanText))
            {
                return $"Error: No readable text content found at: {url}";
            }

            if (cleanText.Length > maxCharacters)
            {
                cleanText = cleanText[..maxCharacters] + "\n\n[Content truncated at " + maxCharacters + " characters]";
            }

            var header = !string.IsNullOrEmpty(title)
                ? $"Page: {title}\nURL: {url}\n\n"
                : $"URL: {url}\n\n";

            return header + cleanText;
        }
        catch (TaskCanceledException)
        {
            return $"Error: Request timed out fetching: {url}";
        }
        catch (HttpRequestException ex)
        {
            return $"Error: Failed to fetch webpage: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error fetching webpage: {ex.Message}";
        }
    }

    private static string ExtractDuckDuckGoUrl(string rawUrl)
    {
        if (string.IsNullOrEmpty(rawUrl)) return rawUrl;

        var match = Regex.Match(rawUrl, @"[?&]uddg=([^&]+)");
        if (match.Success)
        {
            return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        if (rawUrl.StartsWith("//"))
        {
            return "https:" + rawUrl;
        }

        return rawUrl;
    }

    private static string StripHtml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Regex.Replace(text, "<.*?>", "").Trim();
    }

    private static string CollapseWhitespace(string text)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .ToList();

        var result = new List<string>();
        bool lastWasEmpty = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (!lastWasEmpty)
                {
                    result.Add("");
                    lastWasEmpty = true;
                }
            }
            else
            {
                var collapsed = Regex.Replace(line, @"[ \t]+", " ");
                result.Add(collapsed);
                lastWasEmpty = false;
            }
        }

        return string.Join("\n", result).Trim();
    }
}
