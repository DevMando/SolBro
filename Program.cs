using Discord;
using Discord.WebSocket;
using DiscordOllamaBot.Handlers;
using DiscordOllamaBot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        // Discord client configuration with required intents
        var discordConfig = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.DirectMessages |
                             GatewayIntents.MessageContent,
            LogLevel = LogSeverity.Info
        };

        services.AddSingleton(discordConfig);
        services.AddSingleton<DiscordSocketClient>();

        // Services
        services.AddSingleton<OllamaService>();
        services.AddSingleton<ConversationService>();
        services.AddSingleton<MessageHandler>();
    })
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Information);
    })
    .Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var client = host.Services.GetRequiredService<DiscordSocketClient>();
var configuration = host.Services.GetRequiredService<IConfiguration>();
var ollamaService = host.Services.GetRequiredService<OllamaService>();

// Set up Discord logging
client.Log += logMessage =>
{
    var severity = logMessage.Severity switch
    {
        LogSeverity.Critical => LogLevel.Critical,
        LogSeverity.Error => LogLevel.Error,
        LogSeverity.Warning => LogLevel.Warning,
        LogSeverity.Info => LogLevel.Information,
        LogSeverity.Verbose => LogLevel.Debug,
        LogSeverity.Debug => LogLevel.Trace,
        _ => LogLevel.Information
    };
    logger.Log(severity, logMessage.Exception, "[Discord] {Message}", logMessage.Message);
    return Task.CompletedTask;
};

// Initialize message handler
var messageHandler = host.Services.GetRequiredService<MessageHandler>();
messageHandler.Initialize();

// Get bot token
var token = configuration["Discord:Token"];
if (string.IsNullOrWhiteSpace(token) || token == "YOUR_BOT_TOKEN")
{
    logger.LogError("Please set your Discord bot token in appsettings.json");
    return;
}

// Check Ollama availability
logger.LogInformation("Checking Ollama availability...");
if (!await ollamaService.IsAvailableAsync())
{
    logger.LogWarning("Ollama may not be available or the configured model is not installed. The bot will start but may not respond properly.");
}
else
{
    logger.LogInformation("Ollama is available with the configured model.");
}

// Connect to Discord
logger.LogInformation("Starting Discord bot...");
await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

// Wait for ready
var readyTcs = new TaskCompletionSource();
client.Ready += () =>
{
    logger.LogInformation("Bot is connected and ready! Logged in as {Username}", client.CurrentUser.Username);
    readyTcs.TrySetResult();
    return Task.CompletedTask;
};

await readyTcs.Task;

// Keep the bot running until shutdown
logger.LogInformation("Bot is running. Press Ctrl+C to exit.");
logger.LogInformation("- Mention the bot in channels to chat");
logger.LogInformation("- Send direct messages to chat without mentions");
await host.WaitForShutdownAsync();

logger.LogInformation("Shutting down...");
await client.StopAsync();
