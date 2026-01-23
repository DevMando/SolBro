using System.Text;
using DiscordOllamaBot.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OllamaSharp.Models.Chat;

namespace DiscordOllamaBot.Services;

public class OllamaService
{
    private readonly OllamaApiClient _client;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(IConfiguration configuration, ILogger<OllamaService> logger)
    {
        var endpoint = configuration["Ollama:Endpoint"] ?? "http://localhost:11434";
        _model = configuration["Ollama:Model"] ?? "qwen3:4b";
        _systemPrompt = configuration["Ollama:SystemPrompt"] ?? "You are a helpful Discord assistant.";
        _logger = logger;

        _client = new OllamaApiClient(new Uri(endpoint));
    }

    public async Task<string> ChatAsync(IReadOnlyList<ConversationMessage> conversationHistory, string userMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var messages = new List<Message>
            {
                new(ChatRole.System, _systemPrompt)
            };

            // Add conversation history
            foreach (var msg in conversationHistory)
            {
                var role = msg.Role.ToLowerInvariant() switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    _ => ChatRole.User
                };
                messages.Add(new Message(role, msg.Content));
            }

            // Add the new user message
            messages.Add(new Message(ChatRole.User, userMessage));

            var request = new ChatRequest
            {
                Model = _model,
                Messages = messages,
                Stream = true
            };

            _logger.LogDebug("Sending chat request to Ollama with {MessageCount} messages", messages.Count);

            // Collect streaming response
            var responseBuilder = new StringBuilder();
            await foreach (var chunk in _client.ChatAsync(request, cancellationToken))
            {
                if (chunk?.Message?.Content != null)
                {
                    responseBuilder.Append(chunk.Message.Content);
                }
            }

            var response = responseBuilder.ToString();
            if (!string.IsNullOrEmpty(response))
            {
                return response;
            }

            _logger.LogWarning("Received empty response from Ollama");
            return "I couldn't generate a response.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to communicate with Ollama at configured endpoint");
            return "I'm having trouble connecting to my language model. Please make sure Ollama is running.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during Ollama chat");
            return "I encountered an unexpected error while processing your request.";
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var models = await _client.ListLocalModelsAsync(cancellationToken);
            return models.Any(m => m.Name.Contains(_model.Split(':')[0]));
        }
        catch
        {
            return false;
        }
    }
}
