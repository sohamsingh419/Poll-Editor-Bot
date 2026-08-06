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

    // Owner settings
    public static long OwnerId =>
        long.TryParse(Environment.GetEnvironmentVariable("OWNER_ID"), out var id) ? id : 0;

    // Owner Telegram username (without @) for the contact button in welcome message
    public static string OwnerUsername =>
        Environment.GetEnvironmentVariable("OWNER_USERNAME")?.TrimStart('@') ?? "";

    // Force join invite link or @username — used for the "Join" button URL.
    // For private groups, set this to the invite link (https://t.me/+xxx).
    // For public channels/groups, set to @username.
    public static string ForceJoinChannel =>
        Environment.GetEnvironmentVariable("FORCE_JOIN_CHANNEL") ?? "";

    // Numeric chat ID of the force-join group/channel (e.g. -1001234567890).
    // Required when FORCE_JOIN_CHANNEL is a private invite link, because
    // Telegram's API cannot check membership via an invite link — only via chat ID or @username.
    // Leave empty if FORCE_JOIN_CHANNEL is a public @username (it doubles as the chat ID).
    public static string ForceJoinChatId =>
        Environment.GetEnvironmentVariable("FORCE_JOIN_CHAT_ID") ?? "";

    // Telegram poll limits
    public const int MinPollCountOfOptions = 2;
    public const int MaxPollCountOfOptions = 10;
    public const int MinPollOptionLength = 1;
    public const int MaxPollOptionLength = 100;
    public const int MinPollOpenPeriodInSeconds = 5;
    public const int MaxPollOpenPeriodInSeconds = 600;
    public const int MaxLengthOfMessage = 4096;
}
