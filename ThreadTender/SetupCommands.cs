using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace ThreadTender;

/// <summary>
/// Guild-admin slash commands for configuring the bot from inside Discord:
/// /bind (setup wizard), /unbind (confirm-guarded removal), /bindsettings (edit an
/// existing binding), /botsettings (global knobs). All flows are ephemeral,
/// component-driven, and stateless — wizard state rides in the component custom IDs
/// (prefixes: ttb = bind wizard, tte = edit binding, ttu = unbind, ttg = globals).
/// </summary>
public class SetupCommands(BotConfig config, Database db, OrphanManager orphans, FlexThreader flex)
{
	private static readonly ChannelMode[] AllModes = [ChannelMode.PureThread, ChannelMode.ReplyOnly, ChannelMode.FlexThread];

	// ---------------------------------------------------------------- registration

	public async Task RegisterAsync(DiscordClient client)
	{
		DiscordApplicationCommand[] commands =
		[
			Command("bind", "Set up ThreadTender on a channel (walks you through mode and whitelist)."),
			Command("unbind", "Remove ThreadTender from a channel."),
			Command("bindsettings", "Edit an existing channel binding (mode, whitelist, flex threshold)."),
			Command("botsettings", "View and edit global ThreadTender settings."),
		];

		// Per-guild registration: instant availability, unlike global commands.
		foreach (DiscordGuild guild in client.Guilds.Values)
		{
			await guild.BulkOverwriteApplicationCommandsAsync(commands);
			Console.WriteLine($"[ThreadTender] Registered setup commands in \"{guild.Name}\" ({guild.Id}).");
		}
	}

	private static DiscordApplicationCommand Command(string name, string description) =>
		new(name, description, defaultMemberPermissions: new DiscordPermissions(DiscordPermission.ManageGuild), allowDMUsage: false);

	// ---------------------------------------------------------------- slash entry points

	public async Task HandleInteractionAsync(DiscordClient client, InteractionCreatedEventArgs e)
	{
		DiscordInteraction interaction = e.Interaction;
		if (interaction.Type != DiscordInteractionType.ApplicationCommand)
			return;

		Console.WriteLine($"[ThreadTender] Slash command /{interaction.Data.Name} from {interaction.User.Username}.");

		switch (interaction.Data.Name)
		{
			case "bind":
				await RespondAsync(interaction, new DiscordInteractionResponseBuilder()
					.WithContent("Which channel should I manage?")
					.AddActionRowComponent(new DiscordChannelSelectComponent("ttb:ch", "Pick a channel…", [DiscordChannelType.Text])));
				break;

			case "unbind":
				await RespondAsync(interaction, BuildChannelPickOrEmpty(interaction, "ttu:pick",
					"Which channel should I stop managing?", "No channels are currently bound — nothing to unbind."));
				break;

			case "bindsettings":
				await RespondAsync(interaction, BuildChannelPickOrEmpty(interaction, "tte:pick",
					"Which binding do you want to edit?", "No channels are currently bound — use /bind first."));
				break;

			case "botsettings":
				await RespondAsync(interaction, BuildGlobalsMenu());
				break;
		}
	}

	private static Task RespondAsync(DiscordInteraction interaction, DiscordInteractionResponseBuilder builder) =>
		interaction.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource, builder.AsEphemeral());

	private static Task UpdateAsync(DiscordInteraction interaction, DiscordInteractionResponseBuilder builder) =>
		interaction.CreateResponseAsync(DiscordInteractionResponseType.UpdateMessage, builder);

	/// <summary>A select of this guild's bound channels, or a plain message when there are none.</summary>
	private DiscordInteractionResponseBuilder BuildChannelPickOrEmpty(DiscordInteraction interaction, string customId, string prompt, string emptyText)
	{
		List<ChannelBinding> bindings = config.AllBindings
			.Where(b => b.GuildId == interaction.Guild?.Id)
			.OrderBy(b => b.ChannelId)
			.Take(25)
			.ToList();

		if (bindings.Count == 0)
			return new DiscordInteractionResponseBuilder().WithContent(emptyText);

		List<DiscordSelectComponentOption> options = bindings
			.Select(b => new DiscordSelectComponentOption(
				$"#{ChannelName(interaction.Guild, b.ChannelId)}", b.ChannelId.ToString(), b.Mode.DisplayName()))
			.ToList();

		return new DiscordInteractionResponseBuilder()
			.WithContent(prompt)
			.AddActionRowComponent(new DiscordSelectComponent(customId, "Pick a channel…", options));
	}

	private static string ChannelName(DiscordGuild? guild, ulong channelId) =>
		guild is not null && guild.Channels.TryGetValue(channelId, out DiscordChannel? channel)
			? channel.Name
			: channelId.ToString();

	// ---------------------------------------------------------------- component routing

	public async Task HandleComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		switch (parts[0])
		{
			case "ttb": await HandleBindComponentAsync(client, e, parts); break;
			case "tte": await HandleEditComponentAsync(client, e, parts); break;
			case "ttu": await HandleUnbindComponentAsync(client, e, parts); break;
			case "ttg": await HandleGlobalsComponentAsync(e, parts); break;
		}
	}

	public async Task HandleModalAsync(DiscordClient client, ModalSubmittedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		string value = e.Values.TryGetValue("value", out IModalSubmission? submission) && submission is TextInputModalSubmission text
			? (text.Value ?? "").Trim()
			: "";

		if (parts is ["tte", "flexmod", var channelIdText])
		{
			ulong channelId = ulong.Parse(channelIdText);
			ChannelBinding? binding = config.GetBinding(channelId);
			if (binding is null)
			{
				await UpdateAsync(e.Interaction, Ending("That binding no longer exists."));
				return;
			}
			if (!int.TryParse(value, out int threshold) || threshold is < 1 or > 100)
			{
				await UpdateAsync(e.Interaction, Ending("The flex threshold needs to be a whole number between 1 and 100 — run /bindsettings to try again."));
				return;
			}
			config.SetBinding(binding with { FlexThreshold = threshold });
			await UpdateAsync(e.Interaction, Ending($"✅ Flex threshold for <#{channelId}> is now **{threshold}**."));
			return;
		}

		if (parts is ["ttg", "mod", var key])
		{
			string? error = TrySetGlobal(key, value);
			await UpdateAsync(e.Interaction, error is null ? BuildGlobalsMenu() : Ending(error));
		}
	}

	/// <summary>A flow-ending response: content only, all components cleared.</summary>
	private static DiscordInteractionResponseBuilder Ending(string content) =>
		new DiscordInteractionResponseBuilder().WithContent(content);

	// ---------------------------------------------------------------- /bind wizard

	/// <summary>Seeds a freshly-flexed channel's reply graph from recent history (after the interaction is answered).</summary>
	private async Task BackfillAfterResponseAsync(DiscordClient client, ulong channelId)
	{
		ChannelBinding? binding = config.GetBinding(channelId);
		if (binding is null || binding.Mode != ChannelMode.FlexThread)
			return;
		try
		{
			if (await client.GetChannelAsync(channelId) is { } channel)
			{
				int added = await flex.BackfillGraphAsync(client, binding, channel);
				if (added > 0)
					Console.WriteLine($"[ThreadTender] Backfilled {added} reply-graph edge(s) in channel {channelId}.");
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ThreadTender] Backfill failed for channel {channelId}: {ex.Message}");
		}
	}

	private async Task HandleBindComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e, string[] parts)
	{
		switch (parts)
		{
			case ["ttb", "ch"]:
			{
				ulong channelId = ulong.Parse(e.Values[0]);
				ChannelBinding? existing = config.GetBinding(channelId);
				if (existing is not null)
				{
					// Already bound → jump into the edit menu instead of re-running the wizard.
					await UpdateAsync(e.Interaction, BuildEditMenu(existing));
					return;
				}
				await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
					.WithContent($"How should I manage <#{channelId}>?")
					.AddActionRowComponent(ModeSelect($"ttb:mode:{channelId}")));
				return;
			}

			case ["ttb", "mode", var channelIdText]:
			{
				string mode = e.Values[0];
				await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
					.WithContent($"**{ChannelModes.Parse(mode).DisplayName()}** it is. Who is allowed to post top-level content in <#{channelIdText}>?")
					.AddActionRowComponent(new DiscordUserSelectComponent($"ttb:wl:{channelIdText}:{mode}", "Select the whitelisted author(s)…", minOptions: 1, maxOptions: 25)));
				return;
			}

			case ["ttb", "wl", var channelIdText, var modeText]:
			{
				ulong channelId = ulong.Parse(channelIdText);
				ChannelMode mode = ChannelModes.Parse(modeText);
				List<ulong> whitelist = e.Values.Select(ulong.Parse).ToList();

				config.SetBinding(new ChannelBinding(channelId, e.Interaction.Guild!.Id, mode, whitelist, FlexThreshold: 5));

				string summary = $"✅ **<#{channelId}>** is now managed with **{mode.DisplayName()}**.\n" +
					$"Whitelisted author(s): {string.Join(", ", whitelist.Select(id => $"<@{id}>"))}";
				if (mode == ChannelMode.FlexThread)
					summary += "\nFlex threshold: **5** (change it with /bindsettings).";
				await UpdateAsync(e.Interaction, Ending(summary));
				await BackfillAfterResponseAsync(client, channelId);
				return;
			}
		}
	}

	private static DiscordSelectComponent ModeSelect(string customId) => new(customId, "Choose a mode…",
		AllModes.Select(m => new DiscordSelectComponentOption(m.DisplayName(), m.ToConfigString(), m.Description())).ToList());

	// ---------------------------------------------------------------- /bindsettings (and /bind on a bound channel)

	private DiscordInteractionResponseBuilder BuildEditMenu(ChannelBinding binding)
	{
		string whitelist = binding.Whitelist.Count == 0 ? "nobody" : string.Join(", ", binding.Whitelist.Select(id => $"<@{id}>"));
		List<DiscordSelectComponentOption> options =
		[
			new("Switch mode", "mode", $"Currently {binding.Mode.DisplayName()}"),
			new("Edit whitelist", "wl", "Replaces the current whitelist outright"),
			new("Set flex threshold", "flex", $"Currently {binding.FlexThreshold} — only used by Flex mode"),
		];
		return new DiscordInteractionResponseBuilder()
			.WithContent(
				$"**<#{binding.ChannelId}>** — **{binding.Mode.DisplayName()}**, whitelist: {whitelist}, " +
				$"flex threshold: {binding.FlexThreshold}.\nWhat would you like to edit?")
			.AddActionRowComponent(new DiscordSelectComponent($"tte:menu:{binding.ChannelId}", "Pick something to edit…", options));
	}

	private async Task HandleEditComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e, string[] parts)
	{
		switch (parts)
		{
			case ["tte", "pick"]:
			{
				ChannelBinding? binding = config.GetBinding(ulong.Parse(e.Values[0]));
				await UpdateAsync(e.Interaction, binding is null ? Ending("That binding no longer exists.") : BuildEditMenu(binding));
				return;
			}

			case ["tte", "menu", var channelIdText]:
			{
				ulong channelId = ulong.Parse(channelIdText);
				ChannelBinding? binding = config.GetBinding(channelId);
				if (binding is null)
				{
					await UpdateAsync(e.Interaction, Ending("That binding no longer exists."));
					return;
				}

				switch (e.Values[0])
				{
					case "mode":
						await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
							.WithContent($"Switch <#{channelId}> (currently **{binding.Mode.DisplayName()}**) to which mode?")
							.AddActionRowComponent(ModeSelect($"tte:mode:{channelId}")));
						return;

					case "wl":
					{
						DiscordUserSelectComponent select = new($"tte:wl:{channelId}", "Select the whitelisted author(s)…", minOptions: 1, maxOptions: 25);
						select.AddDefaultUsers(binding.Whitelist); // prefilled with the current list
						await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
							.WithContent($"Who should be whitelisted in <#{channelId}>? (This **replaces** the current list.)")
							.AddActionRowComponent(select));
						return;
					}

					case "flex":
						DiscordModalBuilder modal = new DiscordModalBuilder()
							.WithTitle("Flex threshold")
							.WithCustomId($"tte:flexmod:{channelId}")
							.AddTextInput(
								new DiscordTextInputComponent("value", $"currently {binding.FlexThreshold}"),
								"Comments before auto-thread", "How many comments a post's reply graph may hold before it's swept into a thread (1–100).");
						await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.Modal, modal);
						return;
				}
				return;
			}

			case ["tte", "mode", var channelIdText]:
			{
				ulong channelId = ulong.Parse(channelIdText);
				ChannelBinding? binding = config.GetBinding(channelId);
				if (binding is null)
				{
					await UpdateAsync(e.Interaction, Ending("That binding no longer exists."));
					return;
				}
				ChannelMode mode = ChannelModes.Parse(e.Values[0]);
				config.SetBinding(binding with { Mode = mode });
				await UpdateAsync(e.Interaction, Ending($"✅ <#{channelId}> is now **{mode.DisplayName()}**."));
				await BackfillAfterResponseAsync(client, channelId); // no-op unless the new mode is flex
				return;
			}

			case ["tte", "wl", var channelIdText]:
			{
				ulong channelId = ulong.Parse(channelIdText);
				ChannelBinding? binding = config.GetBinding(channelId);
				if (binding is null)
				{
					await UpdateAsync(e.Interaction, Ending("That binding no longer exists."));
					return;
				}
				List<ulong> whitelist = e.Values.Select(ulong.Parse).ToList();
				config.SetBinding(binding with { Whitelist = whitelist });
				await UpdateAsync(e.Interaction, Ending(
					$"✅ Whitelist for <#{channelId}> is now: {string.Join(", ", whitelist.Select(id => $"<@{id}>"))}"));
				return;
			}
		}
	}

	// ---------------------------------------------------------------- /unbind

	private async Task HandleUnbindComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e, string[] parts)
	{
		switch (parts)
		{
			case ["ttu", "pick"]:
			{
				ulong channelId = ulong.Parse(e.Values[0]);
				ChannelBinding? binding = config.GetBinding(channelId);
				if (binding is null)
				{
					await UpdateAsync(e.Interaction, Ending("That binding no longer exists."));
					return;
				}
				await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
					.WithContent(
						$"Are you sure you want to delete the **{binding.Mode.DisplayName()}** binding on <#{channelId}>?\n" +
						"Any pending \"which post?\" prompts there will be cancelled and their content returned to the authors. " +
						"Existing threads and moved messages stay where they are.")
					.AddActionRowComponent(
						new DiscordButtonComponent(DiscordButtonStyle.Danger, $"ttu:yes:{channelId}", "Yes, unbind"),
						new DiscordButtonComponent(DiscordButtonStyle.Secondary, "ttu:no", "Keep it")));
				return;
			}

			case ["ttu", "yes", var channelIdText]:
			{
				ulong channelId = ulong.Parse(channelIdText);
				config.RemoveBinding(channelId);
				db.DeleteGraphEdgesForChannel(channelId);
				// Respond before the orphan cancellations — those involve REST calls
				// (DMs, message deletions) that could blow the 3-second ack window.
				await UpdateAsync(e.Interaction, Ending($"✅ Unbound <#{channelId}>."));
				await orphans.CancelAllForChannelAsync(client, channelId);
				return;
			}

			case ["ttu", "no"]:
				await UpdateAsync(e.Interaction, Ending("Left everything as it was."));
				return;
		}
	}

	// ---------------------------------------------------------------- /botsettings

	private static readonly (string Key, string Label, string Description)[] GlobalSettings =
	[
		("orphan_timeout_minutes", "Orphan timeout (minutes)", "How long a \"which post?\" prompt waits before returning the content"),
		("thread_name_max_length", "Thread name max length", "Character cap for auto-created thread names (10–95)"),
		("max_attachment_bytes", "Max attachment size (bytes)", "Attachments bigger than this become links instead of re-uploads"),
		("search_depth", "Search depth", "How many recent messages are scanned for candidates / fuzzy search"),
		("fuzzy_threshold", "Fuzzy threshold", "Minimum match score (0–100) for text-snippet search"),
	];

	private DiscordInteractionResponseBuilder BuildGlobalsMenu()
	{
		IReadOnlyList<ulong> debugUsers = config.DebugImpersonationUserIds;
		string debug = debugUsers.Count == 0 ? "*(disabled)*" : string.Join(", ", debugUsers.Select(id => $"<@{id}>"));

		string content =
			"**Global settings**\n" +
			$"- Orphan timeout: **{config.OrphanTimeoutMinutes}** minutes\n" +
			$"- Thread name max length: **{config.ThreadNameMaxLength}**\n" +
			$"- Max attachment size: **{config.MaxAttachmentBytes}** bytes\n" +
			$"- Search depth: **{config.SearchDepth}**\n" +
			$"- Fuzzy threshold: **{config.FuzzyThreshold}**\n" +
			$"- Debug masquerade users: {debug}\n\n" +
			"Pick a setting to change:";

		List<DiscordSelectComponentOption> options = GlobalSettings
			.Select(s => new DiscordSelectComponentOption(s.Label, s.Key, s.Description))
			.Append(new DiscordSelectComponentOption("Debug masquerade users", "debug", "Who may use the ![name] impersonation prefix"))
			.ToList();

		return new DiscordInteractionResponseBuilder()
			.WithContent(content)
			.AddActionRowComponent(new DiscordSelectComponent("ttg:pick", "Pick a setting…", options));
	}

	private async Task HandleGlobalsComponentAsync(ComponentInteractionCreatedEventArgs e, string[] parts)
	{
		switch (parts)
		{
			case ["ttg", "pick"] when e.Values[0] == "debug":
			{
				DiscordUserSelectComponent select = new("ttg:debug", "Select debug users…", minOptions: 1, maxOptions: 25);
				select.AddDefaultUsers(config.DebugImpersonationUserIds); // prefilled with the current list
				await UpdateAsync(e.Interaction, new DiscordInteractionResponseBuilder()
					.WithContent("Who may use the `![name]` debug masquerade prefix? (This **replaces** the current list.)")
					.AddActionRowComponent(select)
					.AddActionRowComponent(new DiscordButtonComponent(DiscordButtonStyle.Danger, "ttg:debugclear", "Disable (clear the list)")));
				return;
			}

			case ["ttg", "pick"]:
			{
				string key = e.Values[0];
				(string _, string label, string _) = GlobalSettings.First(s => s.Key == key);
				string current = key switch
				{
					"orphan_timeout_minutes" => config.OrphanTimeoutMinutes.ToString(),
					"thread_name_max_length" => config.ThreadNameMaxLength.ToString(),
					"max_attachment_bytes" => config.MaxAttachmentBytes.ToString(),
					"search_depth" => config.SearchDepth.ToString(),
					"fuzzy_threshold" => config.FuzzyThreshold.ToString(),
					_ => "?",
				};
				DiscordModalBuilder modal = new DiscordModalBuilder()
					.WithTitle(label)
					.WithCustomId($"ttg:mod:{key}")
					.AddTextInput(new DiscordTextInputComponent("value", $"currently {current}"), label, "Enter the new value.");
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.Modal, modal);
				return;
			}

			case ["ttg", "debug"]:
				config.SetDebugUsers(e.Values.Select(ulong.Parse));
				await UpdateAsync(e.Interaction, BuildGlobalsMenu());
				return;

			case ["ttg", "debugclear"]:
				config.SetDebugUsers([]);
				await UpdateAsync(e.Interaction, BuildGlobalsMenu());
				return;
		}
	}

	private string? TrySetGlobal(string key, string value)
	{
		switch (key)
		{
			case "max_attachment_bytes":
				if (!long.TryParse(value, out long bytes) || bytes < 0)
					return "That needs to be a non-negative whole number of bytes — run /botsettings to try again.";
				config.SetGlobal(key, bytes.ToString());
				return null;

			case "orphan_timeout_minutes" or "thread_name_max_length" or "search_depth" or "fuzzy_threshold":
			{
				(int min, int max) = key switch
				{
					"orphan_timeout_minutes" => (1, 10080),
					"thread_name_max_length" => (10, 95),
					"search_depth" => (10, 1000),
					"fuzzy_threshold" => (0, 100),
					_ => (0, 0),
				};
				if (!int.TryParse(value, out int number) || number < min || number > max)
					return $"That needs to be a whole number between {min} and {max} — run /botsettings to try again.";
				config.SetGlobal(key, number.ToString());
				return null;
			}

			default:
				return "Unknown setting.";
		}
	}
}
