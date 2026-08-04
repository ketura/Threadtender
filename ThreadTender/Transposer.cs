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
		DiscordMessage current = message;
		for (int depth = 0; depth < 10 && current.MessageType == DiscordMessageType.Reply; depth++)
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
	/// impersonation), pings the author there, and records the mapping.
	/// </summary>
	public async Task RepostAsync(DiscordClient client, DiscordThreadChannel thread, MovableContent content, ulong originalMessageId)
	{
		// Callers can arrive here with a mapped thread that bypassed GetOrCreateThreadAsync,
		// and webhooks cannot post into archived threads — so unarchive defensively.
		if (thread.ThreadMetadata?.IsArchived == true)
			await thread.ModifyAsync(m => m.IsArchived = false);

		DiscordChannel parent = await client.GetChannelAsync(thread.ParentId
			?? throw new InvalidOperationException($"Thread {thread.Id} has no parent"));

		(string displayName, string avatarUrl) = await ResolveIdentityAsync(client, thread.Guild, content.AuthorId);
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

		// Note: no AddMentions() call on these builders — with an empty mention list the
		// library sends allowed_mentions: none, so the repost cannot re-ping anyone. The
		// original message already fired its notifications before we deleted it.
		DiscordWebhookBuilder builder = new DiscordWebhookBuilder()
			.WithUsername(displayName)
			.WithAvatarUrl(avatarUrl)
			.WithThreadId(thread.Id);
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
		{
			await ExecuteResilientAsync(new DiscordWebhookBuilder()
				.WithUsername(displayName).WithAvatarUrl(avatarUrl).WithThreadId(thread.Id)
				.WithContent(chunk));
		}

		db.RecordTransposed(originalMessageId, thread.Id);

		await thread.SendMessageAsync(new DiscordMessageBuilder()
			.WithContent($"<@{content.AuthorId}> — moved your message into this thread.")
			.WithAllowedMentions([new UserMention(content.AuthorId)]));
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

	private static async Task<(string DisplayName, string AvatarUrl)> ResolveIdentityAsync(DiscordClient client, DiscordGuild guild, ulong userId)
	{
		try
		{
			DiscordMember member = await guild.GetMemberAsync(userId);
			return (member.DisplayName, member.GuildAvatarUrl ?? member.AvatarUrl);
		}
		catch (NotFoundException)
		{
			DiscordUser user = await client.GetUserAsync(userId);
			return (user.Username, user.AvatarUrl);
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
