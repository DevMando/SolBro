namespace DiscordOllamaBot.Models;

public class ConversationContext
{
    public ulong UserId { get; }
    public List<ConversationMessage> Messages { get; } = new();
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;

    public ConversationContext(ulong userId)
    {
        UserId = userId;
    }

    public void AddMessage(string role, string content)
    {
        Messages.Add(new ConversationMessage(role, content));
        LastActivity = DateTime.UtcNow;
    }

    public void TrimHistory(int maxMessages)
    {
        while (Messages.Count > maxMessages)
        {
            Messages.RemoveAt(0);
        }
    }

    public void Clear()
    {
        Messages.Clear();
        LastActivity = DateTime.UtcNow;
    }
}

public record ConversationMessage(string Role, string Content);
