using PollEditorBot.Loggers;
using Npgsql;

namespace PollEditorBot.Storage;

/// <summary>
/// Persists known Telegram chat IDs in PostgreSQL so the broadcast list
/// survives restarts and redeploys.
/// </summary>
public static class UserStorage
{
    static readonly string LegacyFilePath = Path.Combine(
        AppContext.BaseDirectory, "data", "users.json");

    /// <summary>
    /// Loads users from PostgreSQL and imports the old JSON file if it exists.
    /// The JSON fallback keeps the bot usable while the database is unavailable.
    /// </summary>
    public static async Task<HashSet<long>> LoadAsync(ILogger? logger = null)
    {
        try
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureSchemaAsync(connection);
            var users = await ReadUsersAsync(connection);

            // Migrate users collected by older versions. ON CONFLICT makes this
            // safe to run on every startup until the legacy file is removed.
            foreach (long chatId in LoadLegacyUsers(logger))
            {
                await UpsertAsync(connection, chatId);
                users.Add(chatId);
            }

            if (users.Count > 0)
                logger?.LogInformationLine($"Loaded {users.Count} users from PostgreSQL.");

            return users;
        }
        catch (Exception ex)
        {
            logger?.LogWarningLine($"PostgreSQL user storage unavailable: {ex.Message}");
            return LoadLegacyUsers(logger);
        }
    }

    /// <summary>Adds a user or refreshes their last-seen timestamp.</summary>
    public static async Task UpsertUserAsync(long chatId, ILogger? logger = null)
    {
        try
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureSchemaAsync(connection);
            await UpsertAsync(connection, chatId);
        }
        catch (Exception ex)
        {
            logger?.LogWarningLine($"UserStorage.Save warning: {ex.Message}");
        }
    }

    /// <summary>Returns the current broadcast recipients from PostgreSQL.</summary>
    public static async Task<HashSet<long>> GetAllAsync(ILogger? logger = null)
    {
        try
        {
            await using var connection = await OpenConnectionAsync();
            await EnsureSchemaAsync(connection);
            return await ReadUsersAsync(connection);
        }
        catch (Exception ex)
        {
            logger?.LogWarningLine($"UserStorage.GetAll warning: {ex.Message}");
            return LoadLegacyUsers(logger);
        }
    }

    static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DATABASE_URL environment variable is not set.");

        // Some hosted PostgreSQL providers expose a URI ending in a bare
        // "?sslmode". Npgsql requires a value, so treat that shorthand as the
        // usual hosted-database setting instead of rejecting the whole URL.
        connectionString = NormalizeConnectionString(connectionString);

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    static string NormalizeConnectionString(string connectionString)
    {
        int queryStart = connectionString.IndexOf('?');
        if (queryStart < 0 || queryStart == connectionString.Length - 1)
            return connectionString;

        string baseUri = connectionString[..queryStart];
        string query = connectionString[(queryStart + 1)..];
        string[] parameters = query.Split('&', StringSplitOptions.None);

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].IndexOf('=') >= 0)
                continue;

            string parameterName = Uri.UnescapeDataString(parameters[i]);
            if (string.Equals(parameterName, "sslmode", StringComparison.OrdinalIgnoreCase))
                parameters[i] = "sslmode=require";
        }

        return $"{baseUri}?{string.Join("&", parameters)}";
    }

    static async Task<HashSet<long>> ReadUsersAsync(NpgsqlConnection connection)
    {
        var users = new HashSet<long>();
        await using var command = new NpgsqlCommand(
            "SELECT chat_id FROM bot_users ORDER BY chat_id", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            users.Add(reader.GetInt64(0));
        return users;
    }

    static async Task EnsureSchemaAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS bot_users (" +
            "chat_id BIGINT PRIMARY KEY, " +
            "created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(), " +
            "last_seen_at TIMESTAMPTZ NOT NULL DEFAULT NOW()" +
            ")",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    static async Task UpsertAsync(NpgsqlConnection connection, long chatId)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO bot_users (chat_id) " +
            "VALUES ($1) " +
            "ON CONFLICT (chat_id) " +
            "DO UPDATE SET last_seen_at = NOW()",
            connection);
        command.Parameters.AddWithValue(chatId);
        await command.ExecuteNonQueryAsync();
    }

    static HashSet<long> LoadLegacyUsers(ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(LegacyFilePath)) return new();
            string json = File.ReadAllText(LegacyFilePath);
            return System.Text.Json.JsonSerializer.Deserialize<HashSet<long>>(json) ?? new();
        }
        catch (Exception ex)
        {
            logger?.LogInformationLine($"Legacy user storage warning: {ex.Message}");
            return new();
        }
    }
}
