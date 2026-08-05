using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Extensions;
using Telegram.Bot.Polling;
using PollEditorBot.Extensions;
using PollEditorBot;
using PollEditorBot.Loggers;

ILogger logger = new ConsoleLogger();

CancellationTokenSource cts = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

// Start keep-alive HTTP server (prevents Render free-tier from sleeping)
var keepAliveServer = new KeepAliveServer(logger);
_ = keepAliveServer.StartAsync(cts.Token);

// Start the Telegram bot
PollEditorBotLauncher bot = new(logger);
await bot.StartReceivingAsync(cts);

logger.LogInformationLine("Bot is running. Press Ctrl+C to stop.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }
