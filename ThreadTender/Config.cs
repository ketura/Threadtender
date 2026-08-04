using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThreadTender;

public class ChannelConfig
{
	/// <summary>User IDs allowed to make top-level posts in this channel.</summary>
	[JsonPropertyName("whitelisted_author_ids")]
	public List<ulong> WhitelistedAuthorIds { get; set; } = [];
}

public class BotConfig
{
	[JsonPropertyName("token")]
	public string Token { get; set; } = "";

	/// <summary>Keyed by channel ID (as string, since JSON keys are strings).</summary>
	[JsonPropertyName("channels")]
	public Dictionary<string, ChannelConfig> Channels { get; set; } = [];

	[JsonPropertyName("orphan_timeout_minutes")]
	public int OrphanTimeoutMinutes { get; set; } = 15;

	[JsonPropertyName("thread_name_max_length")]
	public int ThreadNameMaxLength { get; set; } = 80;

	/// <summary>Attachments larger than this are linked rather than re-uploaded.</summary>
	[JsonPropertyName("max_attachment_bytes")]
	public long MaxAttachmentBytes { get; set; } = 25 * 1024 * 1024;

	/// <summary>How many recent messages to scan for candidates / fuzzy text search.</summary>
	[JsonPropertyName("search_depth")]
	public int SearchDepth { get; set; } = 100;

	/// <summary>Minimum FuzzySharp partial-ratio score to consider a text match.</summary>
	[JsonPropertyName("fuzzy_threshold")]
	public int FuzzyThreshold { get; set; } = 70;

	[JsonIgnore]
	public Dictionary<ulong, ChannelConfig> ChannelsById { get; private set; } = [];

	public static BotConfig Load(string path)
	{
		if (!File.Exists(path))
		{
			var sample = new BotConfig
			{
				Token = "PASTE_BOT_TOKEN_HERE",
				Channels = new() { ["123456789012345678"] = new ChannelConfig { WhitelistedAuthorIds = [234567890123456789] } }
			};
			File.WriteAllText(path, JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true }));
			throw new InvalidOperationException($"No config found; a sample was written to {path}. Fill it in and restart.");
		}

		BotConfig config = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path))
			?? throw new InvalidOperationException($"Could not parse {path}");

		// Environment variable wins over file, so the token can be kept out of the mounted config.
		string? envToken = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
		if (!string.IsNullOrWhiteSpace(envToken))
			config.Token = envToken;

		if (string.IsNullOrWhiteSpace(config.Token) || config.Token == "PASTE_BOT_TOKEN_HERE")
			throw new InvalidOperationException("No bot token: set \"token\" in settings.json or the DISCORD_TOKEN env var.");

		config.ChannelsById = config.Channels.ToDictionary(kv => ulong.Parse(kv.Key), kv => kv.Value);
		return config;
	}

	public bool IsManagedChannel(ulong channelId) => ChannelsById.ContainsKey(channelId);

	public bool IsWhitelisted(ulong channelId, ulong userId) =>
		ChannelsById.TryGetValue(channelId, out ChannelConfig? c) && c.WhitelistedAuthorIds.Contains(userId);
}
