# Hunt Train Relay

Connects Hunt Helper to Discord. Records the exact moment each mark dies in the
background, then — when you click **End Train Now** — posts a report sorted by
the order things actually died, with each mark's estimated respawn window shown
in every Discord reader's own local time. Also tracks a small, fixed set of
S-rank checks the group actually cares about, and can post an on-demand
scouting report with a code that pastes straight into anyone else's Hunt Helper.

## Install

1. In-game, type `/xlsettings`, go to the **Experimental** tab, and find
   **Custom Plugin Repositories** near the bottom.
2. Paste this into the empty box and click the **+**:
   `https://raw.githubusercontent.com/MusicManBowls/HuntTrainRelay/main/repo.json`
3. Click **Save and Close**.
4. Type `/xlplugins`, search for **Hunt Train Relay**, and click **Install**.

Updates show up as a normal **Update** button in `/xlplugins` — no reinstalling needed.

## Getting started

1. Get a Discord webhook URL: in your server, right-click the channel you want
   reports posted to → Edit Channel → Integrations → Webhooks → New Webhook →
   Copy Webhook URL.
2. Type `/htr` in-game to open the settings window.
3. Go to the **Settings** tab, paste the webhook URL in, and click **Send test
   message** to confirm it posted.
4. Whoever is actively conducting a train ticks **Tracking this train** on the
   **Conductor** tab — everyone else leaves it off.
5. When the train is genuinely finished, click **End Train Now**. Nothing posts
   on its own — this is the only thing that sends a report.

Treat the webhook URL like a password — anyone who has it can post to that channel.

## What each tab does

**Conductor**
- *Tracking this train (records exact kill times)*: turns on background
  tracking. While it's on, the plugin watches Hunt Helper and remembers the
  exact moment each mark flips to dead — it doesn't post anything by itself.
  Only the person actually running Hunt Helper's train recorder should have
  this on.
- *Status*: a live line showing what tracking currently sees.
- *End Train Now*: the only way a report gets sent. Posts every mark tracked
  this train — including ones cleared away mid-train with Hunt Helper's own
  "Remove Dead" — sorted by the order they actually died, plus any S-rank
  check results, then clears everything for the next train. Deliberately
  manual: for multi-expansion trains, auto-firing the moment "everything
  currently tracked is dead" would fire after the *first* leg, not the real end.
- *Reset train tracking now*: clears tracking and S-rank watches without
  posting anything — use if you need to abandon a train.
- *S-Rank Watches*: three quick buttons — Ophioneus, Tyger, and Narrow-rift
  (with a dropdown of its 9 known spawn spots, since it doesn't have one fixed
  location like the other two). Each watch gets Spawned / Didn't Spawn
  checkboxes and shows up on the eventual Discord report either way. Clears
  when the train ends, same as tracking.

**Scout**
- *Send Scouting Report*: posts a paste-able Hunt Helper import code, plus how
  many marks are currently up per expansion — including which specific marks
  were found already dead ("sniped") and which haven't been scouted at all yet.
- *Additional scouts*: credit anyone else whose scouting you folded into this
  report (e.g. they sent you their own Hunt Helper export code privately).

**Marks Slain**
- Live preview of exactly what End Train Now would post right now, in your
  own local time — a way to sanity-check before actually sending it.

**Settings**
- *Send test message*: posts to every **enabled** webhook below.
- *Webhooks*: one row per Discord server or channel — a checkbox to enable/
  disable it (handy for a testing channel you don't want to delete), an
  optional short label, and the URL itself. Add up to 5.
- *Check interval (seconds)*: how often (while "Tracking this train" is on) it
  checks Hunt Helper for changes. 3 seconds is fine for most people.

## Known limitations (by design)

- Only A-rank marks get a computed respawn window. B-rank and S-rank marks
  still get listed if they're in Hunt Helper's train, just without a timer.
- Respawn windows are calculated from when *your own client* saw a mark flip
  to dead, not an exact server-side kill timestamp — accurate to within the
  check interval.
- "Assumed Sniped" (on End Train Now) and "Not yet scouted" (on Scouting
  Report) both check whether a named mark was seen at all, not whether every
  concurrent instance of its zone was checked.
- S-rank watches are a fixed, small set (Ophioneus, Tyger, Narrow-rift) rather
  than a general system — this was deliberately cut back after an earlier,
  more flexible "Rally Flag" design turned out to be more trouble than it was
  worth. A general reusable-location or map-flag system is not planned.

## For whoever maintains this (build from source)

`dotnet build -c Release`, zip `HuntTrainRelay.dll` + `HuntTrainRelay.json`
from `bin\Release\`, attach that zip to a GitHub Release, and update the
version + download links in `repo.json`.
