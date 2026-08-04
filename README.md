# ThreadTender

A Discord bot that keeps designated channels focused on one (or a few) authors' top-level
posts, pushing all discussion into threads with as little friction as possible.

## Behavior

In each configured channel:

- **Whitelisted authors** post top-level content freely. A whitelisted author replying to
  **their own** post is treated as content continuation and left alone.
- **Any other reply** (whitelisted or not) is deleted and transposed into the target
  post's thread — created on the fly if it doesn't exist yet, unarchived if it fell
  asleep. The repost is done through a webhook wearing the original author's display name
  and avatar, so the thread reads naturally. The author is then pinged in the thread.
  Reply chains are walked to their root, so replying to a self-reply continuation still
  lands in the root post's thread.
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

Attachments are re-uploaded (up to a configurable size cap; oversized ones become links).
Content that bots cannot re-send — stickers, polls — is noted inline as omitted.
Messages already transposed are remembered (SQLite), so a reply aimed at a moved message
still routes to the right thread.

## Setup

### 1. Discord application

1. Create an application + bot at <https://discord.com/developers/applications>.
2. Under **Bot**, enable the **Message Content Intent** (privileged).
3. Invite it with the `bot` scope and these permissions in the managed channels:
   View Channel, Send Messages, Send Messages in Threads, Create Public Threads,
   Manage Messages, Manage Threads, Manage Webhooks.
   (Permission integer: `377957256192`.)

### 2. Configuration

First run writes a sample `data/settings.json`:

```json
{
  "token": "PASTE_BOT_TOKEN_HERE",
  "channels": {
    "123456789012345678": { "whitelisted_author_ids": [234567890123456789] }
  },
  "orphan_timeout_minutes": 15,
  "thread_name_max_length": 80,
  "max_attachment_bytes": 26214400,
  "search_depth": 100,
  "fuzzy_threshold": 70
}
```

Channel keys are channel IDs (as strings). The token may instead be supplied via the
`DISCORD_TOKEN` environment variable, which takes precedence.

### 3. Run

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
