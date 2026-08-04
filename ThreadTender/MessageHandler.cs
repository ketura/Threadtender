using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// Routes new messages in managed channels: replies get transposed into the target
/// post's thread; non-reply comments from non-whitelisted users go to the orphan flow.
/// </summary>
public class MessageHandler(BotConfig config, Database db, Transposer transposer, OrphanManager orphans, string dataDir)
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

		if (message.MessageType == DiscordMessageType.Reply)
			await HandleReplyAsync(client, message);
		else if (!config.IsWhitelisted(message.ChannelId, message.Author.Id))
			await orphans.InterceptAsync(client, message);
		// else: whitelisted top-level post — exactly what the channel is for.
	}

	private async Task HandleReplyAsync(DiscordClient client, DiscordMessage message)
	{
		DiscordMessage? target = message.ReferencedMessage;

		if (target is null && message.Reference?.Message is not null)
		{
			ulong targetId = message.Reference.Message.Id;

			// The target may be a message we already transposed (deleted, but mapped).
			ulong? knownThread = db.GetTransposedThread(targetId);
			if (knownThread is not null)
			{
				try
				{
					if (await client.GetChannelAsync(knownThread.Value) is DiscordThreadChannel mappedThread)
					{
						await TransposeIntoThreadAsync(client, message, mappedThread);
						return;
					}
				}
				catch (NotFoundException)
				{
					db.DeleteTransposed(targetId); // thread deleted since; fall through
				}
			}

			try { target = await message.Channel!.GetMessageAsync(targetId); }
			catch (NotFoundException) { }
		}

		if (target is null || target.Author is null || target.Author.IsBot)
		{
			// Reply to a vanished message or to one of the bot's own prompts:
			// treat like an orphan so the user can point at the right post.
			await orphans.InterceptAsync(client, message);
			return;
		}

		// Self-reply exemption: a whitelisted author continuing their own post stays top-level.
		if (config.IsWhitelisted(message.ChannelId, message.Author!.Id) && target.Author.Id == message.Author.Id)
			return;

		DiscordMessage anchor = await Transposer.WalkToRootAsync(target);
		DiscordThreadChannel thread = await transposer.GetOrCreateThreadAsync(client, anchor);
		await TransposeIntoThreadAsync(client, message, thread);
	}

	private async Task TransposeIntoThreadAsync(DiscordClient client, DiscordMessage message, DiscordThreadChannel thread)
	{
		// Idempotency against replayed gateway events: already moved → nothing to do.
		if (db.GetTransposedThread(message.Id) is not null)
			return;

		string tempDir = Path.Combine(dataDir, "temp", message.Id.ToString());
		MovableContent content = await MovableContent.CaptureAsync(message, tempDir, config.MaxAttachmentBytes);
		try
		{
			// Repost FIRST, delete only once the copy exists — a failed repost then
			// leaves the original untouched instead of destroying it. The brief
			// double-existence is the price of never losing a user's message.
			await transposer.RepostAsync(client, thread, content, message.Id);
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
