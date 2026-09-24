# Testing YoutubeOnTV

Two layers:

| | What it covers | Needs |
|---|---|---|
| `tests/YoutubeOnTV.Tests` | Unit tests for the Unity-free logic (turning typed input into queue entries) | .NET 8 SDK |
| `tests/run-ingame-tests.sh` | The real game: hosting, the TV, the terminal, downloading, playback, picture and sound | Lethal Company on Steam (Linux/Proton), r2modman, .NET 8 SDK |

## Unit tests

```sh
dotnet test tests/YoutubeOnTV.Tests
```

## In-game tests

```sh
tests/run-ingame-tests.sh                      # the isolated YoutubeOnTV-Test profile
tests/run-ingame-tests.sh --profile Gooners    # a real modpack, for compatibility
```

The script builds the mod plus the test harness (`tests/YoutubeOnTV.TestHarness`),
deploys both into an r2modman profile, launches the game through Steam the same way
r2modman does, and waits. The harness:

1. picks LAN mode and hosts a lobby on its own save file (`LCSaveFileYoutubeOnTVTest`,
   deleted after the run, so your saves are never touched),
2. unlocks the Television and switches it on with the networked switch,
3. types `tv` commands into the real ship terminal,
4. checks what happens: the fallback plays, a control clip (`tests/assets/control.mp4`,
   colour bars with a 440 Hz tone) proves the picture and sound probes work, videos
   download and play with a moving, non-black picture and audible sound, TV off/on
   pauses and resumes, adding by id, URL and search, prefetching, skip, end-of-video
   advance, clear, a video that does not exist, and leaving and re-hosting,
5. writes `BepInEx/youtubeontv-test-results.json` and quits.

The report, `LogOutput.log` and Unity's `Player.log` are copied to
`tests/results/<timestamp>-<profile>/`. The exit code is 0 only when every check passed.
A run takes about four minutes. The game window is visible, but its sound is muted in PipeWire/PulseAudio (the audio checks measure inside Unity, so they are unaffected); pass `--audible` to hear it.

The harness is inert unless the game is started with `--youtubeontv-autotest`, and it is
never included in the Thunderstore package.

### Setting up on another machine

- Install the .NET 8 SDK (`dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet` works
  without root).
- Create an r2modman profile named `YoutubeOnTV-Test` with the mod's dependencies
  (LethalNetworkAPI, YoutubeDLSharp, ObjectVolumeController and BepInExPack), or pass
  `--profile` to use an existing one. Any installed copy of YoutubeOnTV is disabled for
  the run and restored afterwards.
- `STEAM_ROOT` and `R2_ROOT` override the default Steam and r2modman locations.

### Multiplayer (experimental)

```sh
tests/run-ingame-tests.sh --multiplayer
```

Hosts through Steam and joins from a second instance started through Proton inside Steam's
runtime container, using its own profile (`YoutubeOnTV-TestClient`) so it downloads into its
own cache. The client must run inside the runtime: launched with plain `proton run`, Proton's
GStreamer cannot load its H.264 decoder and every video "plays" as a 4-second placeholder.

Verified this way so far: a joining client receives the host's state, downloads its own copy,
its `tv add`/`tv skip` reach the host (searches keep a single `ytsearch:` prefix) and TV
on/off follows the host. Starting a joined client mid-video could not be verified because of
the decoder problem above. Two instances on one machine also share an IP address, so they
cannot show YouTube's IP-locked stream URLs; that fix is covered by design. Test with a friend.
