using DSharpPlus.Entities;

namespace ThreadTender;

/// <summary>
/// A message's transportable payload: text, downloaded attachment files, and notes
/// about anything that could not survive the move (stickers, polls, oversized files).
/// AuthorName is set only for synthetic debug identities (see Masquerade) — when
/// present it overrides ID-based identity resolution for webhook impersonation.
/// </summary>
public record MovableContent(ulong AuthorId, string Content, List<(string Name, string Path)> Files, string Notes, string? AuthorName = null)
{
	private static readonly HttpClient http = new();

	/// <summary>
	/// Captures everything movable from a live message, downloading attachments into
	/// <paramref name="storageDir"/>. Must be called before the message is deleted.
	/// The override parameters carry masqueraded identity / pre-stripped content.
	/// </summary>
	public static async Task<MovableContent> CaptureAsync(DiscordMessage message, string storageDir, long maxAttachmentBytes,
		ulong? authorIdOverride = null, string? authorNameOverride = null, string? contentOverride = null)
	{
		List<string> notes = [];
		List<(string Name, string Path)> files = [];
		string content = contentOverride ?? message.Content ?? "";

		// Forwarded messages carry their payload in snapshots.
		if (message.MessageSnapshots is { Count: > 0 })
		{
			foreach (DiscordMessageSnapshot snapshot in message.MessageSnapshots)
			{
				string? snapContent = snapshot.Message?.Content;
				if (!string.IsNullOrWhiteSpace(snapContent))
					content += (content.Length > 0 ? "\n" : "") + $"➦ *(forwarded)* {snapContent}";
			}
			notes.Add("*(this was a forwarded message; embedded media may not have survived the move)*");
		}

		if (message.Attachments is { Count: > 0 })
		{
			Directory.CreateDirectory(storageDir);
			int i = 0;
			foreach (DiscordAttachment attachment in message.Attachments)
			{
				string name = attachment.FileName ?? $"attachment{i}";
				if (attachment.FileSize > maxAttachmentBytes)
				{
					notes.Add($"*(attachment `{name}` was too large to re-upload: {attachment.Url})*");
					continue;
				}
				try
				{
					string path = Path.Combine(storageDir, $"{i}_{name}");
					await using (Stream download = await http.GetStreamAsync(attachment.Url))
					await using (FileStream file = File.Create(path))
						await download.CopyToAsync(file);
					files.Add((name, path));
				}
				catch (Exception)
				{
					notes.Add($"*(attachment `{name}` could not be preserved: {attachment.Url})*");
				}
				i++;
			}
		}

		if (message.Stickers is { Count: > 0 })
			foreach (DiscordMessageSticker sticker in message.Stickers)
				notes.Add($"*(sticker omitted: {sticker.Name})*");

		if (message.Poll is not null)
			notes.Add("*(a poll was attached and could not be moved)*");

		return new MovableContent(authorIdOverride ?? message.Author?.Id ?? 0, content, files, string.Join("\n", notes), authorNameOverride);
	}

	public void DeleteFiles()
	{
		foreach ((_, string path) in Files)
		{
			try { File.Delete(path); } catch (IOException) { }
		}
	}
}
