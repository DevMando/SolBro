using System.Collections.Concurrent;
using DiscordOllamaBot.Models;
using Microsoft.Extensions.Configuration;

namespace DiscordOllamaBot.Services;

public class ConversationService
{
    private readonly ConcurrentDictionary<ulong, ConversationContext> _conversations = new();
    private readonly int _maxHistory;

    public ConversationService(IConfiguration configuration)
    {
        _maxHistory = configuration.GetValue("Conversation:MaxHistory", 20);
    }

    public ConversationContext GetOrCreateContext(ulong userId)
    {
        return _conversations.GetOrAdd(userId, id => new ConversationContext(id));
    }

    public IReadOnlyList<ConversationMessage> GetHistory(ulong userId)
    {
        if (_conversations.TryGetValue(userId, out var context))
        {
            return context.Messages.AsReadOnly();
        }
        return Array.Empty<ConversationMessage>();
    }

    public void AddMessage(ulong userId, string role, string content)
    {
        var context = GetOrCreateContext(userId);

        lock (context.Messages)
        {
            context.AddMessage(role, content);
            context.TrimHistory(_maxHistory * 2); // Keep pairs of user/assistant messages
        }
    }

    public void ClearHistory(ulong userId)
    {
        if (_conversations.TryGetValue(userId, out var context))
        {
            context.Clear();
        }
    }
}
