using System.Text.RegularExpressions;
using DSharpPlus.Entities;

namespace ThreadTender;

/// <summary>
/// The identity a message is processed under. Normally just the real author's ID; under
/// a ![name] debug masquerade it may be another member's ID, or a synthetic identity
/// (stable fake ID + display name) when the name matches nobody.
/// </summary>
public record EffectiveAuthor(ulong Id, string? NameOverride);

/// <summary>
/// Debug masquerade: users listed in debug_impersonation_user_ids may prefix a message
/// with ![name] to have the bot treat it as authored by that user. Resolution is naive
/// by design — a name matching a guild member adopts that member's real identity; any
/// other name becomes a synthetic user that exists only in the bot's eyes.
/// </summary>
public static partial class Masquerade
{
	[GeneratedRegex(@"^!\[(?<name>[^\]]{1,80})\]\s*")]
	private static partial Regex PrefixRegex();

	/// <summary>
	/// Returns the effective author and, when a masquerade applied, the message content
	/// with the ![name] prefix stripped (null = use the message's own content).
	/// </summary>
	public static async Task<(EffectiveAuthor Author, string? ContentOverride)> ResolveAsync(BotConfig config, DiscordMessage message)
	{
		EffectiveAuthor real = new(message.Author!.Id, null);
		if (!config.IsDebugUser(message.Author.Id))
			return (real, null);

		Match match = PrefixRegex().Match(message.Content ?? "");
		if (!match.Success)
			return (real, null);

		string name = match.Groups["name"].Value.Trim();
		string stripped = (message.Content ?? "")[match.Length..];

		// A name matching an actual member masquerades as them for real (whitelist
		// checks, pings, DMs all target that member).
		try
		{
			DiscordGuild guild = message.Channel!.Guild;
			IReadOnlyList<DiscordMember> found = await guild.SearchMembersAsync(name);
			DiscordMember? member = found.FirstOrDefault(m =>
				string.Equals(m.Username, name, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(m.DisplayName, name, StringComparison.OrdinalIgnoreCase));
			if (member is not null)
				return (new EffectiveAuthor(member.Id, null), stripped);
		}
		catch (Exception)
		{
			// Search unavailable — fall through to a synthetic identity.
		}

		return (new EffectiveAuthor(SyntheticId(name), name), stripped);
	}

	/// <summary>
	/// Removes a leading ![name] prefix if present. For callers (e.g. the flex sweep)
	/// that re-capture a debug user's message long after the original masquerade
	/// resolution, where the stored identity is already known.
	/// </summary>
	public static string? StripPrefix(string? content)
	{
		if (content is null)
			return null;
		Match match = PrefixRegex().Match(content);
		return match.Success ? content[match.Length..] : content;
	}

	/// <summary>
	/// Stable fake user ID for a made-up name: FNV-1a of the lowercased name, with the
	/// top nibble forced to 0xF so it can never collide with a real snowflake (those
	/// stay far below 2^63) and stays recognizable in logs.
	/// </summary>
	private static ulong SyntheticId(string name)
	{
		ulong hash = 14695981039346656037UL;
		foreach (char c in name.ToLowerInvariant())
		{
			hash ^= c;
			hash *= 1099511628211UL;
		}
		return hash | 0xF000_0000_0000_0000UL;
	}
}
