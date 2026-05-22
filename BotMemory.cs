using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace SolBro
{
    public record BufferedMessage(string Username, string Content, DateTime Timestamp, ulong GuildId);

    public class BotMemory
    {
        private const string DbFile = "bot_memory.db";
        private const int BufferMaxPerChannel = 50;
        private const int BurstThreshold = 15;
        private static readonly TimeSpan BurstWindow = TimeSpan.FromMinutes(5);
        private const int ProfileUpdateInterval = 20;
        private static readonly TimeSpan CultureUpdateInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan MemoryRetentionPeriod = TimeSpan.FromDays(30);

        private readonly Dictionary<ulong, List<BufferedMessage>> _channelBuffers = new();
        private readonly Dictionary<ulong, int> _userMessageCounts = new();
        private readonly object _bufferLock = new();
        private readonly string _connectionString;

        private DateTime _lastCultureUpdate = DateTime.MinValue;

        public BotMemory()
        {
            _connectionString = $"Data Source={DbFile}";
        }

        public async Task InitializeAsync()
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                CREATE TABLE IF NOT EXISTS UserProfiles (
                    UserId TEXT PRIMARY KEY,
                    Username TEXT NOT NULL,
                    Traits TEXT DEFAULT '[]',
                    Interests TEXT DEFAULT '[]',
                    LastSeen TEXT NOT NULL,
                    MessageCount INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS ConversationMemories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ChannelId TEXT NOT NULL,
                    Summary TEXT NOT NULL,
                    Participants TEXT DEFAULT '[]',
                    Timestamp TEXT NOT NULL,
                    MessageCount INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS ServerCulture (
                    GuildId TEXT PRIMARY KEY,
                    Traits TEXT DEFAULT '[]',
                    TopEmojis TEXT DEFAULT '[]',
                    ActiveHours TEXT DEFAULT '[]',
                    LastUpdated TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_conv_channel ON ConversationMemories(ChannelId);
                CREATE INDEX IF NOT EXISTS idx_conv_timestamp ON ConversationMemories(Timestamp);
            ";

            await using var cmd = new SqliteCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();

            Console.WriteLine("BotMemory: SQLite database initialized.");
        }

        public void BufferMessage(ulong channelId, string username, string content, ulong guildId)
        {
            lock (_bufferLock)
            {
                if (!_channelBuffers.ContainsKey(channelId))
                    _channelBuffers[channelId] = new List<BufferedMessage>();

                var buffer = _channelBuffers[channelId];
                buffer.Add(new BufferedMessage(username, content, DateTime.UtcNow, guildId));

                if (buffer.Count > BufferMaxPerChannel)
                    buffer.RemoveRange(0, buffer.Count - BufferMaxPerChannel);
            }
        }

        public bool IsBurstActive(ulong channelId)
        {
            lock (_bufferLock)
            {
                if (!_channelBuffers.TryGetValue(channelId, out var buffer))
                    return false;

                var cutoff = DateTime.UtcNow - BurstWindow;
                var recentCount = buffer.Count(m => m.Timestamp >= cutoff);
                return recentCount >= BurstThreshold;
            }
        }

        public async Task SummarizeAndStoreAsync(ulong channelId, IChatCompletionService chat, Kernel kernel)
        {
            List<BufferedMessage> messages;
            lock (_bufferLock)
            {
                if (!_channelBuffers.TryGetValue(channelId, out var buffer) || buffer.Count == 0)
                    return;
                messages = new List<BufferedMessage>(buffer);
            }

            try
            {
                var conversation = string.Join("\n", messages.Select(m => $"{m.Username}: {m.Content}"));
                var participants = messages.Select(m => m.Username).Distinct().ToList();

                var history = new ChatHistory();
                history.AddSystemMessage("You are a conversation summarizer. Summarize the following Discord conversation in 2-3 sentences. Focus on the main topics discussed and the overall vibe. Be concise.");
                history.AddUserMessage(conversation);

                var response = await chat.GetChatMessageContentAsync(history);
                var summary = response.Content ?? "No summary available.";

                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var sql = @"INSERT INTO ConversationMemories (ChannelId, Summary, Participants, Timestamp, MessageCount)
                           VALUES (@channelId, @summary, @participants, @timestamp, @messageCount)";

                await using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@channelId", channelId.ToString());
                cmd.Parameters.AddWithValue("@summary", summary);
                cmd.Parameters.AddWithValue("@participants", JsonSerializer.Serialize(participants));
                cmd.Parameters.AddWithValue("@timestamp", DateTime.UtcNow.ToString("o"));
                cmd.Parameters.AddWithValue("@messageCount", messages.Count);
                await cmd.ExecuteNonQueryAsync();

                Console.WriteLine($"BotMemory: Stored conversation summary for channel {channelId} ({messages.Count} messages)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotMemory: Failed to summarize conversation — {ex.Message}");
            }
        }

        public async Task UpdateUserProfileAsync(ulong userId, string username, string content, IChatCompletionService chat, Kernel kernel)
        {
            int count;
            lock (_bufferLock)
            {
                if (!_userMessageCounts.ContainsKey(userId))
                    _userMessageCounts[userId] = 0;
                _userMessageCounts[userId]++;
                count = _userMessageCounts[userId];
            }

            await IncrementUserMessageCount(userId, username);

            if (count % ProfileUpdateInterval != 0)
                return;

            try
            {
                var recentMessages = new List<string>();
                lock (_bufferLock)
                {
                    foreach (var buffer in _channelBuffers.Values)
                    {
                        recentMessages.AddRange(
                            buffer.Where(m => m.Username == username)
                                  .TakeLast(20)
                                  .Select(m => m.Content));
                    }
                }

                if (recentMessages.Count == 0) return;

                var existingProfile = await GetUserProfileRaw(userId);

                var history = new ChatHistory();
                history.AddSystemMessage(
                    "You are analyzing a Discord user's messages to build a personality profile. " +
                    "Extract key traits (personality descriptors like 'sarcastic', 'helpful', 'chill') and interests (topics they discuss like 'crypto', 'gaming', 'coding'). " +
                    "Return ONLY valid JSON in this exact format: {\"traits\": [\"trait1\", \"trait2\"], \"interests\": [\"interest1\", \"interest2\"]}. " +
                    "Keep each list to 5-8 items max. If there's an existing profile, merge and update — don't just replace.");

                var prompt = $"User: {username}\n\nRecent messages:\n{string.Join("\n", recentMessages)}";
                if (existingProfile != null)
                    prompt += $"\n\nExisting profile:\nTraits: {existingProfile.Value.traits}\nInterests: {existingProfile.Value.interests}";

                history.AddUserMessage(prompt);

                var response = await chat.GetChatMessageContentAsync(history);
                var responseText = response.Content ?? "";

                var jsonText = responseText;
                var jsonStart = responseText.IndexOf('{');
                var jsonEnd = responseText.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    jsonText = responseText[jsonStart..(jsonEnd + 1)];

                using var doc = JsonDocument.Parse(jsonText);
                var traits = doc.RootElement.GetProperty("traits").EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
                var interests = doc.RootElement.GetProperty("interests").EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

                await UpsertUserProfile(userId, username, JsonSerializer.Serialize(traits), JsonSerializer.Serialize(interests));
                Console.WriteLine($"BotMemory: Updated profile for {username} — {traits.Count} traits, {interests.Count} interests");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotMemory: Failed to update profile for {username} — {ex.Message}");
            }
        }

        public async Task UpdateServerCultureAsync(ulong guildId, IChatCompletionService chat, Kernel kernel)
        {
            if (DateTime.UtcNow - _lastCultureUpdate < CultureUpdateInterval)
                return;

            _lastCultureUpdate = DateTime.UtcNow;

            try
            {
                var recentMessages = new List<string>();
                lock (_bufferLock)
                {
                    foreach (var buffer in _channelBuffers.Values)
                    {
                        recentMessages.AddRange(
                            buffer.Where(m => m.GuildId == guildId)
                                  .TakeLast(30)
                                  .Select(m => $"{m.Username}: {m.Content}"));
                    }
                }

                if (recentMessages.Count < 5) return;

                var history = new ChatHistory();
                history.AddSystemMessage(
                    "You are analyzing a Discord server's recent conversations to understand its culture and vibe. " +
                    "Return ONLY valid JSON: {\"traits\": [\"trait1\", \"trait2\"], \"topEmojis\": [\"emoji1\"], \"activeHours\": \"description\"}. " +
                    "Traits should describe the community vibe (e.g., 'chill gaming community', 'heavy on crypto talk', 'meme-heavy'). Keep traits to 3-5 items.");

                history.AddUserMessage($"Recent server conversations:\n{string.Join("\n", recentMessages)}");

                var response = await chat.GetChatMessageContentAsync(history);
                var responseText = response.Content ?? "";

                var jsonText = responseText;
                var jsonStart = responseText.IndexOf('{');
                var jsonEnd = responseText.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    jsonText = responseText[jsonStart..(jsonEnd + 1)];

                using var doc = JsonDocument.Parse(jsonText);
                var traits = doc.RootElement.GetProperty("traits").EnumerateArray()
                    .Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

                string topEmojis = "[]";
                if (doc.RootElement.TryGetProperty("topEmojis", out var emojisEl))
                    topEmojis = emojisEl.ToString();

                string activeHours = "\"unknown\"";
                if (doc.RootElement.TryGetProperty("activeHours", out var hoursEl))
                    activeHours = hoursEl.ToString();

                await UpsertServerCulture(guildId, JsonSerializer.Serialize(traits), topEmojis, activeHours);
                Console.WriteLine($"BotMemory: Updated server culture for guild {guildId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotMemory: Failed to update server culture — {ex.Message}");
            }
        }

        public async Task<string> GetContextForResponse(ulong channelId, ulong userId, ulong guildId)
        {
            var parts = new List<string>();

            try
            {
                var profile = await GetUserProfileRaw(userId);
                if (profile != null)
                {
                    var traits = profile.Value.traits;
                    var interests = profile.Value.interests;
                    var username = profile.Value.username;
                    if (traits != "[]" || interests != "[]")
                        parts.Add($"User Profile ({username}): Interests: {FormatJsonArray(interests)}. Traits: {FormatJsonArray(traits)}.");
                }

                var memories = await GetRecentConversationMemories(channelId, 3);
                if (memories.Count > 0)
                {
                    var memoryLines = memories.Select(m =>
                    {
                        var age = DateTime.UtcNow - m.timestamp;
                        var timeAgo = age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago"
                                    : age.TotalHours < 24 ? $"{(int)age.TotalHours} hours ago"
                                    : $"{(int)age.TotalDays} days ago";
                        return $"- {timeAgo}: {m.summary}";
                    });
                    parts.Add($"Recent Activity:\n{string.Join("\n", memoryLines)}");
                }

                var culture = await GetServerCulture(guildId);
                if (culture != null)
                    parts.Add($"Server Vibe: {FormatJsonArray(culture)}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotMemory: Error building context — {ex.Message}");
            }

            if (parts.Count == 0)
                return string.Empty;

            return $"[Memory Context]\n{string.Join("\n", parts)}\n[End Memory Context]";
        }

        public async Task CleanupAsync()
        {
            try
            {
                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync();

                var cutoff = (DateTime.UtcNow - MemoryRetentionPeriod).ToString("o");

                var sql = "DELETE FROM ConversationMemories WHERE Timestamp < @cutoff";
                await using var cmd = new SqliteCommand(sql, connection);
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                var deleted = await cmd.ExecuteNonQueryAsync();

                if (deleted > 0)
                    Console.WriteLine($"BotMemory: Cleaned up {deleted} old conversation memories.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"BotMemory: Cleanup failed — {ex.Message}");
            }
        }

        private async Task IncrementUserMessageCount(ulong userId, string username)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO UserProfiles (UserId, Username, LastSeen, MessageCount)
                VALUES (@userId, @username, @lastSeen, 1)
                ON CONFLICT(UserId) DO UPDATE SET
                    Username = @username,
                    LastSeen = @lastSeen,
                    MessageCount = MessageCount + 1";

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@userId", userId.ToString());
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@lastSeen", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<(string username, string traits, string interests)?> GetUserProfileRaw(ulong userId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT Username, Traits, Interests FROM UserProfiles WHERE UserId = @userId";
            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@userId", userId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2)
                );
            }
            return null;
        }

        private async Task UpsertUserProfile(ulong userId, string username, string traits, string interests)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO UserProfiles (UserId, Username, Traits, Interests, LastSeen, MessageCount)
                VALUES (@userId, @username, @traits, @interests, @lastSeen, 0)
                ON CONFLICT(UserId) DO UPDATE SET
                    Username = @username,
                    Traits = @traits,
                    Interests = @interests,
                    LastSeen = @lastSeen";

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@userId", userId.ToString());
            cmd.Parameters.AddWithValue("@username", username);
            cmd.Parameters.AddWithValue("@traits", traits);
            cmd.Parameters.AddWithValue("@interests", interests);
            cmd.Parameters.AddWithValue("@lastSeen", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<List<(string summary, DateTime timestamp)>> GetRecentConversationMemories(ulong channelId, int limit)
        {
            var results = new List<(string summary, DateTime timestamp)>();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT Summary, Timestamp FROM ConversationMemories WHERE ChannelId = @channelId ORDER BY Timestamp DESC LIMIT @limit";
            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@channelId", channelId.ToString());
            cmd.Parameters.AddWithValue("@limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var summary = reader.GetString(0);
                var timestamp = DateTime.Parse(reader.GetString(1));
                results.Add((summary, timestamp));
            }

            return results;
        }

        private async Task<string?> GetServerCulture(ulong guildId)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = "SELECT Traits FROM ServerCulture WHERE GuildId = @guildId";
            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@guildId", guildId.ToString());

            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }

        private async Task UpsertServerCulture(ulong guildId, string traits, string topEmojis, string activeHours)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            var sql = @"
                INSERT INTO ServerCulture (GuildId, Traits, TopEmojis, ActiveHours, LastUpdated)
                VALUES (@guildId, @traits, @topEmojis, @activeHours, @lastUpdated)
                ON CONFLICT(GuildId) DO UPDATE SET
                    Traits = @traits,
                    TopEmojis = @topEmojis,
                    ActiveHours = @activeHours,
                    LastUpdated = @lastUpdated";

            await using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@guildId", guildId.ToString());
            cmd.Parameters.AddWithValue("@traits", traits);
            cmd.Parameters.AddWithValue("@topEmojis", topEmojis);
            cmd.Parameters.AddWithValue("@activeHours", activeHours);
            cmd.Parameters.AddWithValue("@lastUpdated", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }

        private static string FormatJsonArray(string json)
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<string>>(json);
                if (items == null || items.Count == 0) return "none";
                return string.Join(", ", items);
            }
            catch
            {
                return json.Trim('[', ']', '"');
            }
        }
    }
}
