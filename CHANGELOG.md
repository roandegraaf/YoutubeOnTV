# Changelog

## [0.2.11] - 2026-08-27

### Changed
- Video is now requested at 480p instead of 720p60, matching the TV screen's actual resolution. The 720p stream was five times the download for no visible gain and would not open at all.

## [0.2.10] - 2026-08-27

### Changed
- Fixed videos playing with picture but no sound. YouTube is retiring the pre-muxed formats the mod relied on, and yt-dlp was quietly falling back to a video-only stream. When no combined format exists the TV now plays the matching audio stream alongside the video.
- Video is now requested as H.264 instead of whatever codec ranked best, since Unity cannot be relied on to decode AV1.
- Fixed the fallback video not being found when the plugin folder is not named exactly "YoutubeOnTV" (r2modman installs it as "<Author>-YoutubeOnTV")

## [0.2.8] - 2026-08-23

### Changed
- Fixed the queue not advancing to the next video automatically
- Videos are now removed from the queue when they start playing
- Fixed `tv skip` refusing to skip when the queue was empty
- Fixed clients resolving their own videos instead of following the host

## [0.2.7] - 2025-11-23

### Changed
- Republished with the final client TV state sync changes (0.2.6 was packaged from a build made before them)

## [0.2.6] - 2025-11-23

### Changed
- Fixed bug where video played in a loop after ending

## [0.2.5] - 2025-11-23

### Changed
- Small bugfixes

## [0.2.4] - 2025-11-23

### Changed
- Oops, syncing for all clients was NOT working, but good news, it IS working now! 

## [0.2.3] - 2025-11-23

### Changed
- Renamed full project to YoutubeOnTV
- Because of the rename, it could be that the mod needs to be reinstalled. If that is the case, please delete the mod and download it again.

## [0.2.2] - 2025-11-22

### Changed
- Updated YouTube URL handling
- Fixed bug where turning off and on the TV would cause the video to get stuck

## [0.2.1] - 2025-11-22

### Changed
- Updated README

## [0.2.0] - 2025-11-22

### Changed
- Replaced bundled yt-dlp.exe with YoutubeDLSharp library dependency
- yt-dlp.exe now downloads automatically on first run (no manual installation needed)
- Cleaner mod distribution (no longer bundling .exe files with the mod)

### Added
- Added Lordfirespeed-YoutubeDLSharp-1.1.0 as a dependency

## [0.1.0] - 2025-11-22

### Added
- Initial release
- Terminal commands for TV control (tv add, tv queue, tv skip, tv clear)
- YouTube video streaming to in-game TVs
- Network synchronization for multiplayer
- Video queue management
- yt-dlp integration for video downloading
- Fallback video support
- TerminalApi integration
