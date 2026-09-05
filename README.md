# DiskSpace

A disk reclamation tool for Windows. It finds space you can actually get back, tells you what
removing it will cost you, and keeps a record of everything it removed.

Three ways of looking at a full disk, in one window:

- **Scan**: a catalog of things the tool knows how to reclaim: package manager caches, browser
  and Electron caches, Windows' own temp and update caches, leftover data from uninstalled
  software. Every finding names its consequence in plain language before you select it.
- **Programs**: what installed software actually occupies. Registry entries, Store apps, the
  things unpacked into your profile that Windows never recorded, and the parts of Windows itself
  that no application owns. It measures and hands any removal to the program's own uninstaller.
- **Explorer**: a fast recursive sizer with a treemap, for the other question: *what is
  actually eating my disk?* No rules involved, so it can see what the catalog cannot. It paints
  the first levels of a whole drive in about a quarter of a second and fills in the rest while
  you look at it.

## Why another cleaner

Most disk cleaners are opaque about the only thing that matters, which is whether the thing
they are about to delete matters. This one is built around that question:

- Every rule carries a **`WhatBreaks`** description, and the UI shows it next to the finding.
  "Nothing. The next install re-downloads what it needs" is a different decision from "your
  saved logins go with it."
- **Risk levels** decide behavior, not just color. `Safe` regenerates on demand. `Review` is a
  heuristic and is never auto-selected, so findings there are quarantined rather than deleted.
  `Advanced` affects system state. `ReportOnly` is surfaced but never touched, so the disk
  arithmetic still adds up once every cache is gone.
- Large things it refuses to delete (`hiberfil.sys`, the component store) are still listed,
  each with the correct way to deal with it (`powercfg /hibernate off`, not `del`).
- Where a tool has its own purge command, that command is preferred over deleting files
  underneath it. A package manager knows its own index; we do not.

## Measuring a whole drive

`C:\` on the machine this was written on holds 183,000 folders and takes about a minute to walk.
Waiting a minute at an empty window to find out where the space went is the thing the Explorer
page exists to avoid, so it does not wait:

- The first two levels are listed before anything else, which puts a real tree on screen in
  roughly 250 ms. Every total then climbs toward the truth as the walk continues, and a number
  that is still moving is drawn with a `~` so it never reads as settled.
- Opening a folder moves that subtree to the front of the queue, so the part you are looking at
  finishes first.
- Rows are sorted by size when they are opened and deliberately not re-sorted while you watch,
  because a row moving out from under the pointer is worse than a row in the wrong place. There
  is one settling sort when the scan ends, and it is offered as a button instead if the tree is
  in use at that moment.
- Cancelling keeps what was measured, correctly marked incomplete, rather than throwing it away.

Scanning the same root again reads the previous measurement back first, so the tree, the treemap
and the percentages are on screen in about a quarter of a second. Those numbers are drawn with a
`≈` and the status bar says how old they are, because a remembered measurement is not a
measurement. A full walk runs behind it and replaces each value in place.

What the cache deliberately does *not* do is let the scan skip work. A folder's timestamp moves
when something is added to it, removed from it or renamed inside it, but not when a file already
inside it is written to, and not for anything that happens a level further down. So "the
timestamp has not moved" cannot mean "this subtree is unchanged", and every value the tool
finally shows was measured during that run. There is a **Trust folders whose timestamp has not
changed** setting for people who want the trade anyway; it is off by default, it says what it
gets wrong, and anything adopted under it keeps the `≈` for the rest of the scan.

## Safety model

The app runs elevated for its whole lifetime, deletes permanently, and does not use the Recycle
Bin. So the guard rails are in the code rather than in the process token:

| Layer | What it does |
| --- | --- |
| `PathGuard` | The last check before any deletion. Protected trees (Program Files, Documents, Downloads, `.ssh`, OneDrive), a minimum path depth of four segments, and a strict allowlist for anything under `%WINDIR%`. Enforced at the deletion boundary, so no rule can opt out. |
| `PathCanonicalizer` | Every check runs on the canonical path, not the string the caller passed, so a junction inside a cache cannot walk out into the rest of the disk. |
| Plan / execute split | `CleanupExecutor.Plan` produces the exact list of paths; `ExecuteAsync` accepts only that object. Nothing is removed that was not first shown, and every path is re-checked immediately before it is touched. |
| Quarantine | Orphan findings are archived, verified and closed *before* the original is removed. Restorable for the retention period (7 days by default). |
| Audit log | JSONL, one line per item, flushed as each item goes, so a crash halfway through still leaves a complete record. |
| Restart Manager | Turns "the file is in use by another process" into "held by Code.exe", which is something you can act on. |

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the published single-file build
  needs only the .NET 10 desktop runtime (or nothing at all, with `-SelfContained`)
- Administrator rights at runtime, because several rules reach machine-wide caches

## Build and run

`build.ps1` wraps the dotnet CLI so the flags stay in one place:

```powershell
./build.ps1                 # build (Debug)
./build.ps1 Test            # run the test suite
./build.ps1 Test -Coverage  # ...and write a Cobertura report to artifacts/coverage/
./build.ps1 Run             # build and launch, without a UAC prompt
./build.ps1 Publish         # artifacts/publish/DiskSpace.exe, one file
./build.ps1 Installer       # artifacts/installer/DiskSpace-<version>-win-x64-setup.exe
./build.ps1 Version         # show the resolved version
./build.ps1 All             # clean, build, test, publish
```

Useful switches: `-Configuration Release`, `-SelfContained` (bundle the runtime), `-Elevated`
(keep the `requireAdministrator` manifest when running), `-Runtime win-arm64`.

Or use the CLI directly:

```powershell
dotnet build DiskSpace.slnx
dotnet test DiskSpace.slnx
dotnet run --project src/DiskSpace.App -p:DevNoElevation=true
```

### Running during development

The shipping manifest requests `requireAdministrator`, which means a UAC prompt on every
launch. Building with `-p:DevNoElevation=true` swaps in `app.dev.manifest` instead, which is
what `./build.ps1 Run` does. Rules that reach machine-wide caches will report access denied in
that mode, which is the correct behavior, not a bug. Never set it for a build you intend to
hand to someone.

## Installer

`./build.ps1 Installer` publishes the app and then compiles `installer/DiskSpace.iss` with
[Inno Setup 6](https://jrsoftware.org/isdl.php), which the script finds on PATH or in the usual
per-user and per-machine locations. The result is a single setup executable in
`artifacts/installer/`.

What the installer does beyond copying a file:

- **Upgrades in place.** A second install of the same product replaces the first: same
  directory, same Start menu entry, same desktop-icon choice, and one entry in Add/Remove
  Programs rather than two. The final page says which version is being replaced.
- **Refuses to silently downgrade.** Installing an older build over a newer one asks first,
  and in silent mode (`/SILENT`) it aborts rather than rolling back a machine unattended.
- **Closes a running copy.** Restart Manager shuts the app down so an upgrade cannot fail on
  a locked executable.
- **Checks for the .NET 10 Desktop Runtime** and offers the download page if it is missing.
  Publishing with `-SelfContained` bundles the runtime and skips the check.
- **Leaves your data alone on uninstall.** Removing the app offers to delete the cleanup logs
  and never touches quarantined items, which may still be the only copy of something.

## Releasing

Pushing a tag of the form `vX.Y.Z` (or `vX.Y.Z-suffix`) runs
[`release.yml`](.github/workflows/release.yml), which stamps that version into a throwaway
checkout, builds and tests it, compiles the installer, and publishes it as a GitHub Release
with the setup executable attached:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

The tag is the only source of truth for a release's version; nothing is committed back to
`Directory.Build.props`. A `-suffix` tag is published as a pre-release. The workflow can also be
re-run by hand from the Actions tab against an already-pushed tag.

## Checking for updates

The app asks GitHub for the latest release once a day at startup, and from a **Check now**
button on the Settings page. A release newer than the running build shows a small dialog with
the option to open its download; declining is remembered so the same version is not offered
again. Both the check and the reminder can be turned off in Settings. The comparison and the
GitHub call live in `DiskSpace.Core.Updates`; the dialog and the throttling live in
`DiskSpace.App.Updates`.

## Versioning

The version is written down in exactly one place, `Directory.Build.props`:

```xml
<VersionPrefix>0.1.0</VersionPrefix>
<VersionSuffix></VersionSuffix>
```

Everything else derives from it, so the assembly, the installer filename, the Add/Remove
Programs entry and the upgrade comparison cannot disagree:

| | |
| --- | --- |
| `AssemblyVersion` / `FileVersion` | `0.1.0.0` |
| `InformationalVersion` | `0.1.0+<git sha>`, stamped by a target in `Directory.Build.props` |
| Installer | `DiskSpace-0.1.0-win-x64-setup.exe` |

Bump it with the build script rather than by hand, which validates the format and keeps the
prefix and suffix consistent:

```powershell
./build.ps1 Version -SetVersion 0.2.0
./build.ps1 Version -SetVersion 0.2.0-beta.1
```

Pre-release labels are for display only. The Windows version resource and the installer's
upgrade check use the three-part number, so `0.2.0-beta.1` compares as `0.2.0`.

## Project layout

```
src/
  DiskSpace.Core/          No UI. Testable on its own.
    Scanning/              DirectoryReader: the one directory walker, shared by both scanners
                           FastDirectoryScanner: measures a tree and returns once
                           ProgressiveScanner: publishes the tree and keeps filling it in
    Caching/               Remembered scans: the binary tree file, the index, eviction
    Rules/                 Providers, the rule catalog, installed-software lookup
    Programs/              Program providers, the program catalog, the uninstall handoff
    Safety/                PathGuard, PathCanonicalizer, SafeDelete
    Cleaning/              CleanupExecutor, the audit log, Restart Manager interop
    Quarantine/            Archive, restore, retention
    Settings/              AppSettings: what the app remembers between runs
    Updates/               GitHubUpdateChecker: asks GitHub for the latest release
    Model/                 ByteSize, RiskLevel
  DiskSpace.App/           WinForms, laid out in code, no designer files
    Pages/                 Scan, Programs, Explorer, Quarantine, Log, Settings
    Controls/              Treemap, size tree, findings tree, nav rail, progress strip
    Dialogs/               Cleanup confirmation, update-available prompt
    Theme/                 Palette, fonts, glyphs, window icon; follows the Windows light/dark setting
    Updates/               AppUpdateManager: throttling and the skip/remind decision
    Assets/                DiskSpace.ico, shared by the executable, its windows and the installer
tests/
  DiskSpace.Core.Tests/    xUnit; the guard, executor, scanner and quarantine store
installer/
  DiskSpace.iss            Inno Setup script
```

The rule catalog is where new cleanup knowledge goes: implement `IRuleProvider`, return
`CleanupRule` records, and add it to `RuleCatalog.DefaultProviders`. Rules describe territory
and intent. They never delete and never decide safety, which stays with `PathGuard`.

`ProgramCatalog` works the same way for installed software: implement `IProgramProvider`, return
`InstalledProgram` records naming where a program keeps its files, and add it to
`ProgramCatalog.DefaultProviders`. Overlapping claims are expected and are resolved by the
catalog, not the provider: the most specific claim on a path wins, and a program whose folder
contains another program's is measured around it, so shared bytes are counted once.

## What the Programs page cannot tell you

An install size is a floor, and the page says so rather than implying its numbers add up to the
volume. Windows keeps part of every installed product outside the product's own folder: in the
component store, in the MSI cache, in the driver store. Nothing ties those bytes back to the
product that caused them, so they are listed as the Windows components they are rather than
divided up by guesswork.

Two consequences worth knowing:

- **WinSxS overstates itself.** Most of the component store is hard links to files that also live
  in `System32`, and this tool counts a hard-linked file once per path it appears under. The
  measured figure is real disk-as-addressed, not disk-as-occupied.
- **Some installers record nothing.** Where there is no install location and no uninstaller path
  to derive one from, the size shown is the number the installer claimed, drawn with a `~`.

## Where it keeps things

| | |
| --- | --- |
| Run logs | `%LOCALAPPDATA%\DiskSpace\logs\cleanup-<timestamp>.jsonl` |
| Settings | `%LOCALAPPDATA%\DiskSpace\settings.json` |
| Scan cache | `%LOCALAPPDATA%\DiskSpace\cache\` |
| Quarantine | `DiskSpaceQuarantine\` on the roomiest fixed volume that is not the source, or moved aside on the source volume when there is no other |

The first three are plain files: JSONL you can read with any text tool, indented JSON for the
settings, and zip archives you can extract by hand if this application is not around.

The scan cache is the one exception, and the only place the project departs from that. A tree is
written as a compact binary file, because a million-node tree is roughly 30 MB in this format
against well over 150 MB of JSON, and because `JsonSerializerOptions.MaxDepth` is 64 while the
`node_modules` and package-cache trees this tool exists to find go far deeper than that. It is
also the only file here that is purely derived: delete the folder and nothing is lost but the
head start. Everything else records something that actually happened.

## Tests

```powershell
./build.ps1 Test
```

The suite covers the parts where a mistake is expensive: what `PathGuard` refuses, that the
executor plans before it deletes and logs every item, that quarantine round-trips a folder and
honors retention, and that the scanner records unreadable directories as issues rather than
quietly understating the total.

Three of them are load-bearing enough to name:

- **`Produces_the_same_totals_as_the_blocking_scanner`.** The two scanners aggregate differently
  on purpose: one rolls up once at the end, the other credits every ancestor as it goes. Keeping
  both is what makes this a real comparison rather than a tautology, and it guards the numbers a
  deletion is planned from.
- **`A_file_that_grew_in_place_is_picked_up`.** Rewrites a file without touching any folder's
  timestamp, then asserts the new size. This test is the specification of what the cache promises.
- **`Unchanged_directories_keep_their_node_objects`.** The tree view holds those objects in its
  rows; replacing them instead of updating them would break selection and the treemap silently.

## License

Apache License 2.0. See [LICENSE](LICENSE).
