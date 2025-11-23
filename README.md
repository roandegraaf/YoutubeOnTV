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

The mod will automatically download what it needs on first run.

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
> tv add subway surfers gameplay
Video added to queue!

> tv add 10 hours of silence
Video added to queue!

> tv queue
Current queue:
1. ytsearch:subway surfers gameplay
2. ytsearch:10 hours of silence

> tv skip
Skipped! (Good call)
```

## Important Notes

- First video might take a few seconds to load (yt-dlp is downloading in the background)
- The mod picks 360p-480p videos automatically to keep things crispy
- Videos are synced for all players

## Credits

Mod by roandegraaf

Built with [YoutubeDLSharp](https://github.com/Lordfirespeed/YoutubeDLSharpThunderstore) and [yt-dlp](https://github.com/yt-dlp/yt-dlp)

Now get back to work. The Company is watching.
