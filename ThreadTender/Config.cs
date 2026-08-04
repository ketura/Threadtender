using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreadTender;

public enum ChannelMode
{
	/// <summary>Pure-Thread-Enforcement: all discussion is forced into threads immediately.</summary>
	PureThread,
	/// <summary>Reply-Enforcement: non-whitelisted comments must be replies; replies themselves are left alone.</summary>
	ReplyOnly,
	/// <summary>Flex-Thread-Enforcement: Reply-Enforcement, plus a post's reply graph is swept into a thread once it outgrows the threshold.</summary>
	FlexThread,
}

public static class ChannelModes
{
	public static string ToConfigString(this ChannelMode mode) => mode switch
	{
		ChannelMode.PureThread => "pure",
		ChannelMode.ReplyOnly => "reply",
		ChannelMode.FlexThread => "flex",
		_ => "pure",
	};

	public static ChannelMode Parse(string value) => value.Trim().ToLowerInvariant() switch
	{
		"pure" or "pure_thread" or "pure-thread-enforcement" => ChannelMode.PureThread,
		"reply" or "reply_enforcement" or "reply-enforcement" => ChannelMode.ReplyOnly,
		"flex" or "flex_thread" or "flex-thread-enforcement" => ChannelMode.FlexThread,
		_ => throw new InvalidOperationException($"Unknown channel mode \"{value}\"."),
	};

	public static string DisplayName(this ChannelMode mode) => mode switch
	{
		ChannelMode.PureThread => "Pure-Thread-Enforcement",
		ChannelMode.ReplyOnly => "Reply-Enforcement",
		ChannelMode.FlexThread => "Flex-Thread-Enforcement",
		_ => "?",
	};

	/// <summary>Short explanation used in select-menu option descriptions (≤100 chars).</summary>
	public static string Description(this ChannelMode mode) => mode switch
	{
		ChannelMode.PureThread => "Every reply/comment is moved into a thread on its post immediately.",
		ChannelMode.ReplyOnly => "Comments must be replies; replies stay in the channel untouched.",
		ChannelMode.FlexThread => "Replies stay until a post's discussion outgrows a threshold, then all of it moves to a thread.",
		_ => "?",
	};
}

/// <summary>A channel's enforcement configuration. One binding per channel, stored in SQLite.</summary>
public record ChannelBinding(ulong ChannelId, ulong GuildId, ChannelMode Mode, List<ulong> Whitelist, int FlexThreshold);

/// <summary>
/// Runtime configuration. The settings file carries ONLY the bot token (overridable via
/// the DISCORD_TOKEN env var); channel bindings and every behavioral knob live in the
/// database, mutable at runtime through the setup slash commands.
/// </summary>
public class BotConfig
{
	private Database db = null!;
	private readonly ConcurrentDictionary<ulong, ChannelBinding> bindings = new();
	private readonly ConcurrentDictionary<string, string?> settingsCache = new();

	public string Token { get; private set; } = "";

	// ------------------------------------------------------------ global knobs
	// DB-backed with code defaults; cached, cache updated on mutation.

	public int OrphanTimeoutMinutes => GetInt("orphan_timeout_minutes", 15);
	public int ThreadNameMaxLength => GetInt("thread_name_max_length", 80);
	public long MaxAttachmentBytes => GetLong("max_attachment_bytes", 25 * 1024 * 1024);
	public int SearchDepth => GetInt("search_depth", 100);
	public int FuzzyThreshold => GetInt("fuzzy_threshold", 70);

	public IReadOnlyList<ulong> DebugImpersonationUserIds =>
		(GetString("debug_impersonation_user_ids") ?? "")
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(ulong.Parse).ToList();

	private string? GetString(string key) => settingsCache.GetOrAdd(key, k => db.GetSetting(k));
	private int GetInt(string key, int fallback) => int.TryParse(GetString(key), out int v) ? v : fallback;
	private long GetLong(string key, long fallback) => long.TryParse(GetString(key), out long v) ? v : fallback;

	public void SetGlobal(string key, string value)
	{
		db.SetSetting(key, value);
		settingsCache[key] = value;
	}

	public void SetDebugUsers(IEnumerable<ulong> userIds) =>
		SetGlobal("debug_impersonation_user_ids", string.Join(",", userIds));

	// ------------------------------------------------------------ bindings

	public IReadOnlyCollection<ChannelBinding> AllBindings => bindings.Values.ToList();

	public ChannelBinding? GetBinding(ulong channelId) =>
		bindings.TryGetValue(channelId, out ChannelBinding? b) ? b : null;

	public void SetBinding(ChannelBinding binding)
	{
		db.UpsertBinding(binding);
		bindings[binding.ChannelId] = binding;
	}

	public void RemoveBinding(ulong channelId)
	{
		db.DeleteBinding(channelId);
		bindings.TryRemove(channelId, out _);
	}

	public bool IsManagedChannel(ulong channelId) => bindings.ContainsKey(channelId);

	public bool IsWhitelisted(ulong channelId, ulong userId) =>
		GetBinding(channelId)?.Whitelist.Contains(userId) == true;

	public bool IsDebugUser(ulong userId) => DebugImpersonationUserIds.Contains(userId);

	// ------------------------------------------------------------ loading

	private sealed class TokenFile
	{
		[JsonPropertyName("token")]
		public string Token { get; set; } = "";
	}

	public static BotConfig Load(string path, Database db)
	{
		if (!File.Exists(path))
		{
			File.WriteAllText(path, JsonSerializer.Serialize(new TokenFile { Token = "PASTE_BOT_TOKEN_HERE" },
				new JsonSerializerOptions { WriteIndented = true }));
			throw new InvalidOperationException($"No config found; a sample was written to {Path.GetFullPath(path)}. Fill it in and restart.");
		}

		// Older config files carry extra keys (channels, timeouts, …); they are ignored —
		// those settings live in the database now.
		TokenFile file = JsonSerializer.Deserialize<TokenFile>(File.ReadAllText(path))
			?? throw new InvalidOperationException($"Could not parse {path}");

		BotConfig config = new() { Token = file.Token, db = db };

		// Environment variable wins over file, so the token can be kept out of the mounted config.
		string? envToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
		if (!string.IsNullOrWhiteSpace(envToken))
			config.Token = envToken;

		if (string.IsNullOrWhiteSpace(config.Token) || config.Token == "PASTE_BOT_TOKEN_HERE")
			throw new InvalidOperationException("No bot token: set \"token\" in settings.json or the DISCORD_TOKEN env var.");

		foreach (ChannelBinding binding in db.GetAllBindings())
			config.bindings[binding.ChannelId] = binding;

		return config;
	}
}
