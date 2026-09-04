# DiskSpace

A disk reclamation tool for Windows. It finds space you can actually get back, tells you what
removing it will cost you, and keeps a record of everything it removed.

Two ways of looking at a full disk, in one window:

- **Scan** — a catalog of things the tool knows how to reclaim: package manager caches, browser
  and Electron caches, Windows' own temp and update caches, leftover data from uninstalled
  software. Every finding names its consequence in plain language before you select it.
- **Explorer** — a fast recursive sizer with a treemap, for the other question: *what is
  actually eating my disk?* No rules involved, so it can see what the catalog cannot.

## Why another cleaner

Most disk cleaners are opaque about the only thing that matters — whether the thing they are
about to delete matters. This one is built around that question:

- Every rule carries a **`WhatBreaks`** description, and the UI shows it next to the finding.
  "Nothing. The next install re-downloads what it needs" is a different decision from "your
  saved logins go with it."
- **Risk levels** decide behavior, not just color. `Safe` regenerates on demand. `Review` is a
  heuristic and is never auto-selected — findings there are quarantined rather than deleted.
  `Advanced` affects system state. `ReportOnly` is surfaced but never touched, so the disk
  arithmetic still adds up once every cache is gone.
- Large things it refuses to delete — `hiberfil.sys`, the component store — are still listed,
  each with the correct way to deal with it (`powercfg /hibernate off`, not `del`).
- Where a tool has its own purge command, that command is preferred over deleting files
  underneath it. A package manager knows its own index; we do not.

## Safety model

The app runs elevated for its whole lifetime, deletes permanently, and does not use the Recycle
Bin. So the guard rails are in the code rather than in the process token:

| Layer | What it does |
| --- | --- |
| `PathGuard` | The last check before any deletion. Protected trees (Program Files, Documents, Downloads, `.ssh`, OneDrive), a minimum path depth of four segments, and a strict allowlist for anything under `%WINDIR%`. Enforced at the deletion boundary, so no rule can opt out. |
| `PathCanonicalizer` | Every check runs on the canonical path, not the string the caller passed — a junction inside a cache cannot walk out into the rest of the disk. |
| Plan / execute split | `CleanupExecutor.Plan` produces the exact list of paths; `ExecuteAsync` accepts only that object. Nothing is removed that was not first shown, and every path is re-checked immediately before it is touched. |
| Quarantine | Orphan findings are archived, verified and closed *before* the original is removed. Restorable for the retention period (7 days by default). |
| Audit log | JSONL, one line per item, flushed as each item goes — a crash halfway through still leaves a complete record. |
| Restart Manager | Turns "the file is in use by another process" into "held by Code.exe", which is something you can act on. |

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build; the published single-file build
  needs only the .NET 10 desktop runtime (or nothing at all, with `-SelfContained`)
- Administrator rights at runtime — several rules reach machine-wide caches

## Build and run

`build.ps1` wraps the dotnet CLI so the flags stay in one place:

```powershell
./build.ps1                 # build (Debug)
./build.ps1 Test            # run the test suite
./build.ps1 Test -Coverage  # ...and write a Cobertura report to artifacts/coverage/
./build.ps1 Run             # build and launch, without a UAC prompt
./build.ps1 Publish         # artifacts/publish/DiskSpace.exe, one file
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
launch. Building with `-p:DevNoElevation=true` swaps in `app.dev.manifest` instead — that is
what `./build.ps1 Run` does. Rules that reach machine-wide caches will report access denied in
that mode, which is the correct behavior, not a bug. Never set it for a build you intend to
hand to someone.

## Project layout

```
src/
  DiskSpace.Core/          No UI. Testable on its own.
    Scanning/              FastDirectoryScanner — parallel, allocation-light directory sizing
    Rules/                 Providers, the rule catalog, installed-software lookup
    Safety/                PathGuard, PathCanonicalizer, SafeDelete
    Cleaning/              CleanupExecutor, the audit log, Restart Manager interop
    Quarantine/            Archive, restore, retention
    Model/                 ByteSize, RiskLevel
  DiskSpace.App/           WinForms, laid out in code — no designer files
    Pages/                 Scan, Explorer, Quarantine, Log, Settings
    Controls/              Treemap, findings tree, nav rail, custom-drawn primitives
    Theme/                 Palette, fonts, glyphs; follows the Windows light/dark setting
tests/
  DiskSpace.Core.Tests/    xUnit; the guard, executor, scanner and quarantine store
```

The rule catalog is where new cleanup knowledge goes: implement `IRuleProvider`, return
`CleanupRule` records, and add it to `RuleCatalog.DefaultProviders`. Rules describe territory
and intent — they never delete and never decide safety, which stays with `PathGuard`.

## Where it keeps things

| | |
| --- | --- |
| Run logs | `%LOCALAPPDATA%\DiskSpace\logs\cleanup-<timestamp>.jsonl` |
| Quarantine | `DiskSpaceQuarantine\` on the roomiest fixed volume that is not the source, or moved aside on the source volume when there is no other |

Both are plain files: JSONL you can read with any text tool, and zip archives you can extract
by hand if this application is not around.

## Tests

```powershell
./build.ps1 Test
```

The suite covers the parts where a mistake is expensive: what `PathGuard` refuses, that the
executor plans before it deletes and logs every item, that quarantine round-trips a folder and
honors retention, and that the scanner records unreadable directories as issues rather than
quietly understating the total.
