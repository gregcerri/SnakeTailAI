# SnakeTail AI

A modern replacement for SnakeTail: a live log viewer for Windows (WPF, .NET 10, Fluent design),
made for the VIU device logs in `C:\SPECIAL_DEVICES\ViuLog\ViuPC-*`.

The app never writes to the log files. It opens them read-only and shares them with the logger,
so the logger can keep writing and rotating them while you read.

## Run

- **For other PCs:** copy `dist\SnakeTailAI` (or `dist\SnakeTailAI-win-x64.zip`) and run `SnakeTailAI.exe`. It is self-contained: .NET is inside the exe, so nothing needs to be installed. Run `build-dist.cmd` to rebuild it (tests, publish, zip).
- `publish\SnakeTailAI.exe` is smaller but needs the .NET 10 Desktop Runtime installed.
- From source: `dotnet run --project src/SnakeTailAI`.
- Command line: `SnakeTailAI.exe <file> [<file> …]` opens those files in tabs.

## Features

- **Live tail** of several files in tabs. Open files from the Devices panel, with Open (Ctrl+O), by drag & drop, or from Recent files.
- **Log rotation**: when `viu.log` is renamed to `viu.log.1`, the rest of the old file is read and the new file is followed. A separator marks the point in the view.
- **Series**: opens `.log.N … .log.1 + .log` as a single, chronological view.
- **Devices panel**: one card per `ViuPC-*` folder showing IP, size, last write and a live dot. The device you used last is at the top; devices you never opened follow in folder order. Use **Live** to follow the active log, **Series** to open all its files, and the arrow to list the files.
- **Filters**: Include and Exclude boxes (separate several terms with `;`), regex / match case / whole word, chips for each level with counters, and thread, source/function and time range fields. You can save filters as presets.
  - Right-click a line for quick filters: only this thread, hide this source, show in context, and more.
- **Clear (Ctrl+L)** empties the screen only; the file is not touched. **Reload (F5)** reads it again.
- **Pause (Space)** freezes the view while new lines are still collected ("+N new lines"). Scrolling up stops following; **End** or **Latest** follows again.
- **Find (Ctrl+F)**: matches are highlighted in the text and on the overview strip. F3 / Shift+F3 jump between them.
- **Bookmarks**: Ctrl+F2 adds one, F2 / Shift+F2 jump. You can also click in the left margin.
- **Raw view** colours each field: time, level, thread (one colour per thread), source, function. **Columns view (Ctrl+T)** shows the same fields in aligned columns.
  - Drag the edge between two column headers to resize; double-click an edge to reset that column; right-click the header to reset all.
  - Thread and Source can be narrowed to nothing to hide them. A Time column wide enough also shows the date.
  - Widths are saved and shared by all tabs.
- **Highlight rules** (palette button): text or regex, colouring the whole line or only the matched text.
- **Overview strip** next to the scrollbar shows errors, warnings, find matches, bookmarks and the visible area. Click it to jump.
- **Timeline**: the toolbar or Devices-panel button merges the logs of several devices into one tab, in time order.
  - Each line shows its device in a coloured column.
  - Filter devices with the Device box (`31; -29` hides 29), or right-click a line → only / hide this device.
  - What was loaded at open is fully sorted. Live lines are sorted within each 0.1 s batch.
- **Message groups (Ctrl+Shift+G)**: a side panel that counts similar messages; numbers are ignored, so "NMatches = 7" and "NMatches = 12" are one group.
  - Groups are sorted by frequency, with an optional "only warnings and errors" switch.
  - Double-click a group to find its lines. Right-click it to show only those lines or hide them.
- **Go to time (Ctrl+G)**: jump to `HH:mm`, `HH:mm:ss` or `yyyy-MM-dd HH:mm:ss`.
- **Alerts** (bell button): when a new line matches a text/regex and/or a level, you get a Windows notification and/or a sound, even if the tab is in the background.
  - Each alert fires at most once every 30 s per tab; the lines in between are counted.
  - Clicking the notification jumps to the line.
  - The tray icon has "Mute alerts".
- Activity sparkline (lines/s), badges with new errors on background tabs, and line details (Ctrl+D).
- Export the shown lines or the selection to a file. Copy with Ctrl+C.
- **SSH terminal**, built into the bottom of the window. Open it from the SSH button in the toolbar or on a ViuPC card (or right-click a card); Ctrl+Shift+T shows or hides the panel.
  - Each device opens in its own session tab, with a status dot, Reconnect and close. Drag the panel's top edge to resize it.
  - It runs the Windows SSH client in Windows Terminal's renderer, so colours, top and vi work.
  - While you type in the terminal, the app's shortcuts are paused, so Ctrl+L, F3 and the like go to the shell.
  - User, port and password are set in Settings → SSH. The password is stored encrypted for the Windows user (DPAPI) and only reaches ssh in that encrypted form, through SSH_ASKPASS.
  - Remove the password to type it in the terminal instead, or switch the SSH buttons off.
- **More room for the log**:
  - **Focus mode (F11)** hides the toolbar, devices panel, filters and status bar.
  - **Ctrl+Shift+F** hides only the filters; while one is active, a slim line says so.
  - **Row spacing** (Settings) can be set to Compact, Normal or Comfortable.
- Dark / light / system theme, always on top, Ctrl + mouse wheel zoom. Tabs and filters are restored at startup.

## Keyboard

| Keys | Action |
| --- | --- |
| Ctrl+O / Ctrl+W | Open file / close tab |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+L | Clear screen |
| F5 | Reload from file |
| Space | Pause / resume (log view focused) |
| End | Follow the end of the file |
| Ctrl+F, F3, Shift+F3 | Find, next, previous |
| Ctrl+E | Focus the Include filter |
| Ctrl+Shift+X | Clear all filters |
| Ctrl+F2, F2, Shift+F2 | Toggle bookmark, next, previous |
| Ctrl+T / Alt+Z / Ctrl+D | Columns view / word wrap / line details |
| Ctrl+B | Devices panel |
| Ctrl+G | Go to time |
| Ctrl+Shift+G | Message groups |
| F11 | Focus mode (only the log) |
| Ctrl+Shift+T | Show / hide the SSH terminal |
| Ctrl+Shift+F | Show / hide the filters |
| Ctrl+C / Ctrl+A | Copy selected lines / select all |
| Ctrl + wheel, Ctrl+Plus/Minus/0 | Zoom |

## Settings

Settings are stored in `%AppData%\SnakeTailAI\settings.json`. Set the `SNAKETAILAI_SETTINGS` environment variable to use another file.

| Setting | Default |
| --- | --- |
| Devices folder | `C:\SPECIAL_DEVICES\ViuLog` |
| Initial load | 20 MB (0 = whole file) |
| Lines kept in memory | 2,000,000 |
| Check interval | 250 ms |

## Project layout

- `src/SnakeTailAI.Core`: parser (VIU and log4net formats), tailer with rotation detection, document and filters, highlight rules, settings. No UI code.
- `src/SnakeTailAI`: the WPF app (WPF-UI Fluent controls, CommunityToolkit.Mvvm). `Controls/LogViewControl.cs` draws only the visible lines, so millions of lines scroll smoothly.
- `tests/SnakeTailAI.Tests`: xUnit tests (`dotnet test`).
- `tools/LogSimulator.ps1`: writes VIU-style lines and rotates the file, to try the viewer. Run it with `powershell -ExecutionPolicy Bypass -File tools\LogSimulator.ps1`.
- `tools/make_icon.py`: builds the icon from the original SnakeTail icon in `tools/icon-source` (retouched: livelier colours, crisper edges, softer shadow).

## Publish

```powershell
dotnet publish src/SnakeTailAI -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publish
```
