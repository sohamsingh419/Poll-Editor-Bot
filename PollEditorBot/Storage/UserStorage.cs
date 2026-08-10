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
        catch (Exception)
        {
            logger?.LogWarningLine("PostgreSQL user storage unavailable. Check DATABASE_URL and database availability.");
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
        catch (Exception)
        {
            logger?.LogWarningLine("UserStorage.Save warning: PostgreSQL connection or write failed.");
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
        catch (Exception)
        {
            logger?.LogWarningLine("UserStorage.GetAll warning: PostgreSQL connection or read failed.");
            return LoadLegacyUsers(logger);
        }
    }

    static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("DATABASE_URL environment variable is not set.");

        connectionString = NormalizeConnectionString(connectionString);

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    static string NormalizeConnectionString(string connectionString)
    {
        if (!Uri.TryCreate(connectionString, UriKind.Absolute, out Uri? databaseUri)
            || (databaseUri.Scheme != "postgresql" && databaseUri.Scheme != "postgres"))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = databaseUri.Host,
            Port = databaseUri.IsDefaultPort ? 5432 : databaseUri.Port,
            Database = Uri.UnescapeDataString(databaseUri.AbsolutePath.TrimStart('/')),
            SslMode = SslMode.Require
        };

        string[] credentials = databaseUri.UserInfo.Split(':', 2);
        if (credentials.Length > 0 && credentials[0].Length > 0)
            builder.Username = Uri.UnescapeDataString(credentials[0]);
        if (credentials.Length > 1)
            builder.Password = Uri.UnescapeDataString(credentials[1]);

        // Hosted PostgreSQL URLs may contain "?sslmode" without a value.
        // Require TLS for all URI-style database URLs, including that form.
        foreach (string parameter in databaseUri.Query.TrimStart('?')
                     .Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] keyValue = parameter.Split('=', 2);
            if (keyValue.Length == 0
                || !string.Equals(Uri.UnescapeDataString(keyValue[0]), "sslmode",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            if (keyValue.Length > 1
                && Enum.TryParse<SslMode>(
                    Uri.UnescapeDataString(keyValue[1]), true, out SslMode sslMode))
            {
                builder.SslMode = sslMode;
            }
        }

        return builder.ConnectionString;
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
