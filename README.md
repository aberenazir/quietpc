# QuietPC

Calms Windows 11 down: updates only when you say so, a quieter fan, longer battery, no ads or nags. Every change can be undone.

- **Updates on your terms:** pause for as long as you like, no surprise restarts, no driver swaps, no sharing updates with strangers. "Update now" whenever it suits you, plus a monthly reminder for security fixes.
- **Fan & battery:** a plain-English "what's busy right now" list and Quiet mode (no turbo boost on battery). It also pauses indexing on battery, makes virus scans gentler, stops night-time wake-ups, and can hibernate when the lid is closed.
- **Clean up Windows:** turns off ads and suggestions, web results in Start search, Widgets, background tracking and setup nags. Optionally restores the classic right-click menu.
- **Startup apps**, **Remove junk apps** and **Free up space**.
- **A quick panel** from the moon icon by the clock: Quiet mode, Pause updates, Calm my PC (everything at once) and Clean junk files.

## Install

Download **QuietPC-Setup.exe** from the [latest release](https://github.com/aberenazir/quietpc/releases/latest) and double-click it. Windows asks for permission because QuietPC changes system settings.
To remove it: Settings → Apps → QuietPC. It offers to put every setting back.

## Build

`source/` is a .NET Framework 4.8 WinForms app. `release.sh` builds it and publishes a GitHub release. It needs the .NET SDK and `gh`.
