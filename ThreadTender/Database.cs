using Microsoft.Data.Sqlite;

namespace ThreadTender;

public record PendingOrphan(
	ulong MessageId,
	ulong ChannelId,
	ulong GuildId,
	ulong AuthorId,
	string Content,
	string Notes,
	ulong PromptMessageId,
	DateTimeOffset ExpiresAt);

/// <summary>
/// SQLite persistence: pending orphaned comments (so a restart doesn't eat anyone's
/// message) and a map of transposed-message IDs to their thread (so replies aimed at
/// an already-moved message can still be routed).
/// </summary>
public class Database : IDisposable
{
	private readonly SqliteConnection connection;
	private readonly object gate = new();

	public Database(string path)
	{
		connection = new SqliteConnection($"Data Source={path}");
		connection.Open();
		Execute("""
			CREATE TABLE IF NOT EXISTS pending_orphans (
				message_id INTEGER PRIMARY KEY,
				channel_id INTEGER NOT NULL,
				guild_id INTEGER NOT NULL,
				author_id INTEGER NOT NULL,
				content TEXT NOT NULL,
				notes TEXT NOT NULL DEFAULT '',
				prompt_message_id INTEGER NOT NULL DEFAULT 0,
				expires_at TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS transposed (
				original_message_id INTEGER PRIMARY KEY,
				thread_id INTEGER NOT NULL,
				created_at TEXT NOT NULL DEFAULT (datetime('now'))
			);
			""");
	}

	private void Execute(string sql, params (string, object)[] args)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = sql;
			foreach ((string name, object value) in args)
				cmd.Parameters.AddWithValue(name, value);
			cmd.ExecuteNonQuery();
		}
	}

	public void UpsertPending(PendingOrphan p) => Execute(
		"""
		INSERT INTO pending_orphans (message_id, channel_id, guild_id, author_id, content, notes, prompt_message_id, expires_at)
		VALUES ($mid, $cid, $gid, $aid, $content, $notes, $pmid, $exp)
		ON CONFLICT(message_id) DO UPDATE SET prompt_message_id = $pmid, expires_at = $exp
		""",
		("$mid", (long)p.MessageId), ("$cid", (long)p.ChannelId), ("$gid", (long)p.GuildId),
		("$aid", (long)p.AuthorId), ("$content", p.Content), ("$notes", p.Notes),
		("$pmid", (long)p.PromptMessageId), ("$exp", p.ExpiresAt.ToString("O")));

	public void DeletePending(ulong messageId) =>
		Execute("DELETE FROM pending_orphans WHERE message_id = $mid", ("$mid", (long)messageId));

	public List<PendingOrphan> GetAllPending()
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, channel_id, guild_id, author_id, content, notes, prompt_message_id, expires_at FROM pending_orphans";
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<PendingOrphan> result = [];
			while (reader.Read())
			{
				result.Add(new PendingOrphan(
					(ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2),
					(ulong)reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
					(ulong)reader.GetInt64(6), DateTimeOffset.Parse(reader.GetString(7))));
			}
			return result;
		}
	}

	public void RecordTransposed(ulong originalMessageId, ulong threadId) => Execute(
		"""
		INSERT INTO transposed (original_message_id, thread_id) VALUES ($mid, $tid)
		ON CONFLICT(original_message_id) DO UPDATE SET thread_id = $tid
		""",
		("$mid", (long)originalMessageId), ("$tid", (long)threadId));

	public void DeleteTransposed(ulong originalMessageId) =>
		Execute("DELETE FROM transposed WHERE original_message_id = $mid", ("$mid", (long)originalMessageId));

	public ulong? GetTransposedThread(ulong originalMessageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT thread_id FROM transposed WHERE original_message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)originalMessageId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	public void Dispose() => connection.Dispose();
}
