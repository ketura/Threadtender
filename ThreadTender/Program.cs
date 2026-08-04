using DSharpPlus;
using ThreadTender;

// In Docker the image sets DATA_DIR=/data (the mounted volume); running bare, state
// lives in ./data next to wherever you launched from.
string dataDir = Path.GetFullPath(Environment.GetEnvironmentVariable("DATA_DIR") ?? "data");
Directory.CreateDirectory(dataDir);

// Two instances against the same data dir double every transposition (each process
// reacts to every gateway event, racing past the shared dedup tables). Refuse to start.
// The lock releases automatically when the process dies, even ungracefully.
using FileStream instanceLock = AcquireInstanceLock(dataDir);

static FileStream AcquireInstanceLock(string dataDir)
{
	try
	{
		return new FileStream(Path.Combine(dataDir, ".instance.lock"),
			FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
	}
	catch (IOException)
	{
		Console.Error.WriteLine(
			$"[ThreadTender] Another instance is already running against {dataDir} — " +
			"a second bot would double-post every transposition. Exiting.");
		Environment.Exit(1);
		throw; // unreachable
	}
}

// The build copies the repo-root settings.json into <output>/data. When the active data
// dir has no config yet (`dotnet run` resolves ./data against the project dir, not the
// output dir), seed it from that baked copy instead of dying with the sample-file error.
string settingsPath = Path.Combine(dataDir, "settings.json");
string bakedSettings = Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
if (!File.Exists(settingsPath) && File.Exists(bakedSettings)
	&& Path.GetFullPath(bakedSettings) != Path.GetFullPath(settingsPath))
{
	File.Copy(bakedSettings, settingsPath);
}

using Database db = new(Path.Combine(dataDir, "threadtender.db"));
BotConfig config = BotConfig.Load(settingsPath, db);

Transposer transposer = new(config, db);
FlexThreader flex = new(config, db, transposer, dataDir);
OrphanManager orphans = new(config, db, transposer, flex, dataDir);
MessageHandler messages = new(config, db, transposer, orphans, flex, dataDir);
SetupCommands setup = new(config, db, orphans, flex);

bool recovered = false;

DiscordClientBuilder builder = DiscordClientBuilder
	.CreateDefault(config.Token, DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents)
	.ConfigureEventHandlers(events => events
		.HandleMessageCreated(messages.OnMessageCreatedAsync)
		.HandleComponentInteractionCreated(async (client, e) =>
		{
			// Both handlers filter by custom-ID prefix (tt: orphan prompts; ttb/tte/ttu/ttg: setup).
			await orphans.HandleComponentAsync(client, e);
			await setup.HandleComponentAsync(client, e);
		})
		.HandleModalSubmitted(async (client, e) =>
		{
			await orphans.HandleModalAsync(client, e);
			await setup.HandleModalAsync(client, e);
		})
		.HandleInteractionCreated(setup.HandleInteractionAsync)
		// Joining a new server mid-run registers the setup commands immediately —
		// otherwise they wouldn't appear there until the next restart.
		.HandleGuildCreated(async (client, e) => await setup.RegisterGuildAsync(e.Guild))
		.HandleGuildDownloadCompleted(async (client, _) =>
		{
			// Re-arm timeouts for orphans that were pending when we last shut down,
			// and (re)register the setup slash commands per guild.
			if (!recovered)
			{
				recovered = true;
				orphans.RecoverPending(client);
			}
			await setup.RegisterAsync(client);

			// Rebuild reply-graph knowledge for flex channels: replies posted while the
			// bot was down should still count toward their root's threshold.
			foreach (ChannelBinding binding in config.AllBindings)
			{
				if (binding.Mode != ChannelMode.FlexThread)
					continue;
				try
				{
					if (await client.GetChannelAsync(binding.ChannelId) is { } channel)
					{
						int added = await flex.BackfillGraphAsync(client, binding, channel);
						if (added > 0)
							Console.WriteLine($"[ThreadTender] Backfilled {added} reply-graph edge(s) in channel {binding.ChannelId}.");
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[ThreadTender] Backfill failed for channel {binding.ChannelId}: {ex.Message}");
				}
			}
		}));

DiscordClient client = builder.Build();
await client.ConnectAsync();
await Task.Delay(Timeout.Infinite);
