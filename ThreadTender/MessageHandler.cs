using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// Routes new messages in managed channels according to the channel's mode:
/// - Pure-Thread-Enforcement: replies get transposed into the target post's thread;
///   non-reply comments from non-whitelisted users go to the orphan flow.
/// - Reply-Enforcement: replies are left alone; non-reply comments from non-whitelisted
///   users go to the orphan flow (resolving into an in-channel pseudo-reply).
/// - Flex-Thread-Enforcement: Reply-Enforcement, plus replies are counted against their
///   root post and the whole graph is swept into a thread past the threshold; posts
///   that have been swept behave like pure mode from then on.
/// </summary>
public class MessageHandler(BotConfig config, Database db, Transposer transposer, OrphanManager orphans, FlexThreader flex, string dataDir)
{
	public async Task OnMessageCreatedAsync(DiscordClient client, MessageCreatedEventArgs e)
	{
		DiscordMessage message = e.Message;

		if (message.Author is null || message.Author.IsBot)
			return;
		// Thread messages have the thread's own channel ID, so they fall out here;
		// this check only admits the configured top-level channels.
		if (!config.IsManagedChannel(message.ChannelId) || message.Channel is DiscordThreadChannel)
			return;
		if (message.MessageType is not (DiscordMessageType.Default or DiscordMessageType.Reply))
			return; // system messages (thread-created notices, pins, etc.)

		ChannelBinding binding = config.GetBinding(message.ChannelId)!;
		(EffectiveAuthor author, string? contentOverride) = await Masquerade.ResolveAsync(config, message);
		bool isReply = message.MessageType == DiscordMessageType.Reply;
		bool whitelisted = config.IsWhitelisted(message.ChannelId, author.Id);

		switch (binding.Mode)
		{
			case ChannelMode.PureThread:
				if (isReply)
					await HandlePureReplyAsync(client, message, author, contentOverride);
				else if (!whitelisted)
					await orphans.InterceptAsync(client, message, author, contentOverride);
				// else: whitelisted top-level post — exactly what the channel is for.
				break;

			case ChannelMode.ReplyOnly:
				// Being a reply is the whole rule here; anything that is one is left alone.
				if (!isReply && !whitelisted)
					await orphans.InterceptAsync(client, message, author, contentOverride);
				break;

			case ChannelMode.FlexThread:
				if (isReply)
					await HandleFlexReplyAsync(client, binding, message, author, contentOverride);
				else if (!whitelisted)
					await orphans.InterceptAsync(client, message, author, contentOverride);
				break;
		}
	}

	private async Task HandlePureReplyAsync(DiscordClient client, DiscordMessage message, EffectiveAuthor author, string? contentOverride)
	{
		DiscordMessage? target = message.ReferencedMessage;

		if (target is null && message.Reference?.Message is not null)
		{
			ulong targetId = message.Reference.Message.Id;
			if (await TryTransposeViaMappingAsync(client, message, author, contentOverride, targetId))
				return;
			try { target = await message.Channel!.GetMessageAsync(targetId); }
			catch (NotFoundException) { }
		}

		if (target is null || target.Author is null || target.Author.IsBot)
		{
			// Reply to a vanished message or to one of the bot's own prompts:
			// treat like an orphan so the user can point at the right post.
			await orphans.InterceptAsync(client, message, author, contentOverride);
			return;
		}

		// Self-reply exemption: a whitelisted author continuing their own post stays top-level.
		if (config.IsWhitelisted(message.ChannelId, author.Id) && target.Author.Id == author.Id)
			return;

		DiscordMessage anchor = await Transposer.WalkToRootAsync(target,
			m => config.IsWhitelisted(message.ChannelId, m.Author?.Id ?? 0));
		DiscordThreadChannel thread;
		try
		{
			thread = await transposer.GetOrCreateThreadAsync(client, anchor);
		}
		catch (Exception ex) when (ex is NotFoundException or BadRequestException)
		{
			// The anchor vanished between this reply arriving and thread creation (the
			// author deleted it mid-flight). The reply itself still exists untouched —
			// route it through the orphan flow so its author can re-aim it.
			await orphans.InterceptAsync(client, message, author, contentOverride);
			return;
		}
		await TransposeIntoThreadAsync(client, message, author, contentOverride, thread);
	}

	private async Task HandleFlexReplyAsync(DiscordClient client, ChannelBinding binding, DiscordMessage message, EffectiveAuthor author, string? contentOverride)
	{
		DiscordMessage? target = message.ReferencedMessage;

		if (target is null && message.Reference?.Message is not null)
		{
			ulong targetId = message.Reference.Message.Id;
			if (await TryTransposeViaMappingAsync(client, message, author, contentOverride, targetId))
				return;
			try { target = await message.Channel!.GetMessageAsync(targetId); }
			catch (NotFoundException) { }
		}

		if (target is null)
		{
			await orphans.InterceptAsync(client, message, author, contentOverride);
			return;
		}

		// Self-reply exemption, same as pure mode: author continuations stay top-level
		// and are never counted against the threshold. Judged against the target's
		// EFFECTIVE author — a masqueraded (![name]) comment is physically authored by
		// the debug user and a pseudo-reply by the webhook, but the graph edge remembers
		// who it really belongs to. Without this, the author replying to a masqueraded
		// comment would get the continuation exemption and the reply would escape the
		// graph entirely (uncounted, and left behind by the sweep).
		if (config.IsWhitelisted(message.ChannelId, author.Id))
		{
			ulong targetEffectiveAuthor = db.GetGraphEdge(target.Id)?.AuthorId ?? target.Author?.Id ?? 0;
			if (targetEffectiveAuthor == author.Id)
				return;
		}

		// Graph-aware root resolution sees through pseudo-reply reposts; a reply to a
		// bot prompt (or anything else with no whitelisted root) goes to the orphan flow.
		DiscordMessage? root = await flex.ResolveRootAsync(message.Channel!, target);
		if (root is null || root.Author is null || root.Author.IsBot || !config.IsWhitelisted(message.ChannelId, root.Author.Id))
		{
			await orphans.InterceptAsync(client, message, author, contentOverride);
			return;
		}

		// A post that has already been swept behaves like pure mode.
		ulong? threadId = db.GetFlexThread(root.Id);
		if (threadId is not null)
		{
			try
			{
				if (await client.GetChannelAsync(threadId.Value) is DiscordThreadChannel thread)
				{
					await TransposeIntoThreadAsync(client, message, author, contentOverride, thread);
					return;
				}
			}
			catch (NotFoundException)
			{
				// Thread was deleted; fall through — the sweep below will recreate it.
			}
		}

		// Below the thresholds the reply simply stays in the channel; it just gets
		// counted (and may trip the sweep that moves it, and everything else, out).
		await flex.RecordCommentAsync(client, binding, message.Channel!, message.Id,
			FlexThreader.LinesOf(contentOverride ?? message.Content), author, root);
	}

	/// <summary>
	/// If the reply's target was already moved into a thread, transposes the reply there
	/// and returns true. A mapping of 0 means "moved, but not into a thread" (a
	/// Reply-Enforcement repost) and is not a routing destination.
	/// </summary>
	private async Task<bool> TryTransposeViaMappingAsync(DiscordClient client, DiscordMessage message, EffectiveAuthor author, string? contentOverride, ulong targetId)
	{
		ulong? known = db.GetTransposedThread(targetId);
		if (known is not { } threadId || threadId == 0)
			return false;
		try
		{
			if (await client.GetChannelAsync(threadId) is DiscordThreadChannel mapped)
			{
				await TransposeIntoThreadAsync(client, message, author, contentOverride, mapped);
				return true;
			}
		}
		catch (NotFoundException)
		{
			db.DeleteTransposed(targetId); // thread deleted since; fall through
		}
		return false;
	}

	private async Task TransposeIntoThreadAsync(DiscordClient client, DiscordMessage message, EffectiveAuthor author, string? contentOverride, DiscordThreadChannel thread)
	{
		// Idempotency against replayed gateway events: already moved → nothing to do.
		if (db.GetTransposedThread(message.Id) is not null)
			return;

		string tempDir = Path.Combine(dataDir, "temp", message.Id.ToString());
		MovableContent content = await MovableContent.CaptureAsync(message, tempDir, config.MaxAttachmentBytes(message.Channel!.GuildId ?? 0),
			author.Id, author.NameOverride, contentOverride);
		try
		{
			// Repost FIRST, delete only once the copy exists — a failed repost then
			// leaves the original untouched instead of destroying it. The brief
			// double-existence is the price of never losing a user's message.
			// Live transposition = bot relay: the bot is acting on someone's message
			// right now and says so, rather than impersonating them.
			await transposer.RepostAsync(client, thread, content, message.Id, RepostStyle.Bot);
			try { await message.DeleteAsync("ThreadTender: reply moved to thread"); }
			catch (NotFoundException) { } // already gone; the copy exists either way
		}
		finally
		{
			content.DeleteFiles();
			if (Directory.Exists(tempDir))
			{
				try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
			}
		}
	}
}
