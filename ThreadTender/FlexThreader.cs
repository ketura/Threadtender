using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// Flex-Thread-Enforcement bookkeeping: counts the comments accumulating in a top-level
/// post's reply graph and, once the configured threshold is exceeded, sweeps the whole
/// graph into an auto-created thread (oldest first, repost-before-delete per message).
/// After the sweep the post behaves like Pure-Thread-Enforcement.
/// </summary>
public class FlexThreader(BotConfig config, Database db, Transposer transposer, string dataDir)
{
	private readonly ConcurrentDictionary<ulong, SemaphoreSlim> rootLocks = new();

	/// <summary>
	/// Resolves the top-level post a comment ultimately belongs to, following both real
	/// reply chains and recorded graph edges (pseudo-reply reposts aren't Discord
	/// replies, so the walk alone can't see through them). Returns null when the
	/// recorded root no longer exists. Harmless in non-flex channels: no edges exist
	/// there, so this reduces to the plain reply-chain walk.
	/// </summary>
	public async Task<DiscordMessage?> ResolveRootAsync(DiscordChannel channel, DiscordMessage target)
	{
		ulong? rootId = db.GetGraphRoot(target.Id);
		if (rootId is null)
		{
			DiscordMessage walked = await Transposer.WalkToRootAsync(target);
			rootId = db.GetGraphRoot(walked.Id);
			if (rootId is null)
				return walked;
		}

		try { return await channel.GetMessageAsync(rootId.Value); }
		catch (NotFoundException) { return null; }
	}

	/// <summary>
	/// Records a comment against its root and sweeps the graph into a thread once it
	/// outgrows the channel's threshold. Also self-heals: if a root→thread mapping
	/// already exists (this comment raced past the mode switch), the sweep just moves
	/// whatever edges remain — including the one recorded here.
	/// </summary>
	public async Task RecordCommentAsync(DiscordClient client, ChannelBinding binding, DiscordChannel channel, ulong commentId, EffectiveAuthor author, DiscordMessage root)
	{
		SemaphoreSlim gate = rootLocks.GetOrAdd(root.Id, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			db.AddGraphEdge(commentId, root.Id, channel.Id, author.Id, author.NameOverride ?? "");

			if (db.GetFlexThread(root.Id) is not null || db.CountGraph(root.Id) > binding.FlexThreshold)
				await SweepGraphAsync(client, channel, root);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <summary>
	/// Rebuilds graph knowledge from channel history: records edges for the most recent
	/// replies (newest <paramref name="replyLimit"/> of them within the search window)
	/// that resolve to a whitelisted root. Runs on bind, on switching a channel to flex
	/// mode, and on startup, so the bot isn't blind to a reply graph that formed while
	/// it was down or unbound. Deliberately never triggers a sweep — the next live
	/// comment does that if the backfilled graph is already over the threshold.
	/// </summary>
	public async Task<int> BackfillGraphAsync(DiscordClient client, ChannelBinding binding, DiscordChannel channel, int replyLimit = 20)
	{
		if (binding.Mode != ChannelMode.FlexThread)
			return 0;

		List<DiscordMessage> replies = [];
		await foreach (DiscordMessage message in channel.GetMessagesAsync(config.SearchDepth))
		{
			if (message.MessageType != DiscordMessageType.Reply)
				continue;
			if (message.Author is null || message.Author.IsBot)
				continue;
			replies.Add(message);
			if (replies.Count >= replyLimit)
				break;
		}

		// Oldest first, so a nested reply can find the edge of the reply it targets.
		replies.Reverse();

		int added = 0;
		foreach (DiscordMessage message in replies)
		{
			try
			{
				if (await TryBackfillOneAsync(client, channel, message))
					added++;
			}
			catch (Exception)
			{
				// One unreadable reply must not abort the rest of the backfill.
			}
		}
		return added;
	}

	private async Task<bool> TryBackfillOneAsync(DiscordClient client, DiscordChannel channel, DiscordMessage message)
	{
		if (db.GetGraphEdge(message.Id) is not null || db.GetTransposedThread(message.Id) is not null)
			return false; // already known

		// Re-run masquerade resolution so a historical ![name] message keeps its identity.
		(EffectiveAuthor author, string? _) = await Masquerade.ResolveAsync(config, message);

		DiscordMessage? target = message.ReferencedMessage;
		if (target is null && message.Reference?.Message is not null)
		{
			try { target = await channel.GetMessageAsync(message.Reference.Message.Id); }
			catch (NotFoundException) { return false; }
		}
		if (target is null)
			return false;

		// Same effective-author continuation exemption as the live path.
		if (config.IsWhitelisted(channel.Id, author.Id))
		{
			ulong targetEffectiveAuthor = db.GetGraphEdge(target.Id)?.AuthorId ?? target.Author?.Id ?? 0;
			if (targetEffectiveAuthor == author.Id)
				return false;
		}

		DiscordMessage? root = await ResolveRootAsync(channel, target);
		if (root is null || root.Author is null || root.Author.IsBot || !config.IsWhitelisted(channel.Id, root.Author.Id))
			return false;
		if (db.GetFlexThread(root.Id) is not null)
			return false; // root already swept; live replies to it route straight to its thread

		db.AddGraphEdge(message.Id, root.Id, channel.Id, author.Id, author.NameOverride ?? "");
		return true;
	}

	/// <summary>Creates (or finds) the root's thread and moves every recorded comment into it, oldest first.</summary>
	private async Task SweepGraphAsync(DiscordClient client, DiscordChannel channel, DiscordMessage root)
	{
		DiscordThreadChannel thread = await transposer.GetOrCreateThreadAsync(client, root);

		List<GraphEdge> edges = db.GetGraphEdges(root.Id); // oldest → newest
		foreach (GraphEdge edge in edges)
		{
			DiscordMessage message;
			try
			{
				message = await channel.GetMessageAsync(edge.MessageId);
			}
			catch (NotFoundException)
			{
				db.DeleteGraphEdge(edge.MessageId); // deleted out from under us; nothing to move
				continue;
			}

			// Pseudo-reply headers and (for debug users) masquerade prefixes were part of
			// the in-channel presentation; neither belongs in the swept copy.
			string? contentOverride = Transposer.StripReplyHeader(message.Content);
			if (message.Author is not null && config.IsDebugUser(message.Author.Id))
				contentOverride = Masquerade.StripPrefix(contentOverride);

			string tempDir = Path.Combine(dataDir, "temp", edge.MessageId.ToString());
			MovableContent content = await MovableContent.CaptureAsync(message, tempDir, config.MaxAttachmentBytes,
				edge.AuthorId, edge.AuthorName.Length > 0 ? edge.AuthorName : null,
				contentOverride);
			try
			{
				// Same invariant as everywhere else: repost first, delete only once the
				// copy exists. A crash mid-sweep leaves the mapping unset, so the next
				// comment's RecordCommentAsync re-runs the sweep and moves the remainder.
				await transposer.RepostAsync(client, thread, content, edge.MessageId);
				try { await message.DeleteAsync("ThreadTender: reply graph moved to thread"); }
				catch (NotFoundException) { }
				db.DeleteGraphEdge(edge.MessageId);
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

		db.SetFlexThread(root.Id, thread.Id);
	}
}
