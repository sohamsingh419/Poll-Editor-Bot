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
    readonly HashSet<long> allUsers = new();

    // Per-user poll queue — for bulk replace
    readonly Dictionary<long, List<Poll>> pollQueues = new();

    // Interactive replace session: chatId → the old text user selected
    readonly Dictionary<long, string> awaitingNewName = new();

    // Candidate texts shown as buttons: chatId → list of unique texts
    readonly Dictionary<long, List<string>> replaceCandidates = new();

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
        ReceiverOptions receiverOptions = new() { AllowedUpdates = Array.Empty<UpdateType>() };
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

    // ─── Force join ───────────────────────────────────────────────────────────

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
        catch { return true; }
    }

    async Task SendForceJoinPromptAsync(long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        string channel = TelegramSettings.ForceJoinChannel.TrimStart('@');
        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithUrl("📢 Join Channel / Group", $"https://t.me/{channel}") },
            new[] { InlineKeyboardButton.WithCallbackData("✅ I've Joined", "check_join") }
        });
        await messageSender.SendTextMessageAsync(
            "⚠️ <b>Join Required</b>\n\nBot use karne ke liye pehle channel/group join karo.\n\n" +
            "1️⃣ <b>Join Channel / Group</b> dabao\n" +
            "2️⃣ Phir <b>✅ I've Joined</b> dabao",
            chatId, replyToMessageId, keyboard, cts);
    }

    // ─── Callback query handler ───────────────────────────────────────────────

    async Task HandleCallbackQueryAsync(CallbackQuery callbackQuery, CancellationTokenSource cts)
    {
        long chatId = callbackQuery.Message!.Chat.Id;
        long userId = callbackQuery.From.Id;
        int messageId = callbackQuery.Message.MessageId;
        string data = callbackQuery.Data ?? "";

        try
        {
            // ── Force join verification ───────────────────────────────────────
            if (data == "check_join")
            {
                if (await IsUserAllowedAsync(userId))
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cts.Token);
                    try { await bot.DeleteMessageAsync(chatId, messageId, cts.Token); } catch { }
                    allUsers.Add(chatId);
                    await HandleTextMessageAsync(callbackQuery.From.FirstName ?? "User", CommandsStr.Start, null, chatId, 0, cts);
                }
                else
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id,
                        "❌ Abhi join nahi kiya! Pehle join karo, phir dobara try karo.",
                        showAlert: true, cancellationToken: cts.Token);
                }
                return;
            }

            // ── Show replace picker (which text is the name?) ─────────────────
            if (data == "start_replace")
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cts.Token);

                if (!pollQueues.ContainsKey(chatId) || pollQueues[chatId].Count == 0)
                {
                    await bot.SendTextMessageAsync(chatId, "⚠️ Queue mein koi poll nahi. Pehle polls bhejo.",
                        cancellationToken: cts.Token);
                    return;
                }

                // Collect ALL unique texts (question + options) across all queued polls
                var queue = pollQueues[chatId];
                var candidates = queue
                    .SelectMany(p => p.Options.Select(o => o.Text ?? "")
                        .Prepend(p.Question ?? ""))
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(18)   // Telegram limits inline keyboard rows
                    .ToList();

                replaceCandidates[chatId] = candidates;

                // Build one button per candidate — display truncated, index in callback data
                var rows = candidates.Select((text, i) =>
                    new[] { InlineKeyboardButton.WithCallbackData(
                        text.Length > 35 ? text[..35] + "…" : text,
                        $"rp:{i}") }
                ).ToList();
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("❌ Cancel", "rp_cancel") });

                await bot.SendTextMessageAsync(chatId,
                    $"👇 <b>Queue mein {queue.Count} poll(s) hain.</b>\n\n" +
                    "Kaunsa text <b>naam</b> hai jo replace karna hai?\n" +
                    "<i>Neeche se select karo:</i>",
                    parseMode: ParseMode.Html,
                    replyMarkup: new InlineKeyboardMarkup(rows),
                    cancellationToken: cts.Token);
                return;
            }

            // ── Clear poll queue ──────────────────────────────────────────────
            if (data == "clear_queue")
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cts.Token);
                if (pollQueues.ContainsKey(chatId)) pollQueues[chatId].Clear();
                awaitingNewName.Remove(chatId);
                await bot.SendTextMessageAsync(chatId, "🗑 Queue clear ho gaya!",
                    cancellationToken: cts.Token);
                return;
            }

            // ── Cancel replace ────────────────────────────────────────────────
            if (data == "rp_cancel")
            {
                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Cancelled", cancellationToken: cts.Token);
                awaitingNewName.Remove(chatId);
                return;
            }

            // ── User picked which text is the name ────────────────────────────
            if (data.StartsWith("rp:"))
            {
                if (!replaceCandidates.ContainsKey(chatId))
                {
                    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, "Session expire ho gaya. Dobara try karo.", showAlert: true, cancellationToken: cts.Token);
                    return;
                }

                int idx = int.Parse(data[3..]);
                string selectedText = replaceCandidates[chatId][idx];
                awaitingNewName[chatId] = selectedText;

                await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cts.Token);

                // Delete the picker message
                try { await bot.DeleteMessageAsync(chatId, messageId, cts.Token); } catch { }

                await bot.SendTextMessageAsync(chatId,
                    $"✅ Selected: <code>{selectedText}</code>\n\n" +
                    "Ab <b>naya naam</b> type karke bhejo:",
                    parseMode: ParseMode.Html,
                    cancellationToken: cts.Token);
                return;
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

            // ── Interactive replace: waiting for new name ─────────────────────
            if (awaitingNewName.ContainsKey(chatId) && !messageText.StartsWith("/"))
            {
                string oldText = awaitingNewName[chatId];
                string newText = messageText.Trim();
                awaitingNewName.Remove(chatId);
                await ApplyBulkReplaceAsync(chatId, replyToMessageId, new[] { oldText }, newText, cts);
                return;
            }

            // ── Broadcast (owner only) ────────────────────────────────────────
            if (messageText.StartsWith(CommandsStr.Broadcast))
            {
                await HandleBroadcastAsync(messageText, chatId, replyToMessageId, cts);
                return;
            }

            // ── Power-user: /replace old | new (still supported) ─────────────
            if (messageText.StartsWith(CommandsStr.Replace))
            {
                await HandlePowerReplaceAsync(messageText, chatId, replyToMessageId, cts);
                return;
            }

            // ── /my_polls / /clear_polls ──────────────────────────────────────
            if (messageText.Trim() == CommandsStr.MyPolls)
            {
                await HandleMyPollsAsync(chatId, replyToMessageId, cts);
                return;
            }
            if (messageText.Trim() == CommandsStr.ClearPolls)
            {
                if (pollQueues.ContainsKey(chatId)) pollQueues[chatId].Clear();
                awaitingNewName.Remove(chatId);
                await messageSender.SendTextMessageAsync("🗑 Queue clear ho gaya!", chatId, replyToMessageId, null, cts);
                return;
            }

            // ── Force join check on /start ────────────────────────────────────
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

            if (isFinished && botCommand.BulkPolls.Count > 0)
            {
                if (botCommand.MessageStr is { } confirmMsg)
                {
                    await logging.LogStrMessage(confirmMsg);
                    await messageSender.SendTextMessageAsync(confirmMsg, chatId, replyToMessageId, null, cts);
                }
                foreach (var bulkPoll in botCommand.BulkPolls)
                {
                    await logging.LogPollMessageAsync(bulkPoll);
                    await messageSender.SendPollMessageAsync(bulkPoll, chatId, 0, new ReplyKeyboardRemove(), cts);
                }
            }
            else if (isFinished && !botCommand.IsStrResponse)
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

    // ─── Poll received ────────────────────────────────────────────────────────

    async Task HandlePollMessageAsync(string sender, Poll pollMessage, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        if (PollHelper.IfQuizSentCorrectly(pollMessage))
        {
            // Set as current poll for single-edit commands
            MessageReceiver newMessageReceiver = new(pollMessage);
            if (!messageReceivers.ContainsKey(chatId))
                messageReceivers.Add(chatId, newMessageReceiver);
            else
                messageReceivers[chatId] = newMessageReceiver;

            // Add to queue for bulk replace
            if (!pollQueues.ContainsKey(chatId))
                pollQueues[chatId] = new List<Poll>();
            pollQueues[chatId].Add(pollMessage);

            int queueCount = pollQueues[chatId].Count;
            await logging.LogPollMessageAsync(pollMessage);

            // Show action buttons
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData($"🔄 Naam Replace Karo ({queueCount} poll)", "start_replace") },
                new[] { InlineKeyboardButton.WithCallbackData("🗑 Queue Clear Karo", "clear_queue") }
            });

            await messageSender.SendTextMessageAsync(
                $"✅ Poll #{queueCount} queue mein add ho gaya!\n\n" +
                "<i>Aur polls bhej sakte ho ya neeche button dabao.</i>",
                chatId, replyToMessageId, keyboard, cts);
        }
        else
        {
            await LogWarningMessage(TelegramException.QuizSentIncorrectly, chatId, replyToMessageId, null, cts);
        }
    }

    // ─── Bulk replace logic ───────────────────────────────────────────────────

    async Task ApplyBulkReplaceAsync(long chatId, int replyToMessageId, string[] oldValues, string newText, CancellationTokenSource cts)
    {
        if (!pollQueues.ContainsKey(chatId) || pollQueues[chatId].Count == 0)
        {
            await messageSender.SendTextMessageAsync(
                "⚠️ Queue mein koi poll nahi. Pehle polls bhejo.",
                chatId, replyToMessageId, null, cts);
            return;
        }

        var queue = pollQueues[chatId];
        var updatedPolls = queue.Select(p => ApplyReplace(p, oldValues, newText)).ToList();
        pollQueues[chatId] = updatedPolls;

        string oldDisplay = string.Join(", ", oldValues.Select(v => $"<code>{v}</code>"));
        await messageSender.SendTextMessageAsync(
            $"✅ <b>Replace ho gaya!</b>\n{oldDisplay} → <b>{newText}</b>\n\n" +
            $"📤 {updatedPolls.Count} polls aa rahe hain 👇",
            chatId, replyToMessageId, null, cts);

        foreach (var poll in updatedPolls)
        {
            await logging.LogPollMessageAsync(poll);
            await messageSender.SendPollMessageAsync(poll, chatId, 0, new ReplyKeyboardRemove(), cts);
        }
    }

    static Poll ApplyReplace(Poll poll, string[] oldValues, string newText)
    {
        string question = poll.Question ?? "";
        var options = poll.Options?.Select(o => o.Text ?? "").ToArray() ?? Array.Empty<string>();

        foreach (string old in oldValues)
        {
            question = question.Replace(old, newText, StringComparison.OrdinalIgnoreCase);
            options = options.Select(o => o.Replace(old, newText, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return new Poll
        {
            Question = question,
            Options = options.Select(o => new PollOption { Text = o }).ToArray(),
            Type = poll.Type,
            IsAnonymous = poll.IsAnonymous,
            AllowsMultipleAnswers = poll.AllowsMultipleAnswers,
            CorrectOptionId = poll.CorrectOptionId,
            Explanation = poll.Explanation,
            ExplanationEntities = poll.ExplanationEntities,
            OpenPeriod = poll.OpenPeriod,
            CloseDate = poll.CloseDate,
            IsClosed = poll.IsClosed,
        };
    }

    // ─── Power-user: /replace old | new ──────────────────────────────────────

    async Task HandlePowerReplaceAsync(string messageText, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        string args = messageText.Length > CommandsStr.Replace.Length
            ? messageText[(CommandsStr.Replace.Length)..].Trim()
            : "";

        if (!args.Contains('|'))
        {
            await messageSender.SendTextMessageAsync(
                "ℹ️ <b>Usage:</b> <code>/replace purana | naya</code>\n" +
                "Multiple: <code>/replace A,B | naya</code>",
                chatId, replyToMessageId, null, cts);
            return;
        }

        int sep = args.IndexOf('|');
        string[] oldValues = args[..sep].Trim()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string newText = args[(sep + 1)..].Trim();

        if (oldValues.Length == 0 || string.IsNullOrEmpty(newText))
        {
            await messageSender.SendTextMessageAsync(
                "⚠️ Purana aur naya dono likhna zaroori hai.",
                chatId, replyToMessageId, null, cts);
            return;
        }

        await ApplyBulkReplaceAsync(chatId, replyToMessageId, oldValues, newText, cts);
    }

    async Task HandleMyPollsAsync(long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        if (!pollQueues.ContainsKey(chatId) || pollQueues[chatId].Count == 0)
        {
            await messageSender.SendTextMessageAsync(
                "📭 Queue khali hai. Polls bhejo to yahan dikhenge.",
                chatId, replyToMessageId, null, cts);
            return;
        }
        var queue = pollQueues[chatId];
        string list = string.Join("\n", queue.Select((p, i) => $"{i + 1}. {p.Question}"));
        await messageSender.SendTextMessageAsync(
            $"📋 <b>Queue mein {queue.Count} polls:</b>\n\n{list}",
            chatId, replyToMessageId, null, cts);
    }

    // ─── Broadcast (owner only) ───────────────────────────────────────────────

    async Task HandleBroadcastAsync(string messageText, long chatId, int replyToMessageId, CancellationTokenSource cts)
    {
        long ownerId = TelegramSettings.OwnerId;
        if (ownerId == 0 || chatId != ownerId)
        {
            await messageSender.SendTextMessageAsync("⛔ Aap authorized nahi hain.", chatId, replyToMessageId, null, cts);
            return;
        }

        string broadcastText = messageText.Length > CommandsStr.Broadcast.Length
            ? messageText[(CommandsStr.Broadcast.Length)..].Trim() : "";

        if (string.IsNullOrEmpty(broadcastText))
        {
            await messageSender.SendTextMessageAsync("ℹ️ Usage: <code>/broadcast message yahan</code>", chatId, replyToMessageId, null, cts);
            return;
        }

        await messageSender.SendTextMessageAsync($"📤 {allUsers.Count} users ko bhej raha hun...", chatId, replyToMessageId, null, cts);

        int sent = 0, failed = 0;
        foreach (long uid in allUsers.ToList())
        {
            try
            {
                await bot.SendTextMessageAsync(uid, broadcastText, parseMode: ParseMode.Html, cancellationToken: cts.Token);
                sent++;
            }
            catch { failed++; }
        }

        await messageSender.SendTextMessageAsync(
            $"✅ <b>Broadcast complete!</b>\n\n📤 Sent: <b>{sent}</b>\n❌ Failed: <b>{failed}</b>",
            chatId, replyToMessageId, null, cts);
    }

    // ─── Misc ─────────────────────────────────────────────────────────────────

    bool IsMessageEntitiesTypeSupported(IEnumerable<MessageEntity>? messageEntities)
        => messageEntities?.All(msgEntity => Enum.IsDefined(msgEntity.Type)) ?? true;

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

    string GetSenderStr(Chat chat)
    {
        string s = chat.FirstName ?? "";
        if (chat.Username is string u) s += $" (@{u})";
        return s;
    }
}
