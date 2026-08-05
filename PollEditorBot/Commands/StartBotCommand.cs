using PollEditorBot.Settings;
using Telegram.Bot.Types.ReplyMarkups;

namespace PollEditorBot.Commands;

public class StartBotCommand : BaseBotCommand
{
    public override void Execute(string? commandStr)
    {
        MessageStr =
            "👋 <b>Welcome to Poll Editor Bot!</b>\n\n" +
            "I can help you <b>create and edit</b> Telegram polls with ease.\n\n" +
            "📋 <b>What you can do:</b>\n" +
            "• Send me a poll to start editing\n" +
            "• Change question, options, poll type & more\n" +
            "• Create new polls from scratch\n\n" +
            "📌 Use /help to see all available commands.\n\n" +
            "⚡ Just send me any poll and I'll get started!";

        string ownerUsername = TelegramSettings.OwnerUsername;
        if (!string.IsNullOrEmpty(ownerUsername))
        {
            ReplyMarkup = new InlineKeyboardMarkup(
                InlineKeyboardButton.WithUrl("👤 Contact Owner", $"https://t.me/{ownerUsername}")
            );
        }

        IsStrResponse = true;
        IsFinished = true;
    }
}
