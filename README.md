# ThreadTender

A Discord bot that keeps designated channels focused on one (or a few) authors' top-level
posts, pushing discussion into replies or threads with as little friction as possible.

## Behavior

Every managed channel runs in one of three modes, chosen when you `/bind` it:

### Pure-Thread-Enforcement (the default)

- **Whitelisted authors** post top-level content freely. A whitelisted author replying to
  **their own** post is treated as content continuation and left alone.
- **Any other reply** (whitelisted or not) is deleted and transposed into the target
  post's thread — created on the fly if it doesn't exist yet, unarchived if it fell
  asleep. The bot reposts it under its own identity as `@user:` followed by the text —
  a live transposition is the bot acting on someone's message, and it says so.
  Reply chains are walked up to the nearest whitelisted author's *top-level* post — a
  non-reply, or a self-reply continuation (which is a top-level post in its own right,
  so discussion under it gets its own thread rather than being folded into the chain's
  oldest ancestor). An author's reply to someone else's comment is ordinary discussion:
  it's walked through, counted, and swept like anyone else's.
- **A non-reply comment from a non-whitelisted user** is deleted and held, and the bot
  asks (in-channel, buttons visible to everyone but usable only by the commenter) which
  post they meant to reply to:
  - a select menu of recent top-level posts,
  - or a button opening a modal that accepts a **message ID** (dev-mode users) or a
    **snippet of the post's text** (fuzzy-matched; ambiguous matches offer an ephemeral
    pick list).

  On resolution the comment is transposed exactly like a reply and the prompt deletes
  itself. If the commenter does nothing for 15 minutes (configurable), the prompt is
  removed and the held text + attachments are DMed back to them so nothing is lost.

### Reply-Enforcement

Only one rule: comments from non-whitelisted users must be **replies**. Replies of any
kind are left untouched. A non-reply comment gets the same "which post did you mean?"
flow as pure mode, but resolution puts the comment back **in the channel** as a
pseudo-reply: a webhook repost under the commenter's name and avatar, whose first
line links the target post (webhooks cannot create real reply references, so the
link header stands in for it).

### Flex-Thread-Enforcement

Reply-Enforcement, plus a pressure valve: each top-level post's reply graph (direct
replies, replies to replies, and pseudo-replies from the orphan flow) may accumulate in
the channel until it reaches **either** flex threshold: a message count (default 5) or
a total-line count, where every message is at least one line and each ~60 characters
of text adds another — so five short quips and one wall of text weigh comparably
(line threshold 0, the default, disables the line check entirely). The comment that reaches a threshold triggers the sweep (the
5th comment sweeps at a threshold of 5) — a thread is created on the root post and the **entire
graph** is transposed into it, oldest first. The sweep reposts through webhooks wearing
each original author's name and avatar (it re-presents a conversation that already
happened, so it should read naturally) and ends with one mass ping — "*@a @b — created
discussion thread*" — so everyone whose messages moved knows where they went. Reply
chains among the swept comments are recreated as link headers pointing at each
parent's copy in the thread (direct replies to the root need no marker — sequential
flow implies them). Emoji reactions can't be transposed, but the bot echoes each
reaction emoji onto the moved copy as an invitation to re-react (external emojis it
can't use are skipped).
From then on that post behaves like pure mode: further replies to it (or to
any moved comment) are auto-transposed into its thread. Whitelisted authors'
self-reply continuations are exempt as usual — never counted, never swept.

Attachments are re-uploaded (up to a configurable size cap; oversized ones become links).
Content that bots cannot re-send — stickers, polls — is noted inline as omitted.
Messages already transposed are remembered (SQLite), so a reply aimed at a moved message
still routes to the right thread.

## Setup

### 1. Discord application

1. Create an application + bot at <https://discord.com/developers/applications>.
2. Under **Bot**, enable the **Message Content Intent** (privileged).
3. Invite it with the `bot` **and `applications.commands`** scopes (without the latter
   the setup slash commands never appear — if you invited it with `bot` alone, just
   re-invite with both; no need to kick it first) and these permissions in the managed
   channels:
   View Channel, Send Messages, Send Messages in Threads, Create Public Threads,
   Manage Messages, Manage Threads, Manage Webhooks, Read Message History (the bot
   scans recent posts for the orphan prompt and fetches reply targets), Attach Files
   (returning held attachments in-channel when DMs are closed), Add Reactions
   (echoing a swept message's reactions onto its copy).
   (Permission integer: `326954495040`.)

### 2. Configuration

The settings file carries **only the bot token**; everything else — channel bindings,
whitelists, modes, and all behavioral knobs — lives in the bot's database and is
configured from inside Discord with the slash commands below. (Keys left over from
older configs, like `channels`, are ignored.)

The `settings.json` at the **repo root** is the canonical token file — edit it there
(start from `settings.example.json`):

- **Bare runs**: every build/publish copies the root file into the output's `data/`
  folder, and startup seeds an empty data dir from that copy, so it follows you
  whether your working directory is the project or the output folder. Root wins over
  edits made to the copies.
- **Docker**: compose bind-mounts the root file to `/data/settings.json` directly —
  it *is* the live config; restart the container to pick up edits. (The file must
  exist before `docker compose up`, or Docker creates a directory in its place. It is
  never baked into the image, so tokens stay out of image layers.)

The bot reads `<data dir>/settings.json` at startup — `/data` in Docker, `./data`
relative to the working directory when bare; override with the `DATA_DIR` env var. If
no config is found anywhere, a first run writes a sample and prints its full path:

```json
{
  "token": "PASTE_BOT_TOKEN_HERE"
}
```

The token may instead be supplied via the `DISCORD_TOKEN` environment variable, which
takes precedence.

### 3. Slash commands (requires Manage Server)

- **/bind** — setup wizard: pick a channel, pick a mode (each explained in the menu),
  pick the whitelisted author(s). Running it on an already-bound channel drops you into
  the edit menu instead: switch mode, replace the whitelist (prefilled with the current
  one), or set the flex threshold.
- **/unbind** — pick a bound channel, confirm, done. Pending "which post?" prompts in
  that channel are cancelled and their content returned to the authors; existing
  threads and moved messages stay where they are.
- **/botsettings** — view and edit this server's settings: orphan timeout, thread name
  max length, max attachment size, search depth, fuzzy threshold, and the debug
  masquerade user list. All of these are **per-server** — servers sharing the bot never
  see each other's configuration.

All command flows are ephemeral — no channel clutter. Commands are registered per
guild on startup, so they appear as soon as the bot connects.

### Debug masquerade

For testing flows without a second account: users granted access via **/botsettings →
Debug masquerade users** can prefix a message with `![name]` and the bot will process
it as if that user wrote it. The name is resolved naively — if it matches a guild
member's username or display name, the message is treated as theirs (whitelist checks,
pings, and DMs all target that member); any other name becomes a synthetic user that
exists only in the bot's bookkeeping (it can't be pinged or DMed — timeout returns
fall back to an in-channel post — and the listed debug users can drive its prompts).
The prefix is stripped from transposed copies, but a message the bot decides to leave
in place keeps its prefix visible. An empty list (the default) disables the feature.

### 4. Run

```bash
docker compose up -d --build
```

State (config, SQLite DB, held attachments) lives in `./data`.

## Notes & limitations

- The initial "which post did you mean?" prompt is a normal channel message (Discord
  only allows ephemeral messages as responses to interactions); everything after the
  first click is ephemeral or self-cleaning, and the prompt deletes itself on
  resolution, cancel, or timeout.
- Stickers, polls, and voice-message *presentation* can't be recreated by bots; text and
  files survive, the rest is noted as omitted.
- Webhook reposts suppress re-pings (the original message already fired its
  notifications before deletion).
- Requires DSharpPlus 5.0 nightly (targets .NET 10).
