using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// Finds or creates the discussion thread for a top-level post and reposts captured
/// content into it via webhook impersonation.
/// </summary>
public class Transposer(BotConfig config, Database db)
{
	private const string WebhookName = "ThreadTender";

	private readonly ConcurrentDictionary<ulong, DiscordWebhook> webhookCache = new();
	private readonly ConcurrentDictionary<ulong, SemaphoreSlim> anchorLocks = new();

	/// <summary>Walks a reply chain up to its top-level root message.</summary>
	public static async Task<DiscordMessage> WalkToRootAsync(DiscordMessage message)
	{
		// Whitelisted authors build long top-level self-reply chains by design, and a cap
		// that's too low would anchor threads mid-chain, fragmenting discussion. 50 is a
		// backstop against reference cycles, not an expected length.
		DiscordMessage current = message;
		for (int depth = 0; depth < 50 && current.MessageType == DiscordMessageType.Reply; depth++)
		{
			DiscordMessage? referenced = current.ReferencedMessage;
			if (referenced is null && current.Reference?.Message is not null)
			{
				try { referenced = await current.Channel!.GetMessageAsync(current.Reference.Message.Id); }
				catch (NotFoundException) { break; }
			}
			if (referenced is null)
				break;
			current = referenced;
		}
		return current;
	}

	/// <summary>Finds or creates the thread attached to the given top-level message, unarchiving if needed.</summary>
	public async Task<DiscordThreadChannel> GetOrCreateThreadAsync(DiscordClient client, DiscordMessage anchor)
	{
		SemaphoreSlim gate = anchorLocks.GetOrAdd(anchor.Id, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			DiscordThreadChannel thread;
			try
			{
				thread = await anchor.Channel!.CreateThreadAsync(anchor, ThreadNameFor(anchor), DiscordAutoArchiveDuration.Week, "Auto-thread for discussion");
			}
			catch (BadRequestException original)
			{
				// Usually "message already has a thread"; its ID equals the message ID.
				try
				{
					thread = await client.GetChannelAsync(anchor.Id) as DiscordThreadChannel
						?? throw new InvalidOperationException($"Channel {anchor.Id} exists but is not a thread.");
				}
				catch (NotFoundException)
				{
					// The 400 was something else entirely; surface it rather than masking it.
					throw original;
				}
			}

			if (thread.ThreadMetadata?.IsArchived == true)
				await thread.ModifyAsync(m => m.IsArchived = false);

			return thread;
		}
		finally
		{
			gate.Release();
		}
	}

	private string ThreadNameFor(DiscordMessage anchor)
	{
		int max = Math.Clamp(config.ThreadNameMaxLength, 10, 95); // Discord caps thread names at 100
		string name = (anchor.Content ?? "").Replace('\n', ' ').Trim();
		if (name.Length == 0)
			name = $"Discussion — {(anchor.Author as DiscordMember)?.DisplayName ?? anchor.Author?.Username ?? "post"}";
		if (name.Length > max)
			name = name[..max] + "…";
		return name;
	}

	/// <summary>
	/// Reposts captured content into the given thread as its original author (webhook
	/// impersonation) and records the mapping.
	/// </summary>
	public async Task RepostAsync(DiscordClient client, DiscordThreadChannel thread, MovableContent content, ulong originalMessageId)
	{
		// Callers can arrive here with a mapped thread that bypassed GetOrCreateThreadAsync,
		// and webhooks cannot post into archived threads — so unarchive defensively.
		if (thread.ThreadMetadata?.IsArchived == true)
			await thread.ModifyAsync(m => m.IsArchived = false);

		DiscordChannel parent = await client.GetChannelAsync(thread.ParentId
			?? throw new InvalidOperationException($"Thread {thread.Id} has no parent"));

		(string displayName, string? avatarUrl) = await ResolveIdentityAsync(client, thread.Guild, content);
		DiscordWebhook webhook = await GetOrCreateWebhookAsync(parent);

		// Retries once with a fresh webhook if the cached one was deleted out from under us.
		async Task ExecuteResilientAsync(DiscordWebhookBuilder b)
		{
			try
			{
				await webhook.ExecuteAsync(b);
			}
			catch (NotFoundException)
			{
				webhookCache.TryRemove(parent.Id, out _);
				webhook = await GetOrCreateWebhookAsync(parent);
				await webhook.ExecuteAsync(b);
			}
		}

		string text = content.Content;
		if (content.Notes.Length > 0)
			text = text.Length > 0 ? $"{text}\n{content.Notes}" : content.Notes;

		// Forwards and notes can push past Discord's 2000-char cap; chunk if needed.
		List<string> chunks = Chunk(text, 2000);

		// A message can end up with no movable payload at all (e.g. a bare ![name]
		// masquerade prefix that stripping reduced to nothing). Discord rejects an
		// empty webhook execute outright — post a placeholder rather than dying.
		if (chunks.Count == 0 && content.Files.Count == 0)
			chunks.Add("*(empty message)*");

		// Note: no AddMentions() call on these builders — with an empty mention list the
		// library sends allowed_mentions: none, so the repost cannot re-ping anyone. The
		// original message already fired its notifications before we deleted it.
		DiscordWebhookBuilder NewBuilder()
		{
			DiscordWebhookBuilder b = new DiscordWebhookBuilder().WithUsername(displayName).WithThreadId(thread.Id);
			if (avatarUrl is not null)
				b.WithAvatarUrl(avatarUrl);
			return b;
		}

		DiscordWebhookBuilder builder = NewBuilder();
		if (chunks.Count > 0)
			builder.WithContent(chunks[0]);

		List<FileStream> streams = [];
		try
		{
			foreach ((string name, string path) in content.Files)
			{
				FileStream stream = File.OpenRead(path);
				streams.Add(stream);
				builder.AddFile(name, stream);
			}
			await ExecuteResilientAsync(builder);
		}
		finally
		{
			foreach (FileStream stream in streams)
				await stream.DisposeAsync();
		}

		foreach (string chunk in chunks.Skip(1))
			await ExecuteResilientAsync(NewBuilder().WithContent(chunk));

		db.RecordTransposed(originalMessageId, thread.Id);
	}

	/// <summary>
	/// Reply-Enforcement repost: puts captured content back into the main channel as a
	/// webhook-impersonated pseudo-reply. Webhooks cannot create real Discord replies,
	/// so a subtext header links the target instead. Returns the reposted message's ID
	/// (the first chunk's, when chunked) so callers can record it as a graph member.
	/// </summary>
	public async Task<ulong> RepostToChannelAsync(DiscordClient client, DiscordChannel channel, MovableContent content, ulong originalMessageId, DiscordMessage replyTarget)
	{
		(string displayName, string? avatarUrl) = await ResolveIdentityAsync(client, channel.Guild, content);
		DiscordWebhook webhook = await GetOrCreateWebhookAsync(channel);

		async Task<DiscordMessage> ExecuteResilientAsync(DiscordWebhookBuilder b)
		{
			try
			{
				return await webhook.ExecuteAsync(b);
			}
			catch (NotFoundException)
			{
				webhookCache.TryRemove(channel.Id, out _);
				webhook = await GetOrCreateWebhookAsync(channel);
				return await webhook.ExecuteAsync(b);
			}
		}

		DiscordWebhookBuilder NewBuilder()
		{
			DiscordWebhookBuilder b = new DiscordWebhookBuilder().WithUsername(displayName);
			if (avatarUrl is not null)
				b.WithAvatarUrl(avatarUrl);
			return b;
		}

		string text = ReplyHeaderFor(replyTarget);
		if (content.Content.Length > 0)
			text += $"\n{content.Content}";
		if (content.Notes.Length > 0)
			text += $"\n{content.Notes}";
		List<string> chunks = Chunk(text, 2000);

		DiscordWebhookBuilder builder = NewBuilder().WithContent(chunks[0]);

		DiscordMessage first;
		List<FileStream> streams = [];
		try
		{
			foreach ((string name, string path) in content.Files)
			{
				FileStream stream = File.OpenRead(path);
				streams.Add(stream);
				builder.AddFile(name, stream);
			}
			first = await ExecuteResilientAsync(builder);
		}
		finally
		{
			foreach (FileStream stream in streams)
				await stream.DisposeAsync();
		}

		foreach (string chunk in chunks.Skip(1))
			await ExecuteResilientAsync(NewBuilder().WithContent(chunk));

		// thread_id 0 = "moved, but not into a thread": guards against replayed events
		// re-intercepting a message we already handled.
		db.RecordTransposed(originalMessageId, 0);
		return first.Id;
	}

	/// <summary>The subtext header a pseudo-reply carries in place of a real reply reference.</summary>
	public static string ReplyHeaderFor(DiscordMessage target) => $"-# ↪ in reply to {target.JumpLink}";

	/// <summary>Strips a pseudo-reply header line so re-transposition into a thread doesn't drag it along.</summary>
	public static string? StripReplyHeader(string? content)
	{
		if (content is null || !content.StartsWith("-# ↪", StringComparison.Ordinal))
			return content;
		int newline = content.IndexOf('\n');
		return newline < 0 ? "" : content[(newline + 1)..];
	}

	internal static List<string> Chunk(string text, int max)
	{
		List<string> chunks = [];
		while (text.Length > max)
		{
			int split = text.LastIndexOf('\n', max - 1);
			if (split < max / 2)
				split = text.LastIndexOf(' ', max - 1);
			if (split < max / 2)
				split = max;
			chunks.Add(text[..split]);
			text = text[split..].TrimStart('\n', ' ');
		}
		if (text.Length > 0)
			chunks.Add(text);
		return chunks;
	}

	private static async Task<(string DisplayName, string? AvatarUrl)> ResolveIdentityAsync(DiscordClient client, DiscordGuild guild, MovableContent content)
	{
		// Synthetic debug identities carry their own name and have no resolvable ID.
		if (content.AuthorName is not null)
			return (content.AuthorName, null);

		try
		{
			DiscordMember member = await guild.GetMemberAsync(content.AuthorId);
			return (member.DisplayName, member.GuildAvatarUrl ?? member.AvatarUrl);
		}
		catch (NotFoundException)
		{
			try
			{
				DiscordUser user = await client.GetUserAsync(content.AuthorId);
				return (user.Username, user.AvatarUrl);
			}
			catch (NotFoundException)
			{
				return ($"Unknown User ({content.AuthorId})", null);
			}
		}
	}

	private async Task<DiscordWebhook> GetOrCreateWebhookAsync(DiscordChannel channel)
	{
		if (webhookCache.TryGetValue(channel.Id, out DiscordWebhook? cached))
			return cached;

		IReadOnlyList<DiscordWebhook> hooks = await channel.GetWebhooksAsync();
		DiscordWebhook webhook = hooks.FirstOrDefault(h => h.Name == WebhookName && !string.IsNullOrEmpty(h.Token))
			?? await channel.CreateWebhookAsync(WebhookName, reason: "ThreadTender transposition webhook");

		webhookCache[channel.Id] = webhook;
		return webhook;
	}
}
