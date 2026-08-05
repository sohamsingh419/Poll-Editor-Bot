using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace PollEditorBot.Bulk;

public enum BulkEditState
{
    CollectingPolls,
    SettingOldName,
    SettingNewName,
    SettingExplanation,
    Done
}

/// <summary>
/// Manages a single user's bulk-edit session:
/// collect polls → replace name → set explanation → send all.
/// </summary>
public class BulkEditSession
{
    // ── Collected polls ────────────────────────────────────────────────────
    public List<Poll> Polls { get; } = new();

    // ── State machine ──────────────────────────────────────────────────────
    public BulkEditState State { get; private set; } = BulkEditState.CollectingPolls;

    // ── Edit values ────────────────────────────────────────────────────────
    public string? OldName       { get; private set; }
    public bool SkipNameReplace  { get; private set; }
    public string? Explanation   { get; private set; }

    // ── Helpers ────────────────────────────────────────────────────────────
    public bool HasQuizPolls =>
        Polls.Any(p => string.Equals(p.Type, PollType.Quiz.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(p.Type, "quiz", StringComparison.OrdinalIgnoreCase));

    public void AddPoll(Poll poll) => Polls.Add(poll);

    // ── Transitions ────────────────────────────────────────────────────────

    /// <summary>User sent /bulk_done — begin editing phase.</summary>
    public void StartEditing() => State = BulkEditState.SettingOldName;

    /// <summary>
    /// User provided old name (or null = skip name replacement).
    /// </summary>
    public void SetOldName(string? name)
    {
        if (name is null)
        {
            SkipNameReplace = true;
            State = HasQuizPolls ? BulkEditState.SettingExplanation : BulkEditState.Done;
        }
        else
        {
            OldName = name;
            State = BulkEditState.SettingNewName;
        }
    }

    /// <summary>
    /// User provided new name (or null = empty string — remove the old name).
    /// </summary>
    public void SetNewName(string? newName)
    {
        string replacement = newName ?? string.Empty;
        ApplyNameReplacement(OldName!, replacement);
        State = HasQuizPolls ? BulkEditState.SettingExplanation : BulkEditState.Done;
    }

    /// <summary>
    /// User provided explanation (or null = skip).
    /// </summary>
    public void SetExplanation(string? explanation)
    {
        if (explanation is not null)
        {
            Explanation = explanation;
            ApplyExplanation(explanation);
        }
        State = BulkEditState.Done;
    }

    // ── Private apply helpers ──────────────────────────────────────────────

    void ApplyNameReplacement(string oldName, string newName)
    {
        foreach (var poll in Polls)
        {
            poll.Question = poll.Question.Replace(oldName, newName);

            foreach (var opt in poll.Options)
                opt.Text = opt.Text.Replace(oldName, newName);

            if (poll.Explanation is not null)
                poll.Explanation = poll.Explanation.Replace(oldName, newName);
        }
    }

    void ApplyExplanation(string explanation)
    {
        foreach (var poll in Polls)
        {
            bool isQuiz = string.Equals(poll.Type, "quiz", StringComparison.OrdinalIgnoreCase);
            if (isQuiz)
                poll.Explanation = explanation;
        }
    }

    // ── Text representation ────────────────────────────────────────────────

    /// <summary>
    /// Generates a readable summary of all queued polls (shown after /bulk_done).
    /// </summary>
    public string GetPollsSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"📋 <b>POLL TEXT CODE</b> — {Polls.Count} poll(s) collected\n");

        for (int i = 0; i < Polls.Count; i++)
        {
            var p = Polls[i];
            bool isQuiz = string.Equals(p.Type, "quiz", StringComparison.OrdinalIgnoreCase);

            sb.AppendLine($"─── Poll #{i + 1} {(isQuiz ? "🧠 Quiz" : "📊 Regular")} ───");
            sb.AppendLine($"Q: {p.Question}");
            sb.AppendLine();

            for (int j = 0; j < p.Options.Length; j++)
            {
                string prefix = (isQuiz && p.CorrectOptionId == j) ? "✅" : $"{j + 1}.";
                sb.AppendLine($"{prefix} {p.Options[j].Text}");
            }

            if (!string.IsNullOrEmpty(p.Explanation))
                sb.AppendLine($"💡 {p.Explanation}");

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
