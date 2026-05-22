using System.Collections.Concurrent;
using System.Threading.Channels;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;


namespace SolBro
{

    public class Bot
    {
        private readonly DiscordSocketClient _client;
        private string DiscordToken = String.Empty;
        private readonly AiAgent _aiAgent;
        private readonly BotMemory _memory;
        private static readonly Random _random = new();

        // Per-channel attention mode: tracks which channels the bot is actively following.
        // Value is the timestamp of the last activity — auto-deactivates after timeout.
        private readonly Dictionary<ulong, DateTime> _activeChannels = new();
        private static readonly TimeSpan _attentionTimeout = TimeSpan.FromMinutes(5);

        // Channels explicitly silenced via /stfu — no passive reactions either.
        private readonly HashSet<ulong> _silencedChannels = new();

        // Passive observation rate (% chance per message in non-active, non-silenced channels).
        // Adjustable at runtime via /passive-rate.
        private int _passiveReactionPercent = 10;

        private static readonly Regex _socialMediaUrlRegex = new(
            @"https?://(?:www\.)?(?:" +
            @"(?:instagram\.com|instagr\.am)/(?:p|reel|reels|tv)/[\w-]+" +
            @"|(?:x\.com|twitter\.com)/\w+/status/\d+" +
            @"|(?:facebook\.com|fb\.watch|fb\.com)/[^\s]+" +
            @"|(?:tiktok\.com|vm\.tiktok\.com)/[^\s]+" +
            @"|youtube\.com/shorts/[\w-]+" +
            @")", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string VideoDownloadDir = "video_downloads";
        private const long MaxFileSizeBytesGuild = 25 * 1024 * 1024;
        private const long MaxFileSizeBytesDM = 8 * 1024 * 1024;
        private readonly HashSet<ulong> _videoPostMessageIds = new();

        private readonly HashSet<ulong> _liveStreamers = new();
        private string _streamNotificationChannel = "gaming-general";

        private const long MaxImageSizeBytes = 10 * 1024 * 1024;
        private const long MaxDocumentSizeBytes = 10 * 1024 * 1024;
        private static readonly HttpClient _downloadClient = new() { Timeout = TimeSpan.FromSeconds(30) };

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
            { ".pdf", ".xlsx", ".docx", ".csv", ".txt", ".log", ".json", ".xml", ".md" };

        private readonly ConcurrentDictionary<ulong, Channel<QueuedMessage>> _channelQueues = new();
        private readonly ConcurrentDictionary<ulong, Task> _channelProcessors = new();
        private readonly ConcurrentDictionary<ulong, string> _channelBusyStatus = new();
        private const string GeneratedFilesDir = "generated_files";

        private record ReactionRecord(string Username, string Emoji, string MessagePreview, DateTime Timestamp);
        private readonly ConcurrentDictionary<ulong, List<ReactionRecord>> _reactionBuffers = new();
        private const int MaxReactionsPerChannel = 15;

        public Bot()
        {
            // Load .env into process environment variables if present. Silently no-ops if the file is missing.
            DotNetEnv.Env.Load();

            var config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            DiscordToken = config["Discord_Token"]
                ?? throw new Exception("Discord token is missing! Set Discord_Token in your .env file (see .env.example).");
            _streamNotificationChannel = config["StreamNotificationChannel"] ?? _streamNotificationChannel;
            Console.WriteLine("Token loaded successfully!");

            this._client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.AllUnprivileged
                    | GatewayIntents.MessageContent
                    | GatewayIntents.GuildPresences,
                LogLevel = LogSeverity.Info
            });
            this._client.Log += msg => { Console.WriteLine($"[Discord] {msg}"); return Task.CompletedTask; };
            this._client.MessageReceived += MessageHandler;
            this._client.ReactionAdded += ReactionHandler;
            this._client.PresenceUpdated += PresenceHandler;
            this._client.Ready += RegisterSlashCommandsAsync;
            this._client.SlashCommandExecuted += HandleSlashCommandAsync;
            this._aiAgent = new AiAgent(config);
            this._memory = new BotMemory();
            _memory.InitializeAsync().GetAwaiter().GetResult();
        }


        public async Task Start()
        {
            await this._client.LoginAsync(Discord.TokenType.Bot, DiscordToken);
            Console.WriteLine("Bot Successfully Logged In");
            await this._client.StartAsync();
            Console.WriteLine("Bot is running!");

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Shutdown();
            };

            await Task.Delay(-1);
        }

        private void Shutdown()
        {
            Console.WriteLine("\n Shutting down bot...");
            _client.StopAsync().Wait();
            _client.Dispose();
            Console.WriteLine("Shutdown complete.");
            Environment.Exit(0);
        }

        private async Task RegisterSlashCommandsAsync()
        {
            var commands = new[]
            {
                new SlashCommandBuilder()
                    .WithName("stfu")
                    .WithDescription("Silence the bot in this channel")
                    .Build(),
                new SlashCommandBuilder()
                    .WithName("passive-rate")
                    .WithDescription("View or set the bot's passive reaction rate (0-100%)")
                    .AddOption("percent", ApplicationCommandOptionType.Integer,
                        "New rate as a whole number 0-100. Omit to view the current rate.",
                        isRequired: false)
                    .Build()
            };

            try
            {
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(Array.Empty<ApplicationCommandProperties>());

                foreach (var guild in _client.Guilds)
                {
                    await guild.BulkOverwriteApplicationCommandAsync(commands);
                    Console.WriteLine($"Slash commands registered for guild: {guild.Name}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to register slash commands: {ex.Message}");
            }
        }

        private async Task HandleSlashCommandAsync(SocketSlashCommand command)
        {
            try
            {
                switch (command.Data.Name)
                {
                    case "stfu":
                        DeactivateChannel(command.Channel.Id);
                        await command.RespondAsync("Stepping back from this channel. Tag me if you need me.");
                        break;

                    case "passive-rate":
                        await HandlePassiveRate(command);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Slash command error ({command.Data.Name}): {ex.Message}");
                await ReportErrorAsync(command.Channel, ex, $"Slash command: /{command.Data.Name}");
            }
        }

        private record QueuedMessage(SocketMessage Message, string UserMessage, bool IsDirectResponse);

        private Task MessageHandler(SocketMessage message)
        {
            _ = Task.Run(() => MessageHandlerAsync(message));
            return Task.CompletedTask;
        }

        private async Task MessageHandlerAsync(SocketMessage message)
        {
            try
            {
                if (message.Author.IsBot)
                    return;
                if (string.IsNullOrEmpty(message.CleanContent.Trim()) && message.Attachments.Count == 0)
                    return;

                Console.WriteLine($"Received message: {message.Content ?? String.Empty}");
                var userMessage = message.CleanContent ?? String.Empty;

                if (message.Channel is not IDMChannel)
                {
                    var guildChannel = message.Channel as SocketGuildChannel;
                    var guildId = guildChannel?.Guild.Id ?? 0;

                    _memory.BufferMessage(message.Channel.Id, message.Author.Username, userMessage, guildId);

                    if (_memory.IsBurstActive(message.Channel.Id))
                    {
                        _ = Task.Run(() => _memory.SummarizeAndStoreAsync(
                            message.Channel.Id, _aiAgent.Chat, _aiAgent.Kernel));
                    }

                    _ = Task.Run(() => _memory.UpdateUserProfileAsync(
                        message.Author.Id, message.Author.Username, userMessage, _aiAgent.Chat, _aiAgent.Kernel));

                    if (guildId != 0)
                    {
                        _ = Task.Run(() => _memory.UpdateServerCultureAsync(guildId, _aiAgent.Chat, _aiAgent.Kernel));
                    }
                }

                var urlMatch = _socialMediaUrlRegex.Match(userMessage);
                if (urlMatch.Success)
                {
                    var hadVideo = await HandleVideoExtraction(message, urlMatch.Value);
                    if (hadVideo)
                        return;
                }

                bool isDirectResponse = false;

                if (message.Channel is IDMChannel)
                {
                    isDirectResponse = true;
                }
                else
                {
                    var channelId = message.Channel.Id;
                    var isMentioned = message.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);

                    if (isMentioned)
                    {
                        ActivateChannel(channelId);
                        isDirectResponse = true;
                    }
                    else if (IsChannelActive(channelId))
                    {
                        RefreshChannel(channelId);

                        var transcript = await BuildRecentTranscriptAsync(message);
                        var botName = _client.CurrentUser?.Username ?? "Bot";
                        var shouldReply = await _aiAgent.ShouldReplyAsync(
                            botName, transcript, message.Author.Username, userMessage);

                        if (shouldReply)
                            isDirectResponse = true;
                        else
                            return;
                    }
                    else if (!_silencedChannels.Contains(channelId) && _random.Next(100) < _passiveReactionPercent)
                    {
                        await HandlePassiveObservation(message, userMessage);
                        return;
                    }
                }

                if (isDirectResponse)
                {
                    EnqueueMessage(message, userMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unhandled error in MessageHandler: {ex}");
                try
                {
                    await ReportErrorAsync(message.Channel, ex, "Unhandled error in message processing pipeline");
                }
                catch
                {
                    Console.WriteLine($"Failed to report error to Discord: {ex.Message}");
                }
            }
        }

        private void EnqueueMessage(SocketMessage message, string userMessage)
        {
            var channelId = message.Channel.Id;

            var queue = _channelQueues.GetOrAdd(channelId, _ =>
                Channel.CreateUnbounded<QueuedMessage>(new UnboundedChannelOptions { SingleReader = true }));

            if (_channelBusyStatus.TryGetValue(channelId, out var busyTask))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await message.Channel.SendMessageAsync(
                            $"Hang tight — I'm currently {busyTask}. I'll get to you right after!");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Queue] Failed to send busy ack: {ex.Message}");
                    }
                });
            }

            queue.Writer.TryWrite(new QueuedMessage(message, userMessage, true));
            Console.WriteLine($"[Queue] Enqueued message for channel {channelId} from {message.Author.Username}");

            _channelProcessors.GetOrAdd(channelId, id =>
                Task.Run(() => ProcessChannelQueueAsync(id)));
        }

        private async Task ProcessChannelQueueAsync(ulong channelId)
        {
            Console.WriteLine($"[Queue] Processor started for channel {channelId}");

            var queue = _channelQueues[channelId];

            await foreach (var queued in queue.Reader.ReadAllAsync())
            {
                try
                {
                    Console.WriteLine($"[Queue] Processing message from {queued.Message.Author.Username} in channel {channelId}");
                    await HandleDirectMessage(queued.Message, queued.UserMessage);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Queue] Error processing message in channel {channelId}: {ex.Message}");
                    try
                    {
                        await ReportErrorAsync(queued.Message.Channel, ex,
                            $"Queued message from {queued.Message.Author.Username}");
                    }
                    catch { }
                }
            }

            _channelProcessors.TryRemove(channelId, out _);
            Console.WriteLine($"[Queue] Processor ended for channel {channelId}");
        }

        private void ActivateChannel(ulong channelId)
        {
            _activeChannels[channelId] = DateTime.UtcNow;
            _silencedChannels.Remove(channelId);
            Console.WriteLine($"Channel {channelId} attention mode: ON");
        }

        private void DeactivateChannel(ulong channelId)
        {
            _activeChannels.Remove(channelId);
            _silencedChannels.Add(channelId);
            Console.WriteLine($"Channel {channelId} attention mode: OFF (silenced)");
        }

        private void RefreshChannel(ulong channelId)
        {
            _activeChannels[channelId] = DateTime.UtcNow;
        }

        private bool IsChannelActive(ulong channelId)
        {
            if (!_activeChannels.TryGetValue(channelId, out var lastActivity))
                return false;

            if (DateTime.UtcNow - lastActivity > _attentionTimeout)
            {
                _activeChannels.Remove(channelId);
                Console.WriteLine($"Channel {channelId} attention timed out — back to default.");
                return false;
            }

            return true;
        }

        private async Task HandleDirectMessage(SocketMessage message, string userMessage)
        {
            var channelId = message.Channel.Id;
            try
            {
                var busyDescription = "thinking about a response";
                if (message.Attachments.Count > 0)
                {
                    var hasImages = message.Attachments.Any(a => ImageExtensions.Contains(Path.GetExtension(a.Filename)));
                    var hasDocs = message.Attachments.Any(a => DocumentExtensions.Contains(Path.GetExtension(a.Filename)));
                    if (hasImages && hasDocs) busyDescription = "analyzing an image and reading a document";
                    else if (hasImages) busyDescription = "analyzing an image";
                    else if (hasDocs) busyDescription = "reading a document";
                }
                _channelBusyStatus[channelId] = busyDescription;

                using var typing = message.Channel.EnterTypingState();

                var guildChannel = message.Channel as SocketGuildChannel;
                var guildId = guildChannel?.Guild.Id ?? 0;
                var memoryContext = await _memory.GetContextForResponse(message.Channel.Id, message.Author.Id, guildId);
                _aiAgent.InjectMemoryContext(memoryContext);

                var attachmentContext = new StringBuilder();
                string? imageBase64 = null;
                string? imageFilename = null;

                if (message.Attachments.Count > 0)
                {
                    var imageAttachments = message.Attachments
                        .Where(a => ImageExtensions.Contains(Path.GetExtension(a.Filename)))
                        .ToList();
                    var documentAttachments = message.Attachments
                        .Where(a => DocumentExtensions.Contains(Path.GetExtension(a.Filename)))
                        .Take(3)
                        .ToList();

                    foreach (var docAttachment in documentAttachments)
                    {
                        var bytes = await DownloadAttachmentAsync(docAttachment, MaxDocumentSizeBytes);
                        if (bytes == null)
                        {
                            attachmentContext.AppendLine($"[Document: {docAttachment.Filename} — file too large or failed to download]");
                            continue;
                        }

                        using var stream = new MemoryStream(bytes);
                        var extractedText = await DocumentTextExtractor.ExtractTextAsync(stream, docAttachment.Filename);
                        attachmentContext.AppendLine($"[Document: {docAttachment.Filename}]");
                        attachmentContext.AppendLine(extractedText);
                        attachmentContext.AppendLine();
                        Console.WriteLine($"Extracted {extractedText.Length} chars from {docAttachment.Filename}");
                    }

                    var firstImage = imageAttachments.FirstOrDefault();
                    if (firstImage != null)
                    {
                        var imageBytes = await DownloadAttachmentAsync(firstImage, MaxImageSizeBytes);
                        if (imageBytes != null)
                        {
                            imageBase64 = Convert.ToBase64String(imageBytes);
                            imageFilename = firstImage.Filename;
                            Console.WriteLine($"Downloaded image {firstImage.Filename} ({imageBytes.Length} bytes)");
                        }
                        else
                        {
                            attachmentContext.AppendLine($"[Image: {firstImage.Filename} — file too large or failed to download]");
                        }
                    }
                }

                var effectiveUserMessage = userMessage;
                if (string.IsNullOrWhiteSpace(effectiveUserMessage) && message.Attachments.Count > 0)
                {
                    effectiveUserMessage = imageBase64 != null
                        ? "Describe this image"
                        : "Analyze this document";
                }

                if (imageBase64 != null)
                {
                    var description = await _aiAgent.DescribeImageAsync(effectiveUserMessage, imageBase64);
                    attachmentContext.Insert(0, $"[Image description of {imageFilename}: {description}]\n\n");
                    Console.WriteLine($"Vision model described {imageFilename}: {description[..Math.Min(100, description.Length)]}...");
                }

                var messageContext = BuildMessageContext(message, effectiveUserMessage);

                if (attachmentContext.Length > 0)
                {
                    messageContext += "\n\n" + attachmentContext.ToString().TrimEnd();
                }

                var reactionContext = DrainReactionContext(channelId);
                if (reactionContext != null)
                {
                    messageContext += "\n\n" + reactionContext;
                }

                _aiAgent.ChatHistory.AddMessage(AuthorRole.User, messageContext);
                var aiResponse = await _aiAgent.Chat.GetChatMessageContentAsync(_aiAgent.ChatHistory, _aiAgent.ExecutionSettings, _aiAgent.Kernel);
                _aiAgent.ChatHistory.AddMessage(aiResponse.Role, aiResponse.Content ?? string.Empty);

                var responseText = aiResponse.Content ?? string.Empty;

                var fileResult = FindGeneratedFileInHistory();

                if (fileResult != null)
                {
                    await HandleGeneratedFileResponse(message, fileResult, responseText);
                }
                else
                {
                    await ReplyAsync(message, responseText);
                }
            }
            catch (TaskCanceledException ex)
            {
                Console.WriteLine($"AI response timed out for message from {message.Author.Username}");
                await ReportErrorAsync(message.Channel, ex, $"AI response timed out for message from {message.Author.Username}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling message from {message.Author.Username}: {ex.Message}");
                await ReportErrorAsync(message.Channel, ex, $"Handling message from {message.Author.Username}: \"{userMessage}\"");
            }
            finally
            {
                _channelBusyStatus.TryRemove(channelId, out _);
            }
        }

        private async Task HandlePassiveObservation(SocketMessage message, string userMessage)
        {
            try
            {
                var guildChannel = message.Channel as SocketGuildChannel;
                var guildId = guildChannel?.Guild.Id ?? 0;
                var memoryContext = await _memory.GetContextForResponse(message.Channel.Id, message.Author.Id, guildId);
                _aiAgent.InjectMemoryContext(memoryContext);

                var user = message.Author.Username;

                string? imageDescription = null;
                var imageAttachment = message.Attachments
                    .FirstOrDefault(a => ImageExtensions.Contains(Path.GetExtension(a.Filename)));
                if (imageAttachment != null)
                {
                    var imageBytes = await DownloadAttachmentAsync(imageAttachment, MaxImageSizeBytes);
                    if (imageBytes != null)
                    {
                        var base64 = Convert.ToBase64String(imageBytes);
                        imageDescription = await _aiAgent.DescribeImageAsync("Describe this image briefly.", base64);
                        Console.WriteLine($"Passive image description for {imageAttachment.Filename}: {imageDescription?[..Math.Min(100, imageDescription?.Length ?? 0)]}");
                    }
                }

                string prompt;
                if (imageDescription != null)
                {
                    var textPart = string.IsNullOrWhiteSpace(userMessage) ? "" : $" They also said: \"{userMessage}\".";
                    prompt = $"[You are passively observing a Discord conversation. {user} shared an image.{textPart} " +
                        $"Here's what the image shows: {imageDescription}\n\n" +
                        "React to this image the way a real person scrolling through a chat would. You can: " +
                        "drop an emoji reaction, make a quick comment, relate it to something you know, " +
                        "or if it's interesting enough, look something up with your web search tools. " +
                        "Do NOT describe the image back to the user — they know what they posted. " +
                        "Be genuine. Sometimes a single emoji is the perfect response. " +
                        "To add an emoji reaction, start your response with EMOJI:<unicode_emoji> on its own line (e.g. EMOJI:fire or EMOJI:skull). " +
                        "You can also include a short text reply after the EMOJI line if you want to say something. " +
                        "If you want to ONLY react with an emoji and no text, respond with just the EMOJI line. " +
                        "If you want to ONLY send text, just respond with text. " +
                        "Keep any text replies short and casual.]";
                }
                else
                {
                    prompt = $"[You are passively observing a Discord conversation. {user} said: \"{userMessage}\". " +
                        "You may react naturally if the vibe calls for it. " +
                        "To add an emoji reaction, start your response with EMOJI:<unicode_emoji> on its own line (e.g. EMOJI:fire or EMOJI:skull). " +
                        "You can also include a short text reply after the EMOJI line if you want to say something. " +
                        "If you want to ONLY react with an emoji and no text, respond with just the EMOJI line. " +
                        "If you want to ONLY send text, just respond with text. " +
                        "Keep any text replies short and casual.]";
                }

                _aiAgent.ChatHistory.AddMessage(AuthorRole.User, prompt);
                var aiResponse = await _aiAgent.Chat.GetChatMessageContentAsync(
                    _aiAgent.ChatHistory, _aiAgent.ExecutionSettings, _aiAgent.Kernel);
                _aiAgent.ChatHistory.AddMessage(aiResponse.Role, aiResponse.Content ?? string.Empty);

                var responseText = aiResponse.Content ?? string.Empty;
                if (string.IsNullOrWhiteSpace(responseText))
                    return;

                Console.WriteLine($"Passive reaction to {user}: {responseText}");

                var lines = responseText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var emojiLines = lines.Where(l => l.TrimStart().StartsWith("EMOJI:", StringComparison.OrdinalIgnoreCase)).ToList();
                var textLines = lines.Where(l => !l.TrimStart().StartsWith("EMOJI:", StringComparison.OrdinalIgnoreCase)).ToList();

                foreach (var emojiLine in emojiLines)
                {
                    var emojiName = emojiLine.Split(':', 2)[1].Trim();
                    var emoji = EmojiMap(emojiName);
                    if (emoji != null)
                    {
                        try
                        {
                            await message.AddReactionAsync(emoji);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to add reaction {emojiName}: {ex.Message}");
                        }
                    }
                }

                var text = string.Join("\n", textLines).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    if (text.Length > 2000)
                        await SendAsTextFileAsync(message, text);
                    else
                        await message.Channel.SendMessageAsync(text);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Passive observation error: {ex.Message}");
                await ReportErrorAsync(message.Channel, ex, $"Passive observation of message from {message.Author.Username}");
            }
        }

        /// <summary>
        /// Attempts to extract and upload a video from a social media URL.
        /// Returns true if a video was found and handled, false if no video exists (so the message can fall through to AI).
        /// Requires yt-dlp installed on PATH.
        /// </summary>
        private async Task<bool> HandleVideoExtraction(SocketMessage message, string url)
        {
            try
            {
                Console.WriteLine($"Video extraction: {url}");

                var probeStartInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--simulate --no-playlist \"{url}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var probeProcess = new Process { StartInfo = probeStartInfo };
                try
                {
                    probeProcess.Start();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // yt-dlp not installed — silently skip video extraction.
                    Console.WriteLine("yt-dlp not found on PATH; skipping video extraction.");
                    return false;
                }
                var probeStderr = await probeProcess.StandardError.ReadToEndAsync();
                await probeProcess.WaitForExitAsync();

                if (probeProcess.ExitCode != 0)
                {
                    Console.WriteLine($"No video found at {url} — skipping extraction.");
                    return false;
                }

                if (message.Channel is not IDMChannel && message is IUserMessage userMsg)
                {
                    try
                    {
                        await userMsg.ModifyAsync(m => m.Flags = MessageFlags.SuppressEmbeds);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to suppress embed: {ex.Message}");
                    }
                }

                var isDM = message.Channel is IDMChannel;
                var maxBytes = isDM ? MaxFileSizeBytesDM : MaxFileSizeBytesGuild;
                var maxMB = maxBytes / (1024 * 1024);

                Directory.CreateDirectory(VideoDownloadDir);
                var outputTemplate = Path.Combine(VideoDownloadDir, "%(id)s.%(ext)s");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"-f \"best[filesize<{maxMB}M]/best[filesize_approx<{maxMB}M]/best\" " +
                                $"--merge-output-format mp4 " +
                                $"--no-playlist " +
                                $"--max-filesize {maxBytes} " +
                                $"-o \"{outputTemplate}\" " +
                                $"\"{url}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"yt-dlp failed (exit code {process.ExitCode}) for: {url}");
                    await RestoreEmbed(message);
                    return true;
                }

                var downloadedFile = Directory.GetFiles(VideoDownloadDir)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .FirstOrDefault();

                if (downloadedFile == null || !downloadedFile.Exists)
                {
                    Console.WriteLine("yt-dlp completed but no file found.");
                    await RestoreEmbed(message);
                    return true;
                }

                if (downloadedFile.Length > maxBytes)
                {
                    Console.WriteLine($"Video too large ({downloadedFile.Length / 1024 / 1024}MB), skipping upload.");
                    downloadedFile.Delete();
                    await RestoreEmbed(message);
                    return true;
                }

                var videoMsg = await message.Channel.SendFileAsync(
                    downloadedFile.FullName,
                    text: $"<@{message.Author.Id}> link/video shared:");

                _videoPostMessageIds.Add(videoMsg.Id);

                if (message.Channel is not IDMChannel && message is IUserMessage origMsg)
                {
                    try
                    {
                        await origMsg.DeleteAsync();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to delete original message: {ex.Message}");
                    }
                }

                downloadedFile.Delete();
                Console.WriteLine($"Video uploaded and local file deleted: {downloadedFile.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Video extraction failed for {url}: {ex.Message}");
                await RestoreEmbed(message);
                await ReportErrorAsync(message.Channel, ex, $"Video extraction for URL: {url}");
                return true;
            }
        }

        private async Task RestoreEmbed(SocketMessage message)
        {
            if (message.Channel is IDMChannel || message is not IUserMessage userMsg)
                return;

            try
            {
                await userMsg.ModifyAsync(m => m.Flags = MessageFlags.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not restore embed: {ex.Message}");
            }
        }

        private async Task PresenceHandler(SocketUser user, SocketPresence oldPresence, SocketPresence newPresence)
        {
            var wasStreaming = oldPresence?.Activities?.Any(a => a.Type == ActivityType.Streaming) ?? false;
            var isStreaming = newPresence?.Activities?.Any(a => a.Type == ActivityType.Streaming) ?? false;

            if (isStreaming && !wasStreaming)
            {
                if (!_liveStreamers.Add(user.Id))
                    return;

                var streamActivity = newPresence!.Activities.First(a => a.Type == ActivityType.Streaming);
                var streamUrl = (streamActivity as StreamingGame)?.Url ?? "";
                var streamTitle = streamActivity.Name ?? "Untitled Stream";

                Console.WriteLine($"Stream detected: {user.Username} is live — {streamTitle} ({streamUrl})");

                foreach (var guild in _client.Guilds)
                {
                    if (guild.GetUser(user.Id) == null)
                        continue;

                    var channel = guild.TextChannels
                        .FirstOrDefault(c => c.Name.Equals(_streamNotificationChannel, StringComparison.OrdinalIgnoreCase));

                    if (channel == null)
                    {
                        Console.WriteLine($"Channel '{_streamNotificationChannel}' not found in guild '{guild.Name}'");
                        continue;
                    }

                    var message = $"**{user.Username} is live!**\n" +
                                  $"**{streamTitle}**\n" +
                                  $"{streamUrl}";

                    await channel.SendMessageAsync(message);
                    Console.WriteLine($"Stream notification sent to #{channel.Name} in {guild.Name}");
                }
            }
            else if (!isStreaming && wasStreaming)
            {
                _liveStreamers.Remove(user.Id);
                Console.WriteLine($"{user.Username} stopped streaming.");
            }
        }

        private static Emoji? EmojiMap(string name)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["fire"] = "\U0001F525",
                ["skull"] = "\U0001F480",
                ["laughing"] = "\U0001F602",
                ["cry"] = "\U0001F602",
                ["rofl"] = "\U0001F923",
                ["100"] = "\U0001F4AF",
                ["rocket"] = "\U0001F680",
                ["eyes"] = "\U0001F440",
                ["clown"] = "\U0001F921",
                ["thumbsup"] = "\U0001F44D",
                ["thumbsdown"] = "\U0001F44E",
                ["heart"] = "❤️",
                ["pray"] = "\U0001F64F",
                ["cap"] = "\U0001F9E2",
                ["money"] = "\U0001F4B0",
                ["diamond"] = "\U0001F48E",
                ["moon"] = "\U0001F315",
                ["sob"] = "\U0001F62D",
                ["thinking"] = "\U0001F914",
                ["nerd"] = "\U0001F913",
                ["wave"] = "\U0001F44B",
                ["raised_hands"] = "\U0001F64C",
                ["goat"] = "\U0001F410",
                ["sunglasses"] = "\U0001F60E",
                ["dead"] = "\U0001F480",
                ["facepalm"] = "\U0001F926",
                ["crowned"] = "\U0001F451",
                ["handshake"] = "\U0001F91D",
                ["salute"] = "\U0001FAE1",
                ["w"] = "\U0001F1FC",
                ["l"] = "\U0001F1F1",
            };

            if (map.TryGetValue(name, out var unicode))
                return new Emoji(unicode);

            if (name.Length <= 4 && !name.All(char.IsLetterOrDigit))
                return new Emoji(name);

            return null;
        }

        private async Task ReactionHandler(
            Cacheable<IUserMessage, ulong> cachedMessage,
            Cacheable<IMessageChannel, ulong> cachedChannel,
            SocketReaction reaction)
        {
            if (reaction.UserId == _client.CurrentUser.Id)
                return;

            var channel = await cachedChannel.GetOrDownloadAsync();
            var message = await cachedMessage.GetOrDownloadAsync();
            if (channel == null || message == null)
                return;

            if (message.Author.Id != _client.CurrentUser.Id)
                return;

            if (_videoPostMessageIds.Contains(message.Id))
                return;

            var emoji = reaction.Emote.Name;
            var user = reaction.User.IsSpecified ? reaction.User.Value.Username : "Someone";
            var messagePreview = message.Content?.Length > 80
                ? message.Content[..80] + "..."
                : message.Content ?? "(no text)";

            Console.WriteLine($"Reaction noted: {user} reacted with {emoji} on bot's message: \"{messagePreview}\"");

            var channelId = channel.Id;
            var buffer = _reactionBuffers.GetOrAdd(channelId, _ => new List<ReactionRecord>());
            lock (buffer)
            {
                buffer.Add(new ReactionRecord(user, emoji, messagePreview, DateTime.UtcNow));
                if (buffer.Count > MaxReactionsPerChannel)
                    buffer.RemoveRange(0, buffer.Count - MaxReactionsPerChannel);
            }
        }

        private string? DrainReactionContext(ulong channelId)
        {
            if (!_reactionBuffers.TryGetValue(channelId, out var buffer))
                return null;

            List<ReactionRecord> reactions;
            lock (buffer)
            {
                if (buffer.Count == 0)
                    return null;
                reactions = new List<ReactionRecord>(buffer);
                buffer.Clear();
            }

            var sb = new StringBuilder();
            sb.AppendLine("[REACTION SENTIMENT — People reacted to your recent messages with these emoji. " +
                "Use this as passive context for how your responses landed. " +
                "Don't explicitly mention that you saw reactions unless it's natural to do so.]");
            foreach (var r in reactions)
            {
                sb.AppendLine($"  {r.Username} reacted {r.Emoji} to: \"{r.MessagePreview}\"");
            }

            return sb.ToString().TrimEnd();
        }

        private string? FindGeneratedFileInHistory()
        {
            var recentMessages = _aiAgent.ChatHistory.TakeLast(10);
            foreach (var historyMsg in recentMessages)
            {
                if (historyMsg.Content?.Contains("GENERATED_FILE:") == true)
                    return historyMsg.Content;

                foreach (var item in historyMsg.Items)
                {
                    if (item is FunctionResultContent funcResult)
                    {
                        var resultStr = funcResult.Result?.ToString();
                        if (resultStr?.Contains("GENERATED_FILE:") == true)
                            return resultStr;
                    }
                }
            }
            return null;
        }

        private async Task HandleGeneratedFileResponse(SocketMessage message, string fileResult, string responseText)
        {
            try
            {
                var parts = fileResult.Split('|');
                var filePath = parts[0].Replace("GENERATED_FILE:", "").Trim();
                var displayName = parts.Length > 1 ? parts[1].Replace("FILENAME:", "").Trim() : Path.GetFileName(filePath);

                if (File.Exists(filePath))
                {
                    var cleanResponse = responseText
                        .Replace(fileResult, "")
                        .Trim();

                    if (string.IsNullOrWhiteSpace(cleanResponse))
                        cleanResponse = $"Here's the file you asked for:";

                    using var fileStream = File.OpenRead(filePath);
                    await message.Channel.SendFileAsync(fileStream, displayName, text: cleanResponse);

                    try { File.Delete(filePath); }
                    catch { }

                    Console.WriteLine($"Sent generated file {displayName} to channel {message.Channel.Id}");
                }
                else
                {
                    Console.WriteLine($"Generated file not found: {filePath}");
                    await ReplyAsync(message, responseText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending generated file: {ex.Message}");
                await ReplyAsync(message, responseText);
            }
        }

        private async Task ReplyAsync(SocketMessage message, string response)
        {
            var lines = response.Split('\n');
            var emojiLines = lines.Where(l => l.TrimStart().StartsWith("EMOJI:", StringComparison.OrdinalIgnoreCase)).ToList();
            var textLines = lines.Where(l => !l.TrimStart().StartsWith("EMOJI:", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var emojiLine in emojiLines)
            {
                var emojiName = emojiLine.Split(':', 2)[1].Trim();
                var emoji = EmojiMap(emojiName);
                if (emoji != null)
                {
                    try { await message.AddReactionAsync(emoji); }
                    catch (Exception ex) { Console.WriteLine($"Failed to add reaction {emojiName}: {ex.Message}"); }
                }
            }

            var text = string.Join("\n", textLines).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                text = ResolveMentions(message, text);
                if (text.Length > 2000)
                    await SendAsTextFileAsync(message, text);
                else
                    await message.Channel.SendMessageAsync(text);
            }
        }

        private async Task SendAsTextFileAsync(SocketMessage message, string text)
        {
            Directory.CreateDirectory(GeneratedFilesDir);
            var filename = $"response_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
            var filepath = Path.Combine(GeneratedFilesDir, filename);

            try
            {
                await File.WriteAllTextAsync(filepath, text);
                await message.Channel.SendFileAsync(filepath,
                    text: "Response was too long for one Discord message — full text attached:");
                Console.WriteLine($"Sent long response ({text.Length} chars) as {filename} to channel {message.Channel.Id}");
            }
            finally
            {
                try { if (File.Exists(filepath)) File.Delete(filepath); } catch { }
            }
        }

        private string ResolveMentions(SocketMessage message, string text)
        {
            if (message.Channel is not SocketGuildChannel guildChannel)
                return text;

            var guild = guildChannel.Guild;

            return Regex.Replace(text, @"@(\S+)", match =>
            {
                var raw = match.Groups[1].Value;

                var name = raw.TrimEnd('.', ',', '!', '?', ':', ';', ')', ']', '}', '"', '\'');
                var trailingPunctuation = raw[name.Length..];

                var user = guild.Users.FirstOrDefault(u =>
                    u.Username.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (u.DisplayName ?? "").Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (u.GlobalName ?? "").Equals(name, StringComparison.OrdinalIgnoreCase));

                return user != null ? $"<@{user.Id}>{trailingPunctuation}" : match.Value;
            });
        }

        private async Task HandlePassiveRate(SocketSlashCommand command)
        {
            var option = command.Data.Options.FirstOrDefault(o => o.Name == "percent");
            if (option == null)
            {
                await command.RespondAsync($"Passive reaction rate is currently **{_passiveReactionPercent}%**.");
                return;
            }

            var requested = Convert.ToInt32(option.Value);
            var clamped = Math.Clamp(requested, 0, 100);
            _passiveReactionPercent = clamped;

            var note = clamped == requested
                ? $"Passive reaction rate set to **{clamped}%**."
                : $"Value clamped to allowed range — passive reaction rate set to **{clamped}%** (you asked for {requested}).";
            Console.WriteLine($"[PassiveRate] Updated to {clamped}% by {command.User.Username}");
            await command.RespondAsync(note);
        }

        private async Task<byte[]?> DownloadAttachmentAsync(IAttachment attachment, long maxSizeBytes)
        {
            if (attachment.Size > maxSizeBytes)
            {
                Console.WriteLine($"Attachment {attachment.Filename} too large ({attachment.Size} bytes, max {maxSizeBytes}).");
                return null;
            }

            try
            {
                return await _downloadClient.GetByteArrayAsync(attachment.Url);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to download attachment {attachment.Filename}: {ex.Message}");
                return null;
            }
        }

        private async Task<string> BuildRecentTranscriptAsync(SocketMessage currentMessage)
        {
            try
            {
                var botName = _client.CurrentUser?.Username ?? "Bot";
                var fetched = await currentMessage.Channel.GetMessagesAsync(6).FlattenAsync();
                var ordered = fetched
                    .Where(m => m.Id != currentMessage.Id)
                    .OrderBy(m => m.Timestamp)
                    .Take(5)
                    .ToList();

                if (ordered.Count == 0)
                    return "(no prior messages)";

                var sb = new StringBuilder();
                foreach (var m in ordered)
                {
                    var who = m.Author.Id == _client.CurrentUser.Id ? botName : m.Author.Username;
                    var content = (m.CleanContent ?? "").Trim();
                    if (string.IsNullOrEmpty(content))
                        continue;
                    sb.AppendLine($"{who}: {content}");
                }
                return sb.Length == 0 ? "(no prior messages)" : sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Gate] Failed to build transcript: {ex.Message}");
                return "(transcript unavailable)";
            }
        }

        private string BuildMessageContext(SocketMessage message, string userMessage)
        {
            var botName = _client.CurrentUser?.Username ?? "Bot";
            var sender = message.Author.Username;

            var otherMentions = message.MentionedUsers
                .Where(u => u.Id != _client.CurrentUser.Id && u.Id != message.Author.Id)
                .Select(u => u.Username)
                .ToList();

            var context = $"[From: {sender} | You are: {botName}";

            if (otherMentions.Count > 0)
                context += $" | Also mentioned: {string.Join(", ", otherMentions)}";

            context += $"]\n{userMessage}";

            return context;
        }

        private async Task ReportErrorAsync(IMessageChannel channel, Exception ex, string context)
        {
            try
            {
                var errorDetails = $"Exception: {ex.GetType().Name}\n" +
                                   $"Message: {ex.Message}\n" +
                                   $"Context: {context}\n" +
                                   $"Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "N/A"}";

                Console.WriteLine($"Error Report:\n{errorDetails}");

                var analysisPrompt = $"[SYSTEM: You just encountered an error while processing something. Analyze this exception and explain " +
                    $"what went wrong in first person, as if you're telling your developer what happened to you. " +
                    $"Keep it to 2-3 sentences max. Be direct.\n\n{errorDetails}]";

                var analysisHistory = new ChatHistory();
                analysisHistory.AddSystemMessage("You are a Discord bot explaining your own errors in first person. Be concise and direct.");
                analysisHistory.AddUserMessage(analysisPrompt);

                var analysis = await _aiAgent.Chat.GetChatMessageContentAsync(analysisHistory, _aiAgent.ExecutionSettings, _aiAgent.Kernel);
                var analysisText = analysis.Content ?? "Could not analyze the error.";

                var report = $"**Bot Error Report**\n" +
                             $"```\n{ex.GetType().Name}: {ex.Message}\n```\n" +
                             $"**Analysis:** {analysisText}";

                if (report.Length > 2000)
                    report = report[..1997] + "...";

                await channel.SendMessageAsync(report);
            }
            catch (Exception reportEx)
            {
                Console.WriteLine($"Error reporting failed: {reportEx.Message}");
                await channel.SendMessageAsync($"Something went wrong: `{ex.GetType().Name}: {ex.Message}`");
            }
        }
    }
}
