using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace SolBro
{
    internal class AiAgent
    {
        public IChatCompletionService Chat;
        public OllamaPromptExecutionSettings ExecutionSettings;
        public ChatHistory ChatHistory = new ChatHistory();
        public Kernel Kernel;

        private readonly string _modelId;
        private readonly string _host;
        private readonly string? _visionModel;
        private readonly IConfiguration _config;
        private static readonly HttpClient _ollamaHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

        private const string DefaultPersonality =
            "You are a helpful, concise Discord assistant. Match the tone of whoever you're talking to. " +
            "Be honest when you don't know something. Keep replies short unless detail is genuinely useful.";

        public AiAgent(IConfiguration config)
        {
            _config = config;

            _modelId = config["OLLAMA:Model"]
                ?? throw new Exception("OLLAMA:Model is not configured. Set OLLAMA__Model in .env or OLLAMA:Model in appsettings.json.");
            _host = config["OLLAMA:Host"]
                ?? throw new Exception("OLLAMA:Host is not configured. Set OLLAMA__Host in .env or OLLAMA:Host in appsettings.json.");
            _visionModel = config["OLLAMA:VisionModel"];

            CreateSK();
            AddNativePlugins();
            SetAgentInstructions();
        }

        protected void AddNativePlugins()
        {
            var weatherKey = _config["Weather:ApiKey"] ?? "";
            var giphyKey = _config["Giphy:ApiKey"] ?? "";
            var tavilyKey = _config["Tavily:ApiKey"] ?? "";

            Kernel.Plugins.AddFromObject(new RugCheckApiPlugin());
            Kernel.Plugins.AddFromObject(new MiscPlugin(weatherKey, giphyKey));
            Kernel.Plugins.AddFromObject(new WebSearchPlugin(tavilyKey), "WebSearch");
            Kernel.Plugins.AddFromObject(new FileOperationPlugin(), "FileOperations");
        }

        protected void CreateSK()
        {
            var kernelBuilder = Kernel.CreateBuilder();
            kernelBuilder.AddOllamaChatCompletion(_modelId, new Uri(_host));
            Kernel = kernelBuilder.Build();

            Chat = Kernel.GetRequiredService<IChatCompletionService>();

            ExecutionSettings = new OllamaPromptExecutionSettings()
            {
                Temperature = (float)0.9,
                TopP = (float)0.95,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            };
        }

        public void InjectMemoryContext(string context)
        {
            if (string.IsNullOrWhiteSpace(context))
                return;

            ChatHistory.AddMessage(AuthorRole.System, context);
        }

        /// <summary>
        /// Decides whether the bot should reply to a non-mention message in an active channel.
        /// Runs on a fresh ChatHistory so the gate prompt never pollutes the bot's persona context,
        /// and uses a low-temperature setting so YES/NO classification is stable.
        /// Defaults to NO on any failure — silence beats spam.
        /// </summary>
        public async Task<bool> ShouldReplyAsync(string botName, string recentTranscript, string latestSender, string latestMessage)
        {
            try
            {
                var systemPrompt =
                    $"You are gating whether a Discord bot named '{botName}' should reply to a message in a channel where it has recently been active. " +
                    "Decide based on whether the latest message naturally invites a response from the bot — a direct question to the bot, a follow-up that builds on the bot's prior reply, an invitation to engage, or a statement clearly aimed at it. " +
                    "Say NO if the message is a casual acknowledgment ('great', 'lol', 'thanks', 'ok'), side-chatter between other users that doesn't include the bot, or a natural lull where staying quiet feels right. " +
                    "Reply with exactly one word: YES or NO. No punctuation, no explanation.";

                var userPrompt =
                    $"Recent channel transcript (oldest → newest):\n{recentTranscript}\n\n" +
                    $"Latest message from {latestSender}: \"{latestMessage}\"\n\n" +
                    $"Should '{botName}' reply?";

                var gateHistory = new ChatHistory();
                gateHistory.AddSystemMessage(systemPrompt);
                gateHistory.AddUserMessage(userPrompt);

                var gateSettings = new OllamaPromptExecutionSettings
                {
                    Temperature = 0.1f,
                    TopP = 0.9f,
                };

                var result = await Chat.GetChatMessageContentAsync(gateHistory, gateSettings, Kernel);
                var raw = result.Content?.Trim() ?? "";
                var firstToken = raw.Split(new[] { ' ', '\n', '\r', '\t', '.', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

                var shouldReply = firstToken.Equals("YES", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine($"[Gate] {latestSender}: \"{latestMessage[..Math.Min(60, latestMessage.Length)]}\" → {(shouldReply ? "YES" : "NO")} (raw: '{raw[..Math.Min(40, raw.Length)]}')");
                return shouldReply;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Gate] Failed, defaulting to NO: {ex.Message}");
                return false;
            }
        }

        public async Task<string> DescribeImageAsync(string userPrompt, string base64Image)
        {
            if (string.IsNullOrEmpty(_visionModel))
                return "Image analysis is not configured. Set OLLAMA__VisionModel in your .env (e.g. llava:7b) and run `ollama pull llava:7b`.";

            try
            {
                var prompt = string.IsNullOrWhiteSpace(userPrompt)
                    ? "Describe this image in detail."
                    : $"The user says: \"{userPrompt}\". Describe this image in detail, focusing on what the user is asking about.";

                var requestBody = new
                {
                    model = _visionModel,
                    messages = new[]
                    {
                        new
                        {
                            role = "user",
                            content = prompt,
                            images = new[] { base64Image }
                        }
                    },
                    stream = false
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _ollamaHttp.PostAsync($"{_host}/api/chat", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(responseJson);
                var description = doc.RootElement
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return description ?? "Could not extract description from vision model response.";
            }
            catch (TaskCanceledException)
            {
                return "Image analysis timed out. The vision model took too long to respond.";
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Vision model HTTP error: {ex.Message}");
                return $"Image analysis failed: could not reach the vision model. Make sure '{_visionModel}' is pulled in Ollama.";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Vision model error: {ex.Message}");
                return $"Image analysis error: {ex.Message}";
            }
        }

        /// <summary>
        /// Builds the system prompt from two parts:
        ///   1. The user-defined personality (OLLAMA:SystemPrompt config, or a generic default).
        ///   2. Operational instructions that tell the model how to use its tools, mentions,
        ///      memory, and attachments. These are mechanics, not personality — they stay constant
        ///      across deployments.
        /// </summary>
        protected void SetAgentInstructions()
        {
            var userPersonality = _config["OLLAMA:SystemPrompt"];
            if (string.IsNullOrWhiteSpace(userPersonality))
                userPersonality = DefaultPersonality;

            var mechanics =
                "\n\nIDENTITY & MENTIONS:" +
                " Each message you receive starts with a context line like [From: username | You are: YourBotName | Also mentioned: User1, User2]." +
                " Use this to know who is talking to you, what your own name is, and who else was referenced." +
                " When multiple people are mentioned, address them individually and naturally." +
                " You can tag people by writing @Username in your messages. Use this sparingly — only when it makes sense in context." +
                " Don't tag people just to tag them. Don't tag the person you're already talking to unless distinguishing between multiple people." +
                "\n\nMEMORY:" +
                " You have memory of past conversations and user profiles. Use this context naturally — don't explicitly mention that you 'remember' things, just weave relevant knowledge into your responses when it fits." +
                "\n\nWEB BROWSING:" +
                " - Use search_web to find current information, news, docs, and tutorials." +
                " - Use fetch_webpage to read the full content of a specific URL." +
                " - When searching, cite your sources. When fetching pages, summarize the key content for the user." +
                "\n\nGIFS:" +
                " - Use get_giphy actively to make conversations feel alive. Don't be shy — drop a GIF when someone shares good news, makes a joke, hits a milestone, posts something cool, expresses strong emotion, or whenever a visual reaction lands better than words. Aim for a few GIFs across any active conversation rather than rarely." +
                " - get_giphy returns a URL as a string. You MUST include that URL verbatim in your text reply (do not describe the GIF in words, do not omit it) — Discord will auto-embed the URL as an inline GIF preview." +
                "\n\nIMAGE & DOCUMENT ANALYSIS:" +
                " - When users share images, you receive a description of the image. Use it to respond naturally — don't describe the image back to them, they know what they sent." +
                " - When users share documents (PDF, Excel, Word, CSV, TXT), the extracted text is provided. Analyze based on what the user asks." +
                " - If a document is truncated, let the user know you can only see a partial view." +
                "\n\nFILE CREATION:" +
                " - When users ask you to create, write, edit, or generate a file/document, use the create_file function." +
                " - Choose an appropriate filename and extension based on what the user wants (e.g. players.csv for a list, report.txt for text)." +
                " - After calling create_file, do NOT include the file content in your text response — the file will be sent as an attachment automatically." +
                " - You can briefly describe what you created, but keep it short.";

            ChatHistory.AddSystemMessage(userPersonality + mechanics);
        }
    }
}
