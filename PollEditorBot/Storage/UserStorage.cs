using System.Text.Json;
using PollEditorBot.Loggers;

namespace PollEditorBot.Storage;

/// <summary>
/// Persists the set of known user IDs to a local JSON file so that
/// the broadcast list survives process restarts and crashes.
///
/// NOTE: On Render free tier the filesystem is ephemeral — data is
/// lost on a full redeploy. For permanent persistence across redeploys
/// connect a database (Task #3).
/// </summary>
public static class UserStorage
{
    static readonly string FilePath = Path.Combine(
        AppContext.BaseDirectory, "data", "users.json");

    /// <summary>Load users from disk. Returns empty set if file missing or corrupt.</summary>
    public static HashSet<long> Load(ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<HashSet<long>>(json) ?? new();
        }
        catch (Exception ex)
        {
            logger?.LogInformationLine($"UserStorage.Load warning: {ex.Message}");
            return new();
        }
    }

    /// <summary>Save users to disk. Silently ignores I/O errors.</summary>
    public static void Save(HashSet<long> users, ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(users));
        }
        catch (Exception ex)
        {
            logger?.LogInformationLine($"UserStorage.Save warning: {ex.Message}");
        }
    }
}
