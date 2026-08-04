using Telegram.Bot;

namespace PollEditorBot.Settings;

public static class TelegramSettings
{
    static readonly string BotToken = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN")
        ?? throw new InvalidOperationException("TELEGRAM_BOT_TOKEN environment variable is not set.");

    static ITelegramBotClient? _bot;
    public static ITelegramBotClient CurrentBot()
    {
        _bot ??= new TelegramBotClient(BotToken);
        return _bot;
    }

    public static async Task<string?> GetCurrentBotName()
    {
        var me = await CurrentBot().GetMeAsync();
        return me.FirstName;
    }

    public const int MinPollCountOfOptions = 2;
    public const int MaxPollCountOfOptions = 10;
    public const int MinPollOptionLength = 1;
    public const int MaxPollOptionLength = 100;
    public const int MinPollOpenPeriodInSeconds = 5;
    public const int MaxPollOpenPeriodInSeconds = 600;
    public const int MaxLengthOfMessage = 4096;
}
