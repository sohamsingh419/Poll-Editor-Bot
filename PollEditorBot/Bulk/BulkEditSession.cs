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
    /// <summary>Raw input from user (may contain | separators).</summary>
    public string? OldName       { get; private set; }

    /// <summary>All individual old names split by '|' and trimmed.</summary>
    public string[] OldNames =>
        OldName is null
            ? Array.Empty<string>()
            : OldName.Split('|').Select(n => n.Trim()).Where(n => n.Length > 0).ToArray();

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

    void ApplyNameReplacement(string oldNamesRaw, string newName)
    {
        // Support multiple old names separated by '|'
        var names = oldNamesRaw.Split('|')
                               .Select(n => n.Trim())
                               .Where(n => n.Length > 0)
                               .ToArray();

        foreach (var poll in Polls)
        {
            foreach (var name in names)
            {
                poll.Question = poll.Question.Replace(name, newName);

                // Replace in each option; keep the original text if result would be empty
                foreach (var opt in poll.Options)
                {
                    string replaced = opt.Text.Replace(name, newName).Trim();
                    if (!string.IsNullOrEmpty(replaced))
                        opt.Text = replaced;
                    // else: leave original text — empty options crash Telegram
                }

                if (poll.Explanation is not null)
                {
                    poll.Explanation = poll.Explanation.Replace(name, newName);
                    // Clear entities — offsets are invalid after text changes
                    poll.ExplanationEntities = null;
                }
            }

            // Remove options that are empty/whitespace (e.g. the option WAS the watermark)
            // but only if at least 2 non-empty options remain; otherwise keep all.
            var nonEmpty = poll.Options.Where(o => !string.IsNullOrWhiteSpace(o.Text)).ToArray();
            if (nonEmpty.Length >= 2)
                poll.Options = nonEmpty;
        }
    }

    void ApplyExplanation(string explanation)
    {
        foreach (var poll in Polls)
        {
            bool isQuiz = string.Equals(poll.Type, "quiz", StringComparison.OrdinalIgnoreCase);
            if (isQuiz)
            {
                poll.Explanation = explanation;
                // Clear original entities — offsets don't match the new text
                poll.ExplanationEntities = null;
            }
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
