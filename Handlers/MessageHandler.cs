using Discord;
using Discord.WebSocket;
using DiscordOllamaBot.Services;
using Microsoft.Extensions.Logging;

namespace DiscordOllamaBot.Handlers;

public class MessageHandler
{
    private readonly DiscordSocketClient _client;
    private readonly OllamaService _ollamaService;
    private readonly ConversationService _conversationService;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(
        DiscordSocketClient client,
        OllamaService ollamaService,
        ConversationService conversationService,
        ILogger<MessageHandler> logger)
    {
        _client = client;
        _ollamaService = ollamaService;
        _conversationService = conversationService;
        _logger = logger;
    }

    public void Initialize()
    {
        _client.MessageReceived += HandleMessageAsync;
    }

    private async Task HandleMessageAsync(SocketMessage message)
    {
        // Ignore system messages and bot messages
        if (message is not SocketUserMessage userMessage || message.Author.IsBot)
            return;

        var isDm = message.Channel is IDMChannel;
        var isMentioned = userMessage.MentionedUsers.Any(u => u.Id == _client.CurrentUser.Id);

        // Respond to DMs (always) or channel messages with @mention
        if (!isDm && !isMentioned)
            return;

        var userId = message.Author.Id;

        // Strip the bot mention from the message content
        var content = userMessage.Content;
        if (isMentioned)
        {
            foreach (var mention in userMessage.MentionedUsers.Where(u => u.Id == _client.CurrentUser.Id))
            {
                content = content.Replace($"<@{mention.Id}>", "").Replace($"<@!{mention.Id}>", "");
            }
            content = content.Trim();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            await message.Channel.SendMessageAsync("Hello! How can I help you today?");
            return;
        }

        _logger.LogInformation("Received message from {User} ({UserId}) in {ChannelType}: {Content}",
            message.Author.Username, userId, isDm ? "DM" : "channel", content);

        // Show typing indicator while processing
        using var typing = message.Channel.EnterTypingState();

        try
        {
            // Get conversation history for this user and generate response
            var history = _conversationService.GetHistory(userId);
            var response = await _ollamaService.ChatAsync(history, content);

            // Store the conversation for this user
            _conversationService.AddMessage(userId, "user", content);
            _conversationService.AddMessage(userId, "assistant", response);

            // Send response, handling Discord's 2000 character limit
            await SendResponseAsync(message.Channel, response);

            _logger.LogInformation("Sent response to user {User} ({UserId})", message.Author.Username, userId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message from user {UserId}", userId);
            await message.Channel.SendMessageAsync("Sorry, I encountered an error while processing your message.");
        }
    }

    private async Task SendResponseAsync(ISocketMessageChannel channel, string response)
    {
        const int maxLength = 2000;

        if (response.Length <= maxLength)
        {
            await channel.SendMessageAsync(response);
            return;
        }

        // Split into chunks, trying to break at newlines or spaces
        var chunks = SplitMessage(response, maxLength);
        foreach (var chunk in chunks)
        {
            await channel.SendMessageAsync(chunk);
            await Task.Delay(100); // Small delay to avoid rate limiting
        }
    }

    private static IEnumerable<string> SplitMessage(string message, int maxLength)
    {
        if (message.Length <= maxLength)
        {
            yield return message;
            yield break;
        }

        var currentPosition = 0;
        while (currentPosition < message.Length)
        {
            var remaining = message.Length - currentPosition;
            if (remaining <= maxLength)
            {
                yield return message.Substring(currentPosition);
                yield break;
            }

            // Try to find a good break point (newline or space)
            var chunkEnd = currentPosition + maxLength;
            var breakPoint = message.LastIndexOf('\n', chunkEnd - 1, Math.Min(maxLength, chunkEnd - currentPosition));

            if (breakPoint <= currentPosition)
            {
                breakPoint = message.LastIndexOf(' ', chunkEnd - 1, Math.Min(maxLength, chunkEnd - currentPosition));
            }

            if (breakPoint <= currentPosition)
            {
                // No good break point found, just cut at max length
                breakPoint = chunkEnd;
            }

            yield return message.Substring(currentPosition, breakPoint - currentPosition);
            currentPosition = breakPoint;

            // Skip the break character if it's a space or newline
            if (currentPosition < message.Length && (message[currentPosition] == ' ' || message[currentPosition] == '\n'))
            {
                currentPosition++;
            }
        }
    }
}
