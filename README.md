# Zilch

**Find what eats your disk, and remove it with zilch left behind.**

Zilch is a tiny (~350 KB), fast Windows cleaner. One `.exe`, no installer, nothing to set up.
It shows you where your storage went, uninstalls programs *and every trace they leave*,
and rips out the stuff Windows preinstalls and won't let you remove: Edge, Copilot, Xbox, Bing apps, OneDrive and friends.

> Windows Storage settings hides things, uninstallers leave AppData folders, logs, shortcuts and registry keys behind,
> and Microsoft makes its own apps "non-removable". Zilch shows you all of it and lets you decide.

## Features

| Tab | What it does |
|---|---|
| **Space** | Scans a whole drive in seconds (multi-threaded, raw Win32 file API). A colored bar on top splits the disk into Games, Programs, Windows, App data, Caches, Videos, Pictures, Developer stuff, Disk images and more (Unknown for anything else), and the free space updates live. Drill into folders sorted by size, see the biggest files, **Select all**, **Free up this folder** (deletes everything inside, keeps the folder), **Refresh** just the folder you're in. |
| **Inspector** | Click a color in the bar and every folder and file of that kind is listed here, biggest first, ready to open in Zilch or Explorer, or delete. **← Whole disk** switches to the entire drive, and the big search box finds any file or folder name on it as you type (0.1–0.7 s on 1.5 million files), with the matching letters highlighted in green. **Show relatives** also finds names a few typos away ("mivcrosoft" → Microsoft). |
| **Apps** | Every installed program with its real size. **Uninstall + wipe traces** runs the program's own uninstaller, then hunts down everything it left: AppData (Roaming/Local/LocalLow), ProgramData, Documents, Program Files remnants, Start Menu & Desktop shortcuts, registry keys, scheduled tasks and services. You review the list, Zilch removes it. |
| **Startup** | Everything that starts with Windows: Run keys, Startup folders, Store apps (Advanced adds scheduled tasks and services). Enable/disable with the same switch Task Manager uses, or remove the entry. |
| **Processes** | A task manager that updates every second: CPU, RAM, GPU and VRAM per process (with 60 s graphs for the whole PC), disk I/O, what each process is doing (window title / description), its publisher and path. Color-coded: Windows, services, apps, background, and **unknown** (no signature, no publisher). End a process or its whole tree, show it in Explorer or in Zilch. |
| **Windows Bloat** | Removes preinstalled Store apps for **every user** and deprovisions them so new accounts and feature updates don't bring them back. Removes **Microsoft Edge** (forced system uninstall + blocks reinstall) and **OneDrive**. Optional switch stops Windows silently installing "suggested" apps and turns off Copilot. Restore point button. |
| **Junk** | Temp files, Recycle Bin, thumbnail cache, crash dumps, browser caches, GPU shader caches, Windows Update leftovers, Delivery Optimization, old `Windows.old`, hibernation file, component store cleanup. |
| **Settings** | Storage bar on/off, size, top or bottom, legend sizes and percentages; your own category colors; color squares, colored share bars, risk colors, bold folders; which columns to show; filters (only folders, only files, only things bigger than X MB/GB). |
| **Logs** | Every delete, uninstall, cleanup, ended process and startup change Zilch did, with the full list of paths. Errors Zilch caught land here too. Stored locally, clearable. |

Top right: how much you wiped this session, and for all time. Next to the modes: one **Refresh (F5)** that reloads whatever tab you are on. If another tab (Junk, Apps...) deletes something, the Space bar tells you the scan is out of date.

**Track (what is this?)** on any file, folder or Windows app: where it lives, which program it belongs to (or that nothing installed matches it), what it is doing right now, when it last changed, what it hooks into, and a conclusion: *you can remove it*, *your call*, or *you should not remove it*. Windows apps with cryptic names (`Microsoft.4297127D64EC6`) show their real name (Minecraft Launcher) read from the package itself.

While Zilch deletes, uninstalls or cleans, a progress window with a timestamp locks the app, so nothing can be clicked or closed mid-way. Every error is caught and logged; Zilch keeps running.

## Modes

| | Simple | Advanced | Sudo |
|---|---|---|---|
| Hidden & system files in Space | hidden | shown, system ones locked | shown **and deletable** |
| Delete a whole program folder in Program Files | locked | yes | yes |
| Delete Windows / System32 / Program Files itself | locked | locked | **yes**, after typing `DELETE` |
| Leftover matches | only the sure ones | + "maybe" matches (similar names) | + forced delete |
| Uninstall options | normal | + silent, force remove (skip uninstaller), find leftovers only | same |
| Windows apps listed | known bloat | + optional apps | + system apps & frameworks (Store, Start, ...) |
| Files Windows refuses to delete | skipped | skipped | takes ownership from TrustedInstaller, locked files go on next reboot |

**Sudo mode needs admin and can break Windows.** It is there because it's your PC. Dangerous items are shown in red, every delete asks first, and anything system-level needs you to type a confirmation word. Make a restore point first (Windows Bloat tab).

## Safety rules Zilch always follows

- Nothing is deleted without showing you the exact list first.
- Shared folders are never treated as one app's leftovers: `AppData\Microsoft`, `Common Files`, `Packages`, your Documents/Desktop/Downloads, your OneDrive files folder.
- A publisher folder (say `AppData\Local\Adobe`) is never offered whole. Only the matching app's subfolder is, and the publisher folder is removed only if it ends up empty.
- A folder that holds another installed program is never a "sure" leftover.
- Junctions/symlinks are removed as links. Zilch never follows them into their target.
- If a program's uninstaller is cancelled or fails, Zilch asks before touching its files.
- Your user folder is only searched for dot-folders (`.vscode`, `.gradle`, ...). Everything else there is yours.

## Download

Grab `Zilch.exe` from [Releases](https://github.com/Loseless02/Zilch/releases). Runs on Windows 10/11 x64 (uses .NET Framework 4.8, which ships with Windows).
Windows SmartScreen may warn because the exe isn't code-signed: *More info → Run anyway*. See [VIRUSTOTAL.md](VIRUSTOTAL.md) for the scan report and hashes.

Run it normally for your own stuff. Use **Restart as admin** (top bar) for Program Files, Windows apps for all users, Edge, and Sudo mode.

## Build

Needs the .NET SDK (any recent version builds the .NET Framework 4.8 target).

```powershell
.\build.ps1          # builds, runs the self-tests, puts Zilch.exe in .\dist
```

or by hand:

```powershell
dotnet build src/Zilch -c Release
dotnet run --project tests/Zilch.Tests -c Release     # add -- --scan-c for a timed full C: scan
```

The self-tests only ever create and delete their own `ZilchTest*` dummy files, folders and registry keys.

## Privacy

No telemetry, no network calls, no settings files. The only things Zilch writes are its own history at
`%LOCALAPPDATA%\Zilch\history.log` (Logs tab → *Clear history* deletes it) the all-time wiped counter in `wiped.txt`, and `settings.ini` once you change a setting, all in that folder.

## License

[MIT](LICENSE) © Loseless02
