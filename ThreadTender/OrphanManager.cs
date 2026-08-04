using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;
using FuzzySharp;

namespace ThreadTender;

/// <summary>
/// Handles non-reply comments from non-whitelisted users: deletes and stores the
/// comment, prompts the commenter for the post they meant to reply to (recent-post
/// select, or message ID / text snippet via modal), then resolves according to the
/// channel's mode — into the post's thread (pure, or flex once swept) or back into the
/// channel as a pseudo-reply (reply/flex). Pending comments survive restarts;
/// unresolved ones are DMed back on timeout.
/// </summary>
public class OrphanManager(BotConfig config, Database db, Transposer transposer, FlexThreader flex, string dataDir)
{
	private readonly ConcurrentDictionary<ulong, CancellationTokenSource> timeouts = new();
	private readonly ConcurrentDictionary<ulong, byte> processing = new();

	/// <summary>Claims exclusive handling of a pending orphan (resolution vs. timeout race).</summary>
	private bool TryClaim(ulong pendingId) => processing.TryAdd(pendingId, 0);

	private string PendingDir(ulong messageId) => Path.Combine(dataDir, "pending", messageId.ToString());

	/// <summary>How a pending orphan addresses its author: mention for real users, bold name for synthetic ones.</summary>
	private static string AddresseeFor(PendingOrphan pending) =>
		pending.AuthorName.Length > 0 ? $"**{pending.AuthorName}**" : $"<@{pending.AuthorId}>";

	// ---------------------------------------------------------------- intake

	public async Task InterceptAsync(DiscordClient client, DiscordMessage message, EffectiveAuthor author, string? contentOverride)
	{
		// Gateway events are at-least-once; a replayed event for an already-intercepted
		// (or already-resolved) message must not touch existing state or re-prompt.
		if (db.GetAllPending().Any(p => p.MessageId == message.Id))
			return;
		if (db.GetTransposedThread(message.Id) is not null)
			return;

		MovableContent content = await MovableContent.CaptureAsync(message, PendingDir(message.Id), config.MaxAttachmentBytes,
			author.Id, author.NameOverride, contentOverride);

		// Persist BEFORE deleting — from here on, the content can always be recovered.
		PendingOrphan pending = new(message.Id, message.ChannelId, message.Channel!.Guild.Id, author.Id,
			content.Content, content.Notes, author.NameOverride ?? "", 0,
			DateTimeOffset.UtcNow.AddMinutes(config.OrphanTimeoutMinutes));
		db.UpsertPending(pending);

		try
		{
			await message.DeleteAsync("ThreadTender: non-reply comment intercepted");
		}
		catch (NotFoundException)
		{
			// Already gone (raced with another deletion) — proceed as deleted.
		}
		catch (Exception)
		{
			// Nothing was deleted; roll back the pending record.
			db.DeletePending(message.Id);
			content.DeleteFiles();
			throw;
		}

		try
		{
			await SendPromptAsync(client, message, pending);
		}
		catch (Exception)
		{
			// The message is gone but we can't prompt — hand the content straight back.
			await CancelAsync(client, pending, dmContent: true);
			throw;
		}
	}

	private async Task SendPromptAsync(DiscordClient client, DiscordMessage message, PendingOrphan pending)
	{
		List<DiscordMessage> candidates = await GetRecentTopLevelPostsAsync(message.Channel!, 10);

		DiscordMessageBuilder prompt = new DiscordMessageBuilder()
			.WithContent(
				$"{AddresseeFor(pending)} — this channel keeps discussion attached to posts, so I've set your message aside for a moment. " +
				$"**Which post were you replying to?** Pick one below, or give me a message ID / a snippet of its text. " +
				$"You have {config.OrphanTimeoutMinutes} minutes; after that I'll DM your text back to you so nothing is lost.");
		if (pending.AuthorName.Length == 0)
			prompt.WithAllowedMentions([new UserMention(pending.AuthorId)]);

		if (candidates.Count > 0)
		{
			List<DiscordSelectComponentOption> options = [];
			foreach (DiscordMessage candidate in candidates)
			{
				string label = Snippet(candidate.Content, 90);
				if (label.Length == 0)
					label = "(no text — attachment/embed post)";
				options.Add(new DiscordSelectComponentOption(label, candidate.Id.ToString(),
					$"by {(candidate.Author as DiscordMember)?.DisplayName ?? candidate.Author?.Username} · {candidate.Timestamp:MMM d HH:mm}"));
			}
			prompt.AddActionRowComponent(new DiscordSelectComponent($"tt:sel:{message.Id}", "Recent posts…", options));
		}

		prompt.AddActionRowComponent(
			new DiscordButtonComponent(DiscordButtonStyle.Primary, $"tt:btn:{message.Id}", "Enter message ID or text…"),
			new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"tt:cxl:{message.Id}", "Cancel (DM me my text)"));

		DiscordMessage promptMessage = await message.Channel!.SendMessageAsync(prompt);
		pending = pending with { PromptMessageId = promptMessage.Id };
		db.UpsertPending(pending);

		ScheduleTimeout(client, pending);
	}

	// ---------------------------------------------------------------- interactions

	public async Task HandleComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		if (parts.Length != 3 || parts[0] != "tt")
			return;
		string action = parts[1];
		ulong pendingId = ulong.Parse(parts[2]);

		PendingOrphan? pending = db.GetAllPending().FirstOrDefault(p => p.MessageId == pendingId);
		if (pending is null)
		{
			await RespondEphemeralAsync(e.Interaction, "This prompt has expired — if it was yours, the original text was DMed back to its author.");
			return;
		}

		// Debug users may drive any prompt: masqueraded orphans belong to identities
		// that can't click buttons themselves.
		if (e.User.Id != pending.AuthorId && !config.IsDebugUser(e.User.Id))
		{
			await RespondEphemeralAsync(e.Interaction, "This prompt belongs to someone else's message.");
			return;
		}

		switch (action)
		{
			case "sel":
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				await ResolveByMessageIdAsync(client, pending, ulong.Parse(e.Values[0]), e.Interaction);
				break;

			case "btn":
				DiscordModalBuilder modal = new DiscordModalBuilder()
					.WithTitle("Which post did you mean?")
					.WithCustomId($"tt:mod:{pendingId}")
					.AddTextInput(
						new DiscordTextInputComponent("tt-input", "message ID, or a snippet of the post's text"),
						"Target post", "Paste a message ID (dev mode) or type a few words from the post.");
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.Modal, modal);
				break;

			case "cxl":
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				await CancelAsync(client, pending, dmContent: true);
				break;

			case "dsel":
				// Disambiguation pick on the ephemeral follow-up.
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage,
					new DiscordInteractionResponseBuilder().WithContent("⏳ Moving your message…"));
				await ResolveByMessageIdAsync(client, pending, ulong.Parse(e.Values[0]), e.Interaction, editOriginal: true);
				break;
		}
	}

	public async Task HandleModalAsync(DiscordClient client, ModalSubmittedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		if (parts.Length != 3 || parts[0] != "tt" || parts[1] != "mod")
			return;
		ulong pendingId = ulong.Parse(parts[2]);

		PendingOrphan? pending = db.GetAllPending().FirstOrDefault(p => p.MessageId == pendingId);
		if (pending is null)
		{
			await RespondEphemeralAsync(e.Interaction, "This prompt has expired — your original text was DMed back to you.");
			return;
		}

		string query = e.Values.TryGetValue("tt-input", out IModalSubmission? submission) && submission is TextInputModalSubmission text
			? (text.Value ?? "").Trim()
			: "";

		if (query.Length == 0)
		{
			await RespondEphemeralAsync(e.Interaction, "I didn't get any text — press the button and try again.");
			return;
		}

		// Everything below involves REST calls that can blow Discord's 3-second ack
		// window (especially under rate limits) — defer immediately, edit afterward.
		await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
			new DiscordInteractionResponseBuilder().AsEphemeral());

		DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);

		// Dev-mode path: a raw message ID.
		if (query.Length is >= 17 and <= 20 && ulong.TryParse(query, out ulong messageId))
		{
			try
			{
				DiscordMessage target = await channel.GetMessageAsync(messageId);
				await ResolveAsync(client, pending, target, e.Interaction, editOriginal: true);
			}
			catch (NotFoundException)
			{
				await NotifyAsync(e.Interaction, $"No message with ID `{query}` exists in that channel — press the button to try again.", editOriginal: true);
			}
			return;
		}

		// Text path: fuzzy-match against recent top-level posts.
		List<DiscordMessage> posts = await GetRecentTopLevelPostsAsync(channel, config.SearchDepth);
		List<(DiscordMessage Message, int Score)> matches = posts
			.Where(p => !string.IsNullOrWhiteSpace(p.Content))
			.Select(p => (Message: p, Score: Fuzz.PartialRatio(query.ToLowerInvariant(), p.Content!.ToLowerInvariant())))
			.Where(m => m.Score >= config.FuzzyThreshold)
			.OrderByDescending(m => m.Score)
			.Take(5)
			.ToList();

		if (matches.Count == 0)
		{
			await NotifyAsync(e.Interaction, "No post matched that text — press the button to try again (or use a message ID).", editOriginal: true);
			return;
		}

		bool unambiguous = matches.Count == 1 || (matches[0].Score >= 85 && matches[0].Score - matches[1].Score >= 10);
		if (unambiguous)
		{
			await ResolveAsync(client, pending, matches[0].Message, e.Interaction, editOriginal: true);
			return;
		}

		List<DiscordSelectComponentOption> options = matches
			.Select(m => new DiscordSelectComponentOption(
				Snippet(m.Message.Content, 90), m.Message.Id.ToString(), $"match {m.Score}%"))
			.ToList();
		await e.Interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder()
			.WithContent("A few posts match — which one did you mean?")
			.AddActionRowComponent(new DiscordSelectComponent($"tt:dsel:{pendingId}", "Pick the post…", options)));
	}

	// ---------------------------------------------------------------- resolution

	private async Task ResolveByMessageIdAsync(DiscordClient client, PendingOrphan pending, ulong targetId, DiscordInteraction interaction, bool editOriginal = false)
	{
		DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);
		try
		{
			DiscordMessage target = await channel.GetMessageAsync(targetId);
			await ResolveAsync(client, pending, target, interaction, editOriginal);
		}
		catch (NotFoundException)
		{
			await NotifyAsync(interaction, "That post seems to have been deleted — press the button to pick another.", editOriginal);
		}
	}

	private async Task ResolveAsync(DiscordClient client, PendingOrphan pending, DiscordMessage target, DiscordInteraction interaction, bool editOriginal = false)
	{
		if (!TryClaim(pending.MessageId))
		{
			await NotifyAsync(interaction, "That message is already being handled.", editOriginal);
			return;
		}

		ChannelBinding? binding = config.GetBinding(pending.ChannelId);
		if (binding is null)
		{
			// The channel was unbound while this prompt sat open; return the content.
			processing.TryRemove(pending.MessageId, out _);
			await NotifyAsync(interaction, "This channel is no longer managed by the bot — I'll send your message back to you.", editOriginal);
			await CancelAsync(client, pending, dmContent: true);
			return;
		}

		try
		{
			DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);

			// Graph-aware in flex channels; a plain reply-chain walk everywhere else.
			// The raw-ID path can point at anything in the channel (a bot prompt,
			// another orphan) — only whitelisted authors' posts may be resolution targets.
			DiscordMessage? root = await flex.ResolveRootAsync(channel, target);
			if (root is null || root.Author is null || root.Author.IsBot || !config.IsWhitelisted(pending.ChannelId, root.Author.Id))
			{
				processing.TryRemove(pending.MessageId, out _);
				await NotifyAsync(interaction, "That message isn't one of this channel's top-level posts — press the button and pick again.", editOriginal);
				return;
			}

			switch (binding.Mode)
			{
				case ChannelMode.PureThread:
				{
					DiscordThreadChannel thread = await transposer.GetOrCreateThreadAsync(client, root);
					await transposer.RepostAsync(client, thread, RehydrateContent(pending), pending.MessageId);
					await CleanupAsync(client, pending, deletePrompt: true);
					await NotifyAsync(interaction, $"✅ Moved to {thread.Mention}.", editOriginal);
					break;
				}

				case ChannelMode.ReplyOnly:
				{
					await transposer.RepostToChannelAsync(client, channel, RehydrateContent(pending), pending.MessageId, target);
					await CleanupAsync(client, pending, deletePrompt: true);
					await DismissAsync(interaction, editOriginal);
					break;
				}

				case ChannelMode.FlexThread:
				{
					// A post that has already been swept behaves like pure mode.
					ulong? threadId = db.GetFlexThread(root.Id);
					DiscordThreadChannel? existing = threadId is null ? null : await TryGetThreadAsync(client, threadId.Value);
					if (existing is not null)
					{
						await transposer.RepostAsync(client, existing, RehydrateContent(pending), pending.MessageId);
						await CleanupAsync(client, pending, deletePrompt: true);
						await NotifyAsync(interaction, $"✅ Moved to {existing.Mention}.", editOriginal);
						break;
					}

					ulong repostId = await transposer.RepostToChannelAsync(client, channel, RehydrateContent(pending), pending.MessageId, target);
					await CleanupAsync(client, pending, deletePrompt: true);
					await DismissAsync(interaction, editOriginal);

					// The repost is a comment in the root's graph: count it, possibly
					// sweeping the whole graph (repost included) into a thread. The user's
					// content is already safe at this point, so a sweep failure must not
					// surface as "something went wrong" — the edge is recorded before the
					// sweep runs, and the next comment on this root will retry it.
					EffectiveAuthor author = new(pending.AuthorId, pending.AuthorName.Length > 0 ? pending.AuthorName : null);
					try { await flex.RecordCommentAsync(client, binding, channel, repostId, author, root); }
					catch (Exception) { }
					break;
				}
			}
		}
		catch (Exception)
		{
			processing.TryRemove(pending.MessageId, out _);
			await NotifyAsync(interaction, "Something went wrong moving your message — please try again.", editOriginal);
			throw;
		}
	}

	private static async Task<DiscordThreadChannel?> TryGetThreadAsync(DiscordClient client, ulong threadId)
	{
		try { return await client.GetChannelAsync(threadId) as DiscordThreadChannel; }
		catch (NotFoundException) { return null; }
	}

	/// <summary>
	/// Channel reposts need no confirmation — the repost appearing in the channel IS the
	/// feedback. Clears the ephemeral instead of announcing (errors still use NotifyAsync).
	/// </summary>
	private static async Task DismissAsync(DiscordInteraction interaction, bool editOriginal)
	{
		if (!editOriginal)
			return; // deferred update on the prompt, which cleanup just deleted; nothing shows
		try { await interaction.DeleteOriginalResponseAsync(); }
		catch (Exception) { } // an undeletable ephemeral is cosmetic; never fail the resolution over it
	}

	private static Task NotifyAsync(DiscordInteraction interaction, string content, bool editOriginal) =>
		editOriginal
			? interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent(content))
			: FollowupEphemeralAsync(interaction, content);

	private MovableContent RehydrateContent(PendingOrphan pending)
	{
		List<(string Name, string Path)> files = [];
		string dir = PendingDir(pending.MessageId);
		if (Directory.Exists(dir))
		{
			foreach (string path in Directory.GetFiles(dir).OrderBy(p => p))
			{
				string fileName = Path.GetFileName(path);
				int underscore = fileName.IndexOf('_');
				files.Add((underscore >= 0 ? fileName[(underscore + 1)..] : fileName, path));
			}
		}
		return new MovableContent(pending.AuthorId, pending.Content, files, pending.Notes,
			pending.AuthorName.Length > 0 ? pending.AuthorName : null);
	}

	// ---------------------------------------------------------------- timeout / cancel

	public void RecoverPending(DiscordClient client)
	{
		foreach (PendingOrphan pending in db.GetAllPending())
			ScheduleTimeout(client, pending);
	}

	/// <summary>Returns every pending orphan in a channel to its author (used when the channel is unbound).</summary>
	public async Task CancelAllForChannelAsync(DiscordClient client, ulong channelId)
	{
		foreach (PendingOrphan pending in db.GetAllPending().Where(p => p.ChannelId == channelId))
			await CancelAsync(client, pending, dmContent: true);
	}

	private void ScheduleTimeout(DiscordClient client, PendingOrphan pending)
	{
		CancellationTokenSource cts = new();
		if (!timeouts.TryAdd(pending.MessageId, cts))
			return;

		TimeSpan delay = pending.ExpiresAt - DateTimeOffset.UtcNow;
		if (delay < TimeSpan.Zero)
			delay = TimeSpan.Zero;

		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(delay, cts.Token);
				await CancelAsync(client, pending, dmContent: true);
			}
			catch (OperationCanceledException) { }
		});
	}

	private async Task CancelAsync(DiscordClient client, PendingOrphan pending, bool dmContent)
	{
		if (!TryClaim(pending.MessageId))
			return; // a resolution is already in flight

		// The claim can be won late (timeout firing just as a resolution finished);
		// if the DB row is already gone, there is nothing to cancel.
		if (db.GetAllPending().All(p => p.MessageId != pending.MessageId))
		{
			processing.TryRemove(pending.MessageId, out _);
			return;
		}

		if (dmContent)
		{
			MovableContent content = RehydrateContent(pending);
			bool delivered = false;

			try
			{
				DiscordGuild guild = await client.GetGuildAsync(pending.GuildId);
				DiscordMember member = await guild.GetMemberAsync(pending.AuthorId);
				await SendContentBackAsync(b => member.SendMessageAsync(b), pending, content,
					$"Your comment in <#{pending.ChannelId}> wasn't reattached to a post, so here it is back:",
					mention: false);
				delivered = true;
			}
			catch (Exception)
			{
				// DMs closed or member unreachable (synthetic debug identities always
				// land here) — fall back to the channel below.
			}

			if (!delivered)
			{
				try
				{
					DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);
					await SendContentBackAsync(b => channel.SendMessageAsync(b), pending, content,
						$"{AddresseeFor(pending)} I couldn't DM you, so here's your unattached comment back — grab it from here:",
						mention: pending.AuthorName.Length == 0);
					delivered = true;
				}
				catch (Exception)
				{
					// Channel unreachable too (likely a connectivity blip).
				}
			}

			if (!delivered)
			{
				// Never destroy content we failed to return: re-arm and retry later.
				pending = pending with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(config.OrphanTimeoutMinutes) };
				db.UpsertPending(pending);
				if (timeouts.TryRemove(pending.MessageId, out CancellationTokenSource? stale))
				{
					// Cancel before disposing: if this cancellation came from the Cancel
					// button rather than the timer itself, the original timer task is still
					// counting down and would otherwise fire a duplicate retry at the old
					// expiry alongside the one we're about to arm.
					stale.Cancel();
					stale.Dispose();
				}
				processing.TryRemove(pending.MessageId, out _);
				ScheduleTimeout(client, pending);
				return;
			}
		}

		await CleanupAsync(client, pending, deletePrompt: true);
	}

	/// <summary>Returns held content in full fidelity: header, chunked text, notes, files.</summary>
	private static async Task SendContentBackAsync(
		Func<DiscordMessageBuilder, Task<DiscordMessage>> send, PendingOrphan pending, MovableContent content,
		string header, bool mention)
	{
		DiscordMessageBuilder first = new DiscordMessageBuilder().WithContent(header);
		if (mention)
			first.WithAllowedMentions([new UserMention(pending.AuthorId)]);
		await send(first);

		string text = content.Content;
		if (content.Notes.Length > 0)
			text = text.Length > 0 ? $"{text}\n{content.Notes}" : content.Notes;
		foreach (string chunk in Transposer.Chunk(text, 2000))
			await send(new DiscordMessageBuilder().WithContent(chunk));

		if (content.Files.Count > 0)
		{
			DiscordMessageBuilder fileMessage = new();
			List<FileStream> streams = [];
			try
			{
				foreach ((string name, string path) in content.Files)
				{
					FileStream stream = File.OpenRead(path);
					streams.Add(stream);
					fileMessage.AddFile(name, stream);
				}
				await send(fileMessage);
			}
			finally
			{
				foreach (FileStream stream in streams)
					await stream.DisposeAsync();
			}
		}
	}

	private async Task CleanupAsync(DiscordClient client, PendingOrphan pending, bool deletePrompt)
	{
		if (timeouts.TryRemove(pending.MessageId, out CancellationTokenSource? cts))
		{
			cts.Cancel();
			cts.Dispose();
		}

		db.DeletePending(pending.MessageId);

		string dir = PendingDir(pending.MessageId);
		if (Directory.Exists(dir))
		{
			try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
		}

		if (deletePrompt && pending.PromptMessageId != 0)
		{
			try
			{
				DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);
				DiscordMessage prompt = await channel.GetMessageAsync(pending.PromptMessageId);
				await prompt.DeleteAsync();
			}
			catch (NotFoundException) { }
		}

		processing.TryRemove(pending.MessageId, out _);
	}

	// ---------------------------------------------------------------- helpers

	private async Task<List<DiscordMessage>> GetRecentTopLevelPostsAsync(DiscordChannel channel, int take)
	{
		List<DiscordMessage> result = [];
		await foreach (DiscordMessage message in channel.GetMessagesAsync(config.SearchDepth))
		{
			if (message.MessageType != DiscordMessageType.Default)
				continue;
			if (message.Author is null || message.Author.IsBot)
				continue;
			if (!config.IsWhitelisted(channel.Id, message.Author.Id))
				continue;
			result.Add(message);
			if (result.Count >= take)
				break;
		}
		return result;
	}

	private static string Snippet(string? text, int max)
	{
		string clean = (text ?? "").Replace('\n', ' ').Trim();
		return clean.Length <= max ? clean : clean[..max] + "…";
	}

	private static Task RespondEphemeralAsync(DiscordInteraction interaction, string content) =>
		interaction.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource,
			new DiscordInteractionResponseBuilder().WithContent(content).AsEphemeral());

	private static Task FollowupEphemeralAsync(DiscordInteraction interaction, string content) =>
		interaction.CreateFollowupMessageAsync(new DiscordFollowupMessageBuilder().WithContent(content).AsEphemeral());
}
