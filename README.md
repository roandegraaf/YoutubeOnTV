# YoutubeOnTV

Tired of scavenging while your ship's TV plays the same boring broadcast? Spice up your doomed space expeditions by streaming YouTube videos on the company-issued television!

Nothing says "professional employee" quite like watching cat videos while your crewmates are getting eaten by monsters outside.

## What does this mod do?

Simple: it turns your ship's TV into a YouTube player. Queue up videos, rickroll your friends, or watch cooking tutorials while the quota looms over your head. All players see the same content in perfect sync, because shared trauma is better trauma.

**Features:**
- Stream YouTube videos directly to the in-game TV
- Add videos by URL, video ID, or just search by name
- Queue system so the content never stops (unlike your paychecks)
- Everyone sees the same thing - full multiplayer sync
- When all else fails, there's a fallback video (you'll see)

## Installation

Use [r2modman](https://thunderstore.io/c/lethal-company/p/ebkr/r2modman/) or Thunderstore Mod Manager:

1. Search for "YoutubeOnTV"
2. Click "Install"
3. Launch the game
4. That's it!

The mod automatically downloads what it needs (yt-dlp and ffmpeg) on first run.

## How to use

Open the terminal on your ship and type these commands:

**`tv add <something>`**
Add a video to the queue. You can use:
- Full YouTube URL: `tv add https://www.youtube.com/watch?v=dQw4w9WgXcQ`
- Just the video ID: `tv add dQw4w9WgXcQ`
- Search by name: `tv add never gonna give you up`

**`tv queue`**
See what's coming up next

**`tv skip`**
Skip to the next video (when your friend's music taste is questionable)

**`tv clear`**
Empty the entire queue (emergency use only)

## Example

```
> tv add dQw4w9WgXcQ
Added to queue: youtu.be/dQw4w9WgXcQ

> tv add subway surfers gameplay
Added to queue: search: subway surfers gameplay

> tv queue
Now playing: youtu.be/dQw4w9WgXcQ

Videos in queue: 1
1. search: subway surfers gameplay

> tv skip
Skipped the current video.
```

## Important Notes

- Videos are downloaded before they play (480p H.264, a few seconds for a typical music video). The next video in the queue downloads while the current one plays, so it starts right away.
- The first launch downloads yt-dlp and ffmpeg (about 70 MB together) into the plugin folder. yt-dlp keeps itself up to date.
- Every player downloads the video themselves and follows the host's position, so everyone sees the same thing.
- Downloaded videos are cached in the plugin folder (1 GB by default, oldest removed first).
- Don't run this together with TVLoader: both take over the ship TV.

## Configuration

`BepInEx/config/com.roandegraaf.youtubeontv.cfg`:

- `MaxVideoMinutes` (default 60): longest video that can be queued
- `MaxCacheMegabytes` (default 1024): size of the video cache
- `PrefetchNextVideo` (default on): download the next queued video while the current one plays

## Credits

Mod by roandegraaf

Built with [YoutubeDLSharp](https://github.com/Lordfirespeed/YoutubeDLSharpThunderstore) and [yt-dlp](https://github.com/yt-dlp/yt-dlp)

Now get back to work. The Company is watching.
