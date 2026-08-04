using DSharpPlus;
using ThreadTender;

string dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/data";
Directory.CreateDirectory(dataDir);

BotConfig config = BotConfig.Load(Path.Combine(dataDir, "settings.json"));
using Database db = new(Path.Combine(dataDir, "threadtender.db"));

Transposer transposer = new(config, db);
OrphanManager orphans = new(config, db, transposer, dataDir);
MessageHandler messages = new(config, db, transposer, orphans, dataDir);

bool recovered = false;

DiscordClientBuilder builder = DiscordClientBuilder
	.CreateDefault(config.Token, DiscordIntents.AllUnprivileged | DiscordIntents.MessageContents)
	.ConfigureEventHandlers(events => events
		.HandleMessageCreated(messages.OnMessageCreatedAsync)
		.HandleComponentInteractionCreated(orphans.HandleComponentAsync)
		.HandleModalSubmitted(orphans.HandleModalAsync)
		.HandleGuildDownloadCompleted((client, _) =>
		{
			// Re-arm timeouts for orphans that were pending when we last shut down.
			if (!recovered)
			{
				recovered = true;
				orphans.RecoverPending(client);
			}
			return Task.CompletedTask;
		}));

DiscordClient client = builder.Build();
await client.ConnectAsync();
await Task.Delay(Timeout.Infinite);
