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

	/// <summary>
	/// Walks a reply chain toward its root, stopping at the nearest message that counts
	/// as a whitelisted author's TOP-LEVEL post: a non-reply, or a reply to the author's
	/// own message (a self-reply "continuation" is a top-level post in its own right,
	/// often a deliberate link to an earlier post). An author's reply to someone ELSE is
	/// ordinary discussion and gets walked through like anyone else's comment.
	/// </summary>
	public static async Task<DiscordMessage> WalkToRootAsync(DiscordMessage message, Func<DiscordMessage, bool> isWhitelistedAuthor)
	{
		// The 50-hop cap is a backstop against reference cycles, not an expected length.
		DiscordMessage current = message;
		for (int depth = 0; depth < 50 && current.MessageType == DiscordMessageType.Reply; depth++)
		{
			// Resolve the parent first — whether a whitelisted author's reply is a root
			// depends on WHO it answers (self = continuation; anyone else = discussion).
			DiscordMessage? parent = current.ReferencedMessage;
			if (parent is null && current.Reference?.Message is not null)
			{
				try { parent = await current.Channel!.GetMessageAsync(current.Reference.Message.Id); }
				catch (NotFoundException) { break; }
			}
			if (parent is null)
				break;

			if (isWhitelistedAuthor(current) && current.Author is not null && parent.Author?.Id == current.Author.Id)
				break; // self-reply continuation: current IS the top-level post

			current = parent;
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
		int max = Math.Clamp(config.ThreadNameMaxLength(anchor.Channel?.GuildId ?? 0), 10, 95); // Discord caps thread names at 100
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
	/// <returns>The first message of the reposted copy (callers may decorate it, e.g. reaction echoes).</returns>
	public async Task<DiscordMessage> RepostAsync(DiscordClient client, DiscordThreadChannel thread, MovableContent content, ulong originalMessageId, RepostStyle style, string? replyHeader = null)
	{
		// Callers can arrive here with a mapped thread that bypassed GetOrCreateThreadAsync,
		// and webhooks cannot post into archived threads — so unarchive defensively.
		if (thread.ThreadMetadata?.IsArchived == true)
			await thread.ModifyAsync(m => m.IsArchived = false);

		// Bot-relay style: the bot posts under its own identity with an "@user:" header
		// instead of impersonating via webhook.
		if (style == RepostStyle.Bot)
		{
			DiscordMessage botFirst = await SendAsBotAsync(b => thread.SendMessageAsync(b), content, header: replyHeader);
			db.RecordTransposed(originalMessageId, thread.Id);
			return botFirst;
		}

		DiscordChannel parent = await client.GetChannelAsync(thread.ParentId
			?? throw new InvalidOperationException($"Thread {thread.Id} has no parent"));

		(string displayName, string? avatarUrl) = await ResolveIdentityAsync(client, thread.Guild, content);
		DiscordWebhook webhook = await GetOrCreateWebhookAsync(parent);

		// Retries once with a fresh webhook if the cached one was deleted out from under us.
		async Task<DiscordMessage> ExecuteResilientAsync(DiscordWebhookBuilder b)
		{
			try
			{
				return await webhook.ExecuteAsync(b);
			}
			catch (NotFoundException)
			{
				webhookCache.TryRemove(parent.Id, out _);
				webhook = await GetOrCreateWebhookAsync(parent);
				return await webhook.ExecuteAsync(b);
			}
		}

		string text = content.Content;
		if (content.Notes.Length > 0)
			text = text.Length > 0 ? $"{text}\n{content.Notes}" : content.Notes;
		if (replyHeader is not null)
			text = text.Length > 0 ? $"{replyHeader}\n{text}" : replyHeader;

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

		db.RecordTransposed(originalMessageId, thread.Id);
		return first;
	}

	/// <summary>
	/// Reply-Enforcement repost: puts captured content back into the main channel as a
	/// webhook-impersonated pseudo-reply. Webhooks cannot create real Discord replies,
	/// so a subtext header links the target instead. Returns the reposted message's ID
	/// (the first chunk's, when chunked) so callers can record it as a graph member.
	/// </summary>
	public async Task<ulong> RepostToChannelAsync(DiscordClient client, DiscordChannel channel, MovableContent content, ulong originalMessageId, DiscordMessage replyTarget, RepostStyle style = RepostStyle.Bot)
	{
		if (style == RepostStyle.Bot)
		{
			DiscordMessage botFirst = await SendAsBotAsync(b => channel.SendMessageAsync(b), content, ReplyHeaderFor(replyTarget));
			db.RecordTransposed(originalMessageId, 0);
			return botFirst.Id;
		}

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

	/// <summary>The "@user:" line a bot-relay repost opens with (bold name for synthetic debug identities).</summary>
	private static string AuthorTagFor(MovableContent content) =>
		content.AuthorName is not null ? $"**{content.AuthorName}**:" : $"<@{content.AuthorId}>:";

	/// <summary>
	/// Bot-relay repost: the bot posts as itself — optional pseudo-reply header, then
	/// "@user:", then the content. Mentions render but never ping (suppressed), matching
	/// the webhook path's no-re-ping behavior. Returns the first message sent.
	/// </summary>
	private static async Task<DiscordMessage> SendAsBotAsync(Func<DiscordMessageBuilder, Task<DiscordMessage>> send, MovableContent content, string? header)
	{
		string text = AuthorTagFor(content);
		if (header is not null)
			text = $"{header}\n{text}";
		if (content.Content.Length > 0)
			text += $"\n{content.Content}";
		if (content.Notes.Length > 0)
			text += $"\n{content.Notes}";

		List<string> chunks = Chunk(text, 2000); // never empty: the author tag is always present

		DiscordMessageBuilder first = new DiscordMessageBuilder().WithContent(chunks[0]).WithAllowedMentions([]);
		DiscordMessage firstMessage;
		List<FileStream> streams = [];
		try
		{
			foreach ((string name, string path) in content.Files)
			{
				FileStream stream = File.OpenRead(path);
				streams.Add(stream);
				first.AddFile(name, stream);
			}
			firstMessage = await send(first);
		}
		finally
		{
			foreach (FileStream stream in streams)
				await stream.DisposeAsync();
		}

		foreach (string chunk in chunks.Skip(1))
			await send(new DiscordMessageBuilder().WithContent(chunk).WithAllowedMentions([]));

		return firstMessage;
	}

	/// <summary>
	/// Strips the "@user:" line a bot-relay repost added, so re-transposition (the flex
	/// sweep) doesn't stack a second header on top. No-op when the prefix isn't there
	/// (webhook reposts, ordinary messages).
	/// </summary>
	public static string? StripAuthorTag(string? content, ulong authorId, string authorName)
	{
		if (content is null)
			return null;
		string tag = authorName.Length > 0 ? $"**{authorName}**:" : $"<@{authorId}>:";
		if (!content.StartsWith(tag, StringComparison.Ordinal))
			return content;
		string rest = content[tag.Length..];
		return rest.StartsWith('\n') ? rest[1..] : rest;
	}

	/// <summary>The subtext header a pseudo-reply carries in place of a real reply reference.</summary>
	public static string ReplyHeaderFor(DiscordMessage target) => $"-# ↪ in reply to {target.JumpLink}";

	/// <summary>
	/// Extracts the target message ID from a pseudo-reply header line, if present — the
	/// jump link's last path segment. Lets the sweep treat pseudo-replies as reply-graph
	/// links even though they carry no real Discord reference.
	/// </summary>
	public static ulong? ParseReplyHeaderTarget(string? content)
	{
		if (content is null || !content.StartsWith("-# ↪", StringComparison.Ordinal))
			return null;
		int newline = content.IndexOf('\n');
		string line = newline < 0 ? content : content[..newline];
		int slash = line.LastIndexOf('/');
		return slash >= 0 && ulong.TryParse(line[(slash + 1)..].Trim(), out ulong id) ? id : null;
	}

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
