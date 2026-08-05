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
PollEditorBotLauncher bot = new(logger);

CancellationTokenSource cts = new();

Console.CancelKeyPress += (, e) =>
{
e.Cancel = true;
cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (, _) => cts.Cancel();

await bot.StartReceivingAsync(cts);

logger.LogInformationLine("Bot is running. Press Ctrl+C to stop.");

try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }
