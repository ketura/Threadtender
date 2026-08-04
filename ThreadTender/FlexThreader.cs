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
	/// A comment's "line" weight: character count divided by 60, plus one — a 20-char
	/// message is still one line, it just hasn't wrapped yet. Deliberately naive
	/// otherwise (no newline handling). Five short quips and one wall of text weigh
	/// comparably.
	/// </summary>
	public static int LinesOf(string? content) => (content ?? "").Length / 60 + 1;

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
			DiscordMessage walked = await Transposer.WalkToRootAsync(target,
				m => config.IsWhitelisted(channel.Id, m.Author?.Id ?? 0));
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
	public async Task RecordCommentAsync(DiscordClient client, ChannelBinding binding, DiscordChannel channel, ulong commentId, int lineCount, EffectiveAuthor author, DiscordMessage root)
	{
		SemaphoreSlim gate = rootLocks.GetOrAdd(root.Id, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			db.AddGraphEdge(commentId, root.Id, channel.Id, author.Id, author.NameOverride ?? "", lineCount);

			// Dual threshold: either enough messages, or enough total text — reaching a
			// threshold triggers (the 5th comment sweeps at threshold 5). Line
			// threshold 0 = message count only.
			(int messages, int lines) = db.GetGraphStats(root.Id);
			bool overThreshold = messages >= binding.FlexThreshold
				|| (binding.FlexLineThreshold > 0 && lines >= binding.FlexLineThreshold);

			if (db.GetFlexThread(root.Id) is not null || overThreshold)
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
		await foreach (DiscordMessage message in channel.GetMessagesAsync(config.SearchDepth(channel.GuildId ?? 0)))
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
		(EffectiveAuthor author, string? contentOverride) = await Masquerade.ResolveAsync(config, message);

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

		db.AddGraphEdge(message.Id, root.Id, channel.Id, author.Id, author.NameOverride ?? "",
			LinesOf(contentOverride ?? message.Content));
		return true;
	}

	/// <summary>Creates (or finds) the root's thread and moves every recorded comment into it, oldest first.</summary>
	private async Task SweepGraphAsync(DiscordClient client, DiscordChannel channel, DiscordMessage root)
	{
		DiscordThreadChannel thread = await transposer.GetOrCreateThreadAsync(client, root);

		List<GraphEdge> edges = db.GetGraphEdges(root.Id); // oldest → newest
		List<(ulong Id, string? Name)> movedAuthors = [];

		// Original message ID → its copy in the thread, built as the sweep walks oldest
		// first (so a reply's parent is always copied before the reply itself). Used to
		// recreate reply chains: webhooks can't set real reply references, so a copy
		// whose parent also moved gets a link header pointing at the parent's copy.
		Dictionary<ulong, DiscordMessage> copies = [];

		foreach (GraphEdge edge in edges)
		{
			// An edge whose message already has a transposed copy (a crash landed between
			// the repost and the edge deletion on a previous sweep) must never be
			// reposted again — just finish the delete-and-forget half.
			if (db.GetTransposedThread(edge.MessageId) is not null)
			{
				try
				{
					DiscordMessage stale = await channel.GetMessageAsync(edge.MessageId);
					await stale.DeleteAsync("ThreadTender: already transposed");
				}
				catch (NotFoundException) { }
				db.DeleteGraphEdge(edge.MessageId);
				continue;
			}

			DiscordMessage message;
			try
			{
				// skipCache: the cached copy is a snapshot from when the message arrived —
				// reactions added (and edits made) since then only exist on the REST copy.
				message = await channel.GetMessageAsync(edge.MessageId, skipCache: true);
			}
			catch (NotFoundException)
			{
				db.DeleteGraphEdge(edge.MessageId); // deleted out from under us; nothing to move
				continue;
			}

			// Pseudo-reply headers, bot-relay "@user:" lines, and (for debug users)
			// masquerade prefixes were part of the in-channel presentation; none of them
			// belong in the swept copy — the repost re-adds its own presentation.
			string? contentOverride = Transposer.StripReplyHeader(message.Content);
			contentOverride = Transposer.StripAuthorTag(contentOverride, edge.AuthorId, edge.AuthorName);
			if (message.Author is not null && config.IsDebugUser(channel.GuildId ?? 0, message.Author.Id))
				contentOverride = Masquerade.StripPrefix(contentOverride);

			string tempDir = Path.Combine(dataDir, "temp", edge.MessageId.ToString());
			MovableContent content = await MovableContent.CaptureAsync(message, tempDir, config.MaxAttachmentBytes(channel.GuildId ?? 0),
				edge.AuthorId, edge.AuthorName.Length > 0 ? edge.AuthorName : null,
				contentOverride);
			try
			{
				// Same invariant as everywhere else: repost first, delete only once the
				// copy exists. A crash mid-sweep leaves the mapping unset, so the next
				// comment's RecordCommentAsync re-runs the sweep and moves the remainder.
				// Reply linkage: a real reply carries a reference; a pseudo-reply carries
				// its target in the header line (parsed from the RAW content, before the
				// header is stripped above). Links to the root itself are omitted —
				// sequential flow in the thread already implies them.
				ulong? parentId = Transposer.ParseReplyHeaderTarget(message.Content) ?? message.Reference?.Message?.Id;
				string? replyHeader = parentId is { } pid && pid != root.Id && copies.TryGetValue(pid, out DiscordMessage? parentCopy)
					? Transposer.ReplyHeaderFor(parentCopy)
					: null;

				// The sweep re-presents a conversation that already happened, so webhook
				// impersonation keeps it reading naturally; live transpositions elsewhere
				// use the bot relay instead.
				DiscordMessage copy = await transposer.RepostAsync(client, thread, content, edge.MessageId, RepostStyle.Webhook, replyHeader);
				copies[edge.MessageId] = copy;

				// Reactions can't be transposed, but the bot can echo each emoji on the
				// copy — an invitation for people to re-react. Decoration only: a failed
				// echo never aborts the sweep, but it IS logged (reaction rate limits are
				// a special, easily-fumbled bucket) rather than swallowed silently.
				if (message.Reactions is { Count: > 0 })
				{
					Console.WriteLine(
						$"[ThreadTender] Echoing {message.Reactions.Count} reaction(s) from {edge.MessageId}: " +
						string.Join(" ", message.Reactions.Select(r => r.Emoji.ToString())));
					foreach (DiscordReaction reaction in message.Reactions)
					{
						try
						{
							await copy.CreateReactionAsync(reaction.Emoji);
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[ThreadTender] Reaction echo {reaction.Emoji} failed on copy {copy.Id}: {ex.GetType().Name}: {ex.Message}");
						}
						// Discord's reaction rate limit is ~1 per 300ms per channel and uses
						// its own bucket; explicit spacing keeps echoes from tripping 429s.
						await Task.Delay(325);
					}
				}

				try { await message.DeleteAsync("ThreadTender: reply graph moved to thread"); }
				catch (NotFoundException) { }
				db.DeleteGraphEdge(edge.MessageId);
				if (!movedAuthors.Any(a => a.Id == edge.AuthorId))
					movedAuthors.Add((edge.AuthorId, edge.AuthorName.Length > 0 ? edge.AuthorName : null));
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

		if (movedAuthors.Count > 0)
		{
			// One mass ping so everyone whose messages just moved knows where the
			// conversation went; synthetic debug identities render as plain text.
			string tags = string.Join(" ", movedAuthors.Select(a => a.Name is not null ? $"**{a.Name}**" : $"<@{a.Id}>"));
			await thread.SendMessageAsync(new DiscordMessageBuilder()
				.WithContent($"{tags} — created discussion thread")
				.WithAllowedMentions(movedAuthors.Where(a => a.Name is null).Select(a => (IMention)new UserMention(a.Id)).ToList()));
		}
	}
}
