using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types;
using Telegram.Bot;
using PollEditorBot.Extensions;
using PollEditorBot.Settings;
using PollEditorBot.Exceptions;
using PollEditorBot.Loggers;
using PollEditorBot.Commands;
using Telegram.Bot.Types.ReplyMarkups;

namespace PollEditorBot;

public class PollEditorBotLauncher
{
    readonly ILogger logger;
    readonly Logging logging;
    readonly ITelegramBotClient bot;
    readonly MessageSender messageSender;

    readonly Dictionary<long, MessageReceiver> messageReceivers = new();

    // All users who have ever started the bot — used for broadcast
    readonly HashSet<long> allUsers = new();

    string botName = string.Empty;

    public PollEditorBotLauncher(ILogger logger)
    {
        this.logger = logger;
        logging = new(logger);
        bot = TelegramSettings.CurrentBot();
        messageSender = new(bot);
    }

    public async Task StartReceivingAsync(CancellationTokenSource cts)
    {
        // Empty array = receive ALL update types (including CallbackQuery)
        ReceiverOptions receiverOptions = new ReceiverOptions() { AllowedUpdates = Array.Empty<UpdateType>() };

        bot.StartReceiving(
            updateHandler: (ITelegramBotClient bot, Update update, CancellationToken _) => HandleUpdateAsync(update, cts),
            pollingErrorHandler: (ITelegramBotClient bot, Exception exc, CancellationToken _) => HandlePollingErrorAsync(exc, cts),
            receiverOptions,
            cts.Token);

        User me = await bot.GetMeAsync();
        botName = me.FirstName;
        logger.LogInformationLine(botName, $"\"{botName}\" started listening ...");
    }

    // ─── Update router ────────────────────────────────────────────────────────

    async Task HandleUpdateAsync(Update update, CancellationTokenSource cts)
    {
        if (update.Message is { } message)
        {
            Chat chat = message.Chat;
            int replyToMessageId = message.MessageId;
            long chatId = chat.Id;

            if (chat.Type == ChatType.Private)
            {
                // Track every user who writes to the bot
                allUsers.Add(chatId);

                string senderStr = GetSenderStr(chat);

                if (message.Text is { } messageText)
                    await HandleTextMessageAsync(senderStr, messageText, message.Entities, chatId, replyToMessageId, cts);
                else if (message.Poll is { } messagePoll)
                    await HandlePollMessageAsync(senderStr, messagePoll, chatId, replyToMessageId, cts);
                else
                    await HandleAnotherMessageTypeAsync(chatId, replyToMessageId, cts);
            }
            else
            {
                await LogWarningMessage(TelegramException.OnlyPrivateChatsSupported, chatId, replyToMessageId, null, cts);
            }
        }
        else if (update.CallbackQuery is { } callbackQuery)
        {
            await HandleCallbackQueryAsync(callbackQuery, cts);
        }
    }

    // ─── Force join helpers ───────────────────────────────────────────────────

    /// <summary>Returns true if force-join is not configured OR user is already a member.</summary>
    async Task<bool> IsUserAllowedAsync(long userId)
    {
        string channel = TelegramSettings.ForceJoinChannel;
        if (string.IsNullOrEmpty(channel)) return true;

        try
        {
            ChatMember member = await bot.GetChatMemberAsync(channel, userId);
            return member.Status is ChatMemberStatus.Member
                or ChatMemberStatus.Administrator
                or ChatMemberStatus.Creator;
        }
        catch
        {
            // If we can't check (e.g. bot not in channel), don't block the user
            return true;
        }
    }

    async Task SendForceJoinPromptAsync(long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        string channel = TelegramSettings.ForceJoinChannel.TrimStart('@');
        string joinUrl = $"https://t.me/{channel}";

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📢 Join Channel / Group", joinUrl) },
            new[] { InlineKeyboardButton.WithCallbackData("✅ I've Joined", "check_join") }
        });

        await messageSender.SendTextMessageAsync(
            "⚠️ <b>Join Required</b>\n\n" +
            "You must join our channel/group before using this bot.\n\n" +
            "1️⃣ Click <b>Join Channel / Group</b> below\n" +
            "2️⃣ Then press <b>✅ I've Joined</b>",
            chatId, replyToMessageId, keyboard, cts);
    }

    // ─── Callback query (inline button presses) ───────────────────────────────

    async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationTokenSource cts)
    {
        long chatId = callbackQuery.Message!.Chat.Id;
        long userId = callbackQuery.From.Id;
        int messageId = callbackQuery.Message.MessageId;

        try
        {
            if (callbackQuery.Data == "check_join")
            {
                if (await IsUserAllowedAsync(userId))
                {
                    // Dismiss loading spinner — no popup text needed
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cts.Token);

                    // Remove the force-join prompt message
                    try { await bot.DeleteMessageAsync(chatId, messageId, cts.Token); } catch { }

                    // Track user and show the welcome message
                    allUsers.Add(chatId);
                    await HandleTextMessageAsync(
                        callbackQuery.From.FirstName ?? "User",
                        CommandsStr.Start,
                        null,
                        chatId,
                        0,
                        cts);
                }
                else
                {
                    // Show popup alert — user still hasn't joined
                    await bot.AnswerCallbackQueryAsync(
                        callbackQuery.Id,
                        "❌ You haven't joined yet! Please join first, then try again.",
                        showAlert: true,
                        cancellationToken: cts.Token);
                }
            }
        }
        catch (Exception exc)
        {
            logger.LogCriticalLine(exc.Message);
        }
    }

    // ─── Text message handler ─────────────────────────────────────────────────

    async Task HandleTextMessageAsync(string sender, string messageText, IEnumerable<MessageEntity>? messageEntities, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        try
        {
            logging.LogCommandStrMessage(sender, messageText);

            // ── Broadcast (owner only) ────────────────────────────────────────
            if (messageText.StartsWith(CommandsStr.Broadcast))
            {
                await HandleBroadcastAsync(messageText, chatId, replyToMessageId, cts);
                return;
            }

            // ── Force join check (only on /start) ────────────────────────────
            if (messageText.Trim() == CommandsStr.Start
                && !string.IsNullOrEmpty(TelegramSettings.ForceJoinChannel)
                && !await IsUserAllowedAsync(chatId))
            {
                await SendForceJoinPromptAsync(chatId, replyToMessageId, cts);
                return;
            }

            // ── Normal command flow ───────────────────────────────────────────
            if (!messageReceivers.ContainsKey(chatId))
                messageReceivers.Add(chatId, new());

            MessageReceiver currentMessageReceiver = messageReceivers.GetValueOrDefault(chatId)!;

            if (currentMessageReceiver.BotCommand is not null)
            {
                if (!IsMessageEntitiesTypeSupported(messageEntities))
                {
                    await LogWarningMessage(TelegramException.MessageEntityTypeNotSupported, chatId, replyToMessageId, null, cts);
                    return;
                }
                currentMessageReceiver.BotCommand.MessageEntities = messageEntities;
            }

            await currentMessageReceiver.Execute(messageText);

            BaseBotCommand botCommand = currentMessageReceiver.BotCommand!;
            bool isFinished = botCommand.IsFinished ?? false;
            IReplyMarkup? replyMarkup = botCommand.ReplyMarkup;

            if (isFinished && !botCommand.IsStrResponse)
            {
                var poll = botCommand.Poll!;
                await logging.LogPollMessageAsync(poll);
                await messageSender.SendPollMessageAsync(poll, chatId, replyToMessageId, replyMarkup, cts);
            }
            else if (botCommand.MessageStr is { } messageStr)
            {
                await logging.LogStrMessage(messageStr);
                await messageSender.SendTextMessageAsync(messageStr, chatId, replyToMessageId, replyMarkup, cts);
            }
        }
        catch (PollEditorException botExc)
        {
            string messageExc = botExc.Message;
            logger.LogWarningLine(messageExc);
            await messageSender.SendTextMessageAsync(messageExc, chatId, replyToMessageId, null, cts);
        }
        catch (ApiRequestException ex)
        {
            int delay = ex.Parameters?.RetryAfter ?? 0;
            if (delay > 0)
            {
                string messageExc = TelegramException.TooManyRequests(delay);
                logger.LogWarningLine(messageExc);
                await messageSender.SendTextMessageAsync(messageExc, chatId, replyToMessageId, null, cts);
                Task.Delay(delay * 1000).Wait();
            }
        }
        catch (Exception exc)
        {
            logger.LogCriticalLine(exc.Message);
        }
    }

    // ─── Broadcast ────────────────────────────────────────────────────────────

    async Task HandleBroadcastAsync(string messageText, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        long ownerId = TelegramSettings.OwnerId;

        // Only the owner can broadcast
        if (ownerId == 0 || chatId != ownerId)
        {
            await messageSender.SendTextMessageAsync(
                "⛔ You are not authorized to use this command.",
                chatId, replyToMessageId, null, cts);
            return;
        }

        // Extract message after "/broadcast "
        string broadcastText = messageText.Length > CommandsStr.Broadcast.Length
            ? messageText[(CommandsStr.Broadcast.Length)..].Trim()
            : "";

        if (string.IsNullOrEmpty(broadcastText))
        {
            await messageSender.SendTextMessageAsync(
                "ℹ️ Usage: <code>/broadcast Your message here</code>",
                chatId, replyToMessageId, null, cts);
            return;
        }

        await messageSender.SendTextMessageAsync(
            $"📤 Broadcasting to {allUsers.Count} users...",
            chatId, replyToMessageId, null, cts);

        int sent = 0, failed = 0;
        foreach (long uid in allUsers.ToList())
        {
            try
            {
                await bot.SendTextMessageAsync(
                    uid, broadcastText,
                    parseMode: ParseMode.Html,
                    cancellationToken: cts.Token);
                sent++;
            }
            catch { failed++; }
        }

        await messageSender.SendTextMessageAsync(
            $"✅ <b>Broadcast complete!</b>\n\n📤 Sent: <b>{sent}</b>\n❌ Failed: <b>{failed}</b>",
            chatId, replyToMessageId, null, cts);
    }

    // ─── Misc handlers ────────────────────────────────────────────────────────

    bool IsMessageEntitiesTypeSupported(IEnumerable<MessageEntity>? messageEntities)
        => messageEntities?.All(msgEntity => Enum.IsDefined(msgEntity.Type)) ?? true;

    async Task HandlePollMessageAsync(string sender, Poll pollMessage, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        if (PollHelper.IfQuizSentCorrectly(pollMessage))
        {
            MessageReceiver newMessageReceiver = new(pollMessage);

            if (!messageReceivers.ContainsKey(chatId))
                messageReceivers.Add(chatId, newMessageReceiver);
            else
                messageReceivers[chatId] = newMessageReceiver;

            await messageSender.SendTextMessageAsync(
                "Now, please send one of the available commands.",
                chatId, replyToMessageId, new ReplyKeyboardRemove(), cts);
            await logging.LogPollMessageAsync(pollMessage);
        }
        else
        {
            await LogWarningMessage(TelegramException.QuizSentIncorrectly, chatId, replyToMessageId, null, cts);
        }
    }

    async Task HandleAnotherMessageTypeAsync(long chatId, int replyToMessageId, CancellationTokenSource cts)
        => await LogWarningMessage(TelegramException.MessageTypeNotSuitable, chatId, replyToMessageId, null, cts);

    async Task LogWarningMessage(string warningStr, long chatId, int replyToMessageId, IReplyMarkup? replyMarkup, CancellationTokenSource cts)
    {
        logger.LogWarningLine(warningStr);
        await messageSender.SendTextMessageAsync(warningStr, chatId, replyToMessageId, null, cts);
    }

    Task HandlePollingErrorAsync(Exception exc, CancellationTokenSource cts)
    {
        if (exc is ApiRequestException ex)
        {
            int delay = ex.Parameters?.RetryAfter ?? 0;
            if (delay > 0)
            {
                logger.LogErrorLine(TelegramException.TooManyRequests(delay));
                Task.Delay(delay * 1000).Wait();
                return Task.CompletedTask;
            }
        }

        logger.LogError(exc.Message);
        return Task.CompletedTask;
    }

    async Task StopBotAsync(CancellationTokenSource cts)
    {
        User me = await bot.GetMeAsync();
        string fName = me.FirstName;
        logger.LogDebugLine($"\"{fName}\" finished listening ...");
        cts.Cancel();
    }

    string GetSenderStr(Chat chat)
    {
        string senderStr = chat.FirstName ?? "";
        if (chat.Username is string userName)
            senderStr += $" (@{userName})";
        return senderStr;
    }
}
