# HydraClaude for macOS

A native menu bar app that runs several Claude Desktop profiles side by side, each with its own
login, history, settings and window. Same idea as the Windows build, rebuilt on AppKit and
SwiftUI: no Electron, no dependencies, about 2 MB.

## Why the Mac needs it too

Claude Desktop on the Mac is a normal Electron app bundle, so the MSIX problems from the Windows
build are gone, but two others take their place:

1. **Launch Services thinks every Claude is the same app.** Opening Claude while one instance is
   running just brings that instance forward, so a second profile never starts. HydraClaude asks
   for a *new instance* every time and passes `--user-data-dir` as a real argv entry.
2. **Claude does not protect a profile from a second copy.** Launch the same `--user-data-dir`
   twice and you get two full instances writing to one folder. HydraClaude knows which instance
   owns which folder and brings that one forward instead.

And, as on Windows, Claude updates itself (through Squirrel, which swaps the whole bundle), so
nothing pins a path: Claude.app is looked up by bundle identifier on every launch, and everything
HydraClaude owns lives outside Claude.app.

## Install

```sh
cd macos
./build.sh
```

That builds a universal (Apple silicon and Intel) `HydraClaude.app` into `~/Applications`,
ad-hoc signed. Pass a folder to build somewhere else: `./build.sh dist`.

Requirements: macOS 14 or later, Claude Desktop, and the Swift toolchain from Xcode or the
Command Line Tools (`xcode-select --install`).

To uninstall, delete `~/Applications/HydraClaude.app`. Profile data stays in
`~/Library/Application Support/ClaudeProfiles` until you delete that too.

## Use

HydraClaude lives in the menu bar. Click the octopus to open any profile straight away; a tick
marks the ones already running, and choosing one of those brings its window forward.

The manager window has the same single card as on Windows:

| Action | What happens |
| --- | --- |
| **Open profile** (or double-click / <kbd>Return</kbd>) | Launches Claude with that profile's `--user-data-dir`, or brings it forward if it is already running |
| **New profile** | Name, data folder, and any extra Electron args |
| **Edit** / **Show in Finder** | Repoint a profile, or reveal its data folder |
| Right-click a row | The above, plus *Create desktop shortcut* and *Remove* |
| **Settings** | Bundle identifier and Claude app override, with a *Detect now* readout |

<kbd>⌘N</kbd> new · <kbd>⌘O</kbd> open · <kbd>⌘E</kbd> edit · <kbd>⇧⌘R</kbd> show in Finder ·
<kbd>⌘⌫</kbd> remove · <kbd>⌘R</kbd> refresh · <kbd>⌘,</kbd> settings · <kbd>Esc</kbd> or
<kbd>⌘W</kbd> hides to the menu bar · <kbd>⌘Q</kbd> quits.

While the window is open HydraClaude has a Dock icon and a place in <kbd>⌘</kbd><kbd>Tab</kbd>;
closed, it is menu bar only. A one-time popover says so the first time. Opening HydraClaude.app
again wakes the parked window rather than starting a second copy.

**Default** launches with no `--user-data-dir`, i.e. your normal Claude, whose data lives in
`~/Library/Application Support/Claude`. *Remove* keeps the folder unless you tick the box, and
then only moves it to the Bin.

### Command line

```sh
HYDRA=~/Applications/HydraClaude.app/Contents/MacOS/HydraClaude
"$HYDRA"              # manager window
"$HYDRA" Work         # launch the "Work" profile directly, which is what shortcuts call
"$HYDRA" --gui        # force the window even with other args
```

An unknown name is created on the spot under `ClaudeProfiles/<name>`, and a running manager
picks the change up straight away. The direct-launch path shows no window and no menu bar icon.

### Desktop shortcuts

A Finder alias cannot carry arguments, so *Create desktop shortcut* makes a tiny
`Claude - <name>.app` on the Desktop whose executable is a two-line shell script calling
`HydraClaude <name>`. It has no Dock icon, needs no signature, and is never quarantined because
it is built locally. It records where HydraClaude.app was when it was made, so recreate
shortcuts if you move HydraClaude.app.

## Config

`~/Library/Application Support/ClaudeProfiles/hydraclaude.json`, indented, keys sorted so
saving never reorders the file:

```json
{
  "appPathOverride" : "",
  "bundleIdentifier" : "com.anthropic.claudefordesktop",
  "profiles" : [
    { "dir" : "", "extraArgs" : "", "lastLaunched" : "", "name" : "Default" },
    { "dir" : "/Users/you/Library/Application Support/ClaudeProfiles/Work",
      "extraArgs" : "", "lastLaunched" : "2026-10-22T10:35:08.125Z", "name" : "Work" }
  ],
  "schema" : 1,
  "trayHintShown" : true
}
```

Same rules as Windows: every key is optional, writes are atomic, a file that fails to parse is
copied aside as `hydraclaude.json.bad` and never blocks a launch, and the first run adopts any
folders already in `ClaudeProfiles`. The Mac replaces the Windows package keys with
`bundleIdentifier` and `appPathOverride`.

`HYDRACLAUDE_THEME=light|dark` forces an appearance without touching the system setting.

## If Claude ever stops launching

The footer reads *"Claude 3.x.y detected"* in the healthy state. If it reads *"Claude not
detected"*, Launch Services cannot find the bundle identifier. Check it with

```sh
mdls -name kMDItemCFBundleIdentifier /Applications/Claude.app
```

and fix it in **Settings**, or point **Claude app override** at the Claude.app you use.

## Layout

| Path | |
| --- | --- |
| `Package.swift` | SwiftPM package: a core library, the app, and tests |
| `Sources/HydraClaudeCore/Store.swift` | config store, seeding, atomic save |
| `Sources/HydraClaudeCore/ClaudeApp.swift` | finding Claude.app by bundle id |
| `Sources/HydraClaudeCore/Arguments.swift` | argv building, extra-args splitting, path normalisation |
| `Sources/HydraClaudeCore/RunningScan.swift` | which folder each running instance owns |
| `Sources/HydraClaudeCore/Launcher.swift` | new instance or bring forward |
| `Sources/HydraClaudeCore/Shortcuts.swift`, `Validation.swift` | desktop shortcuts, profile rules |
| `Sources/HydraClaude/` | menu bar, manager window, sheets, entry point |
| `Resources/Info.plist` | bundle metadata (`LSUIElement`) |
| `build.sh` | builds and assembles `HydraClaude.app` |

Run the tests with `swift test`. The icons (`assets/hydra.icns`, `assets/hydra-menubar*.png`)
come from the same `tools/make_icons.py` as the Windows ones.

## Notes from building this

Checked against Claude Desktop 3.1623 on macOS 27:

- Unlike Windows, the Claude **main** process does carry `--user-data-dir` when it was launched
  with one, so the scan reads two or three argv buffers through `sysctl(KERN_PROCARGS2)` rather
  than every helper. A main process with no flag is the stock instance.
- There is no polling. `NSWorkspace` posts a notification whenever an app launches or quits, and
  the scan runs only then, when the window opens, and when the menu opens.
- `URL.resolvingSymlinksInPath` strips a leading `/private` and gives up on paths that do not
  exist yet, so `/tmp/x` and `/private/tmp/x` compared unequal. Paths are normalised with
  `realpath(3)` on the nearest existing ancestor instead.
- `JSONEncoder` writes keys in a different order on every save unless told to sort them.
- Without a main menu, SwiftUI text fields get no Cut, Copy or Paste.
