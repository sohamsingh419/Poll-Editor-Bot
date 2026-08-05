using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

using PollEditorBot;
using PollEditorBot.Loggers;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Poll Editor Bot is running!");

_ = app.RunAsync();

ILogger logger = new ConsoleLogger();
PollEditorBotLauncher bot = new(logger);

CancellationTokenSource cts = new();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

await bot.StartReceivingAsync(cts);

logger.LogInformationLine("Bot is running. Press Ctrl+C to stop.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
}
