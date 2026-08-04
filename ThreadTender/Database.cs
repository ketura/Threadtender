using Microsoft.Data.Sqlite;

namespace ThreadTender;

public record PendingOrphan(
	ulong MessageId,
	ulong ChannelId,
	ulong GuildId,
	ulong AuthorId,
	string Content,
	string Notes,
	string AuthorName,
	ulong PromptMessageId,
	DateTimeOffset ExpiresAt);

/// <summary>A comment recorded as part of a top-level post's reply graph (flex mode).</summary>
public record GraphEdge(ulong MessageId, ulong AuthorId, string AuthorName);

/// <summary>
/// SQLite persistence: pending orphaned comments (so a restart doesn't eat anyone's
/// message), a map of transposed-message IDs to their destination (so replies aimed at
/// an already-moved message can still be routed; thread_id 0 = moved back into the
/// channel rather than into a thread), reply-graph membership for flex-mode counting,
/// and the root→thread map for flex posts that have already been swept.
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
				author_name TEXT NOT NULL DEFAULT '',
				prompt_message_id INTEGER NOT NULL DEFAULT 0,
				expires_at TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS transposed (
				original_message_id INTEGER PRIMARY KEY,
				thread_id INTEGER NOT NULL,
				created_at TEXT NOT NULL DEFAULT (datetime('now'))
			);
			CREATE TABLE IF NOT EXISTS graph_edges (
				message_id INTEGER PRIMARY KEY,
				root_id INTEGER NOT NULL,
				channel_id INTEGER NOT NULL,
				author_id INTEGER NOT NULL,
				author_name TEXT NOT NULL DEFAULT '',
				line_count INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS idx_graph_edges_root ON graph_edges(root_id);
			CREATE TABLE IF NOT EXISTS flex_threads (
				root_id INTEGER PRIMARY KEY,
				thread_id INTEGER NOT NULL
			);
			CREATE TABLE IF NOT EXISTS bindings (
				channel_id INTEGER PRIMARY KEY,
				guild_id INTEGER NOT NULL,
				mode TEXT NOT NULL,
				whitelist TEXT NOT NULL DEFAULT '',
				flex_threshold INTEGER NOT NULL DEFAULT 5,
				flex_line_threshold INTEGER NOT NULL DEFAULT 0
			);
			CREATE TABLE IF NOT EXISTS settings (
				key TEXT PRIMARY KEY,
				value TEXT NOT NULL
			);
			""");

		// pending_orphans.author_name postdates the first release; graft it onto older DBs.
		try { Execute("ALTER TABLE pending_orphans ADD COLUMN author_name TEXT NOT NULL DEFAULT ''"); }
		catch (SqliteException) { /* column already exists */ }

		// (A short-lived bindings.repost_style column may exist in DBs from early builds;
		// it is simply ignored — repost style is contextual now, not configured.)

		// Dual flex thresholds postdate the original tables; graft onto older DBs.
		try { Execute("ALTER TABLE bindings ADD COLUMN flex_line_threshold INTEGER NOT NULL DEFAULT 0"); }
		catch (SqliteException) { /* column already exists */ }
		try { Execute("ALTER TABLE graph_edges ADD COLUMN line_count INTEGER NOT NULL DEFAULT 0"); }
		catch (SqliteException) { /* column already exists */ }
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

	// ---------------------------------------------------------------- pending orphans

	public void UpsertPending(PendingOrphan p) => Execute(
		"""
		INSERT INTO pending_orphans (message_id, channel_id, guild_id, author_id, content, notes, author_name, prompt_message_id, expires_at)
		VALUES ($mid, $cid, $gid, $aid, $content, $notes, $aname, $pmid, $exp)
		ON CONFLICT(message_id) DO UPDATE SET prompt_message_id = $pmid, expires_at = $exp
		""",
		("$mid", (long)p.MessageId), ("$cid", (long)p.ChannelId), ("$gid", (long)p.GuildId),
		("$aid", (long)p.AuthorId), ("$content", p.Content), ("$notes", p.Notes),
		("$aname", p.AuthorName), ("$pmid", (long)p.PromptMessageId), ("$exp", p.ExpiresAt.ToString("O")));

	public void DeletePending(ulong messageId) =>
		Execute("DELETE FROM pending_orphans WHERE message_id = $mid", ("$mid", (long)messageId));

	public List<PendingOrphan> GetAllPending()
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, channel_id, guild_id, author_id, content, notes, author_name, prompt_message_id, expires_at FROM pending_orphans";
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<PendingOrphan> result = [];
			while (reader.Read())
			{
				result.Add(new PendingOrphan(
					(ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2),
					(ulong)reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
					reader.GetString(6), (ulong)reader.GetInt64(7), DateTimeOffset.Parse(reader.GetString(8))));
			}
			return result;
		}
	}

	// ---------------------------------------------------------------- transposed map

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

	// ---------------------------------------------------------------- flex reply graphs

	public void AddGraphEdge(ulong messageId, ulong rootId, ulong channelId, ulong authorId, string authorName, int lineCount) => Execute(
		"""
		INSERT INTO graph_edges (message_id, root_id, channel_id, author_id, author_name, line_count)
		VALUES ($mid, $rid, $cid, $aid, $aname, $lines)
		ON CONFLICT(message_id) DO NOTHING
		""",
		("$mid", (long)messageId), ("$rid", (long)rootId), ("$cid", (long)channelId),
		("$aid", (long)authorId), ("$aname", authorName), ("$lines", lineCount));

	public void DeleteGraphEdge(ulong messageId) =>
		Execute("DELETE FROM graph_edges WHERE message_id = $mid", ("$mid", (long)messageId));

	/// <summary>The recorded edge for a single comment, if any — including its EFFECTIVE author.</summary>
	public GraphEdge? GetGraphEdge(ulong messageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, author_id, author_name FROM graph_edges WHERE message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)messageId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			return reader.Read()
				? new GraphEdge((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), reader.GetString(2))
				: null;
		}
	}

	public ulong? GetGraphRoot(ulong messageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT root_id FROM graph_edges WHERE message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)messageId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	/// <summary>Message count and total line count recorded against a root's reply graph.</summary>
	public (int Messages, int Lines) GetGraphStats(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(line_count), 0) FROM graph_edges WHERE root_id = $rid";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			reader.Read();
			return (reader.GetInt32(0), reader.GetInt32(1));
		}
	}

	/// <summary>All recorded comments for a root, oldest first (message IDs are chronological snowflakes).</summary>
	public List<GraphEdge> GetGraphEdges(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, author_id, author_name FROM graph_edges WHERE root_id = $rid ORDER BY message_id";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<GraphEdge> result = [];
			while (reader.Read())
				result.Add(new GraphEdge((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), reader.GetString(2)));
			return result;
		}
	}

	// ---------------------------------------------------------------- flex root→thread map

	public void SetFlexThread(ulong rootId, ulong threadId) => Execute(
		"""
		INSERT INTO flex_threads (root_id, thread_id) VALUES ($rid, $tid)
		ON CONFLICT(root_id) DO UPDATE SET thread_id = $tid
		""",
		("$rid", (long)rootId), ("$tid", (long)threadId));

	public ulong? GetFlexThread(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT thread_id FROM flex_threads WHERE root_id = $rid";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	// ---------------------------------------------------------------- bindings

	public void UpsertBinding(ChannelBinding b) => Execute(
		"""
		INSERT INTO bindings (channel_id, guild_id, mode, whitelist, flex_threshold, flex_line_threshold)
		VALUES ($cid, $gid, $mode, $wl, $flex, $flexLines)
		ON CONFLICT(channel_id) DO UPDATE SET guild_id = $gid, mode = $mode, whitelist = $wl, flex_threshold = $flex, flex_line_threshold = $flexLines
		""",
		("$cid", (long)b.ChannelId), ("$gid", (long)b.GuildId), ("$mode", b.Mode.ToConfigString()),
		("$wl", string.Join(",", b.Whitelist)), ("$flex", b.FlexThreshold), ("$flexLines", b.FlexLineThreshold));

	public void DeleteBinding(ulong channelId) =>
		Execute("DELETE FROM bindings WHERE channel_id = $cid", ("$cid", (long)channelId));

	public List<ChannelBinding> GetAllBindings()
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT channel_id, guild_id, mode, whitelist, flex_threshold, flex_line_threshold FROM bindings";
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<ChannelBinding> result = [];
			while (reader.Read())
			{
				List<ulong> whitelist = reader.GetString(3)
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(ulong.Parse).ToList();
				result.Add(new ChannelBinding(
					(ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1),
					ChannelModes.Parse(reader.GetString(2)), whitelist, reader.GetInt32(4), reader.GetInt32(5)));
			}
			return result;
		}
	}

	public void DeleteGraphEdgesForChannel(ulong channelId) =>
		Execute("DELETE FROM graph_edges WHERE channel_id = $cid", ("$cid", (long)channelId));

	// ---------------------------------------------------------------- settings

	public void SetSetting(string key, string value) => Execute(
		"INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v",
		("$k", key), ("$v", value));

	public string? GetSetting(string key)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT value FROM settings WHERE key = $k";
			cmd.Parameters.AddWithValue("$k", key);
			return cmd.ExecuteScalar() as string;
		}
	}

	public void Dispose() => connection.Dispose();
}
