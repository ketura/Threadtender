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
/// select, or message ID / text snippet via modal), then transposes on resolution.
/// Pending comments survive restarts; unresolved ones are DMed back on timeout.
/// </summary>
public class OrphanManager(BotConfig config, Database db, Transposer transposer, string dataDir)
{
	private readonly ConcurrentDictionary<ulong, CancellationTokenSource> timeouts = new();
	private readonly ConcurrentDictionary<ulong, byte> processing = new();

	/// <summary>Claims exclusive handling of a pending orphan (resolution vs. timeout race).</summary>
	private bool TryClaim(ulong pendingId) => processing.TryAdd(pendingId, 0);

	private string PendingDir(ulong messageId) => Path.Combine(dataDir, "pending", messageId.ToString());

	// ---------------------------------------------------------------- intake

	public async Task InterceptAsync(DiscordClient client, DiscordMessage message)
	{
		// Gateway events are at-least-once; a replayed event for an already-intercepted
		// message must not touch the existing pending record or its stored files.
		if (db.GetAllPending().Any(p => p.MessageId == message.Id))
			return;

		MovableContent content = await MovableContent.CaptureAsync(message, PendingDir(message.Id), config.MaxAttachmentBytes);

		// Persist BEFORE deleting — from here on, the content can always be recovered.
		PendingOrphan pending = new(message.Id, message.ChannelId, message.Channel!.Guild.Id, message.Author!.Id,
			content.Content, content.Notes, 0,
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
				$"<@{message.Author!.Id}> — this channel keeps discussion in threads, so I've set your message aside for a moment. " +
				$"**Which post were you replying to?** Pick one below, or give me a message ID / a snippet of its text. " +
				$"You have {config.OrphanTimeoutMinutes} minutes; after that I'll DM your text back to you so nothing is lost.")
			.WithAllowedMentions([new UserMention(message.Author.Id)]);

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

		if (e.User.Id != pending.AuthorId)
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

		DiscordThreadChannel thread;
		try
		{
			DiscordMessage anchor = await Transposer.WalkToRootAsync(target);
			thread = await transposer.GetOrCreateThreadAsync(client, anchor);
			await transposer.RepostAsync(client, thread, RehydrateContent(pending), pending.MessageId);
		}
		catch (Exception)
		{
			processing.TryRemove(pending.MessageId, out _);
			await NotifyAsync(interaction, "Something went wrong moving your message — please try again.", editOriginal);
			throw;
		}

		await CleanupAsync(client, pending, deletePrompt: true);
		await NotifyAsync(interaction, $"✅ Moved to {thread.Mention}.", editOriginal);
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
		return new MovableContent(pending.AuthorId, pending.Content, files, pending.Notes);
	}

	// ---------------------------------------------------------------- timeout / cancel

	public void RecoverPending(DiscordClient client)
	{
		foreach (PendingOrphan pending in db.GetAllPending())
			ScheduleTimeout(client, pending);
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
				// DMs closed or member unreachable — fall back to the channel below.
			}

			if (!delivered)
			{
				try
				{
					DiscordChannel channel = await client.GetChannelAsync(pending.ChannelId);
					await SendContentBackAsync(b => channel.SendMessageAsync(b), pending, content,
						$"<@{pending.AuthorId}> I couldn't DM you, so here's your unattached comment back — grab it from here:",
						mention: true);
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
					stale.Dispose();
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
