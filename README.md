# Backblaze Monitor

A small Windows tile that watches the **Backblaze** backup service (`bzserv`): status, real upload rate, history graph, remaining files, and an adjustable upload limit.

> **Unofficial tool**, not affiliated with Backblaze, Inc. "Backblaze" is a trademark of its owner. This tool only **reads** Backblaze's local logs and reports, and controls its Windows service.

## What it shows

- **Service status** (running, stopped, ...), with Start / Stop / Restart.
- **Current upload rate**, all threads combined (sum of the bytes completed over 2 minutes), in Mbps or MB/s (click to switch).
- **Graph** over 30 min, 24 h or 7 days (right-click), with a tooltip per time slice.
- **Remaining files and bytes**, with an estimated finish time; **progress per disk** (click the "Remaining" line).
- **Upload limit** by step, or by **time window** (for example 3 Mbps from 8 am to 10 pm), applied through a Windows QoS policy.
- **Alerts**: the service stopped without going through the tile, or nothing has been sent for a threshold while files are waiting.
- Notification-area icon, list of the last files sent, start at sign-in.

## Requirements

- Windows 10 or 11 (64-bit).
- The **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)** (free, from Microsoft).
- The **Backblaze** backup client installed on the machine (it writes the logs and reports the tile reads).

## Installation

1. Download the zip from the [Releases](../../releases) page (or from the Downloads page of dealtimeworlds.com) and unzip it anywhere.
2. Double-click **`BackblazeMonitor.exe`**.

The exe is **not code-signed**, so on first launch Windows SmartScreen may show a blue "Windows protected your PC" box: click *More info* → *Run anyway*. If the zip was downloaded from the web, you can also right-click it → Properties → *Unblock* before unzipping.

## Administrator rights

Starting or stopping the service and setting a limit need administrator rights: Windows shows a **UAC prompt**. For the limit and time windows, the first prompt creates a SYSTEM scheduled task that handles the following ones without asking again. Nothing runs without that prompt.

## Optional: service stopped on purpose by another tool

Both are off unless you set them up; most people never need them.

- **Pause witness.** A tool of yours that stops `bzserv` on purpose can write `C:\ProgramData\BackblazeMonitor\arr-watch.txt` just before (reason on the first line) and delete it before it starts the service again. While it exists, the tile shows an orange "Paused (arr-watch)" with the reason, and no "Backblaze stopped" alert rings.
- **Vacation mode.** Over `ssh`, the tile can ask another machine to hold off that tool for 1 h to 14 days (tray menu, and a "Start + vacation 48 h" button in the Start box). Add two lines to `settings.txt`:

  ```ini
  VacationHost=my-host
  VacationKey=C:\Users\me\.ssh\my_key
  ```

  The remote end must answer `status`, `off` and `on <hours>` (hours from 1, 4, 8, 12, 24, 48, 72, 336) and nothing else: the tile sends nothing outside that list. `ssh.exe` (Windows OpenSSH) must be on the PATH.

## Files written

Next to the exe: `settings.txt` (preferences), `historique.txt` (15-minute slices of the last 7 days), `volumes.txt` (per-disk progress), and `error.log` if something goes wrong. They are specific to each machine; delete them to start from scratch.

## Build and test

Requires the .NET 8 SDK or later.

```powershell
dotnet test BackblazeMonitor.slnx
dotnet publish src/BackblazeMonitor/BackblazeMonitor.csproj -c Release -o publish
```

`publish/BackblazeMonitor.exe` is a single framework-dependent exe. The logic (log parsing, rates, history, schedules, command building) lives in `src/BackblazeMonitor.Core` and is covered by the unit tests; the Windows interface is in `src/BackblazeMonitor`.

## Support

Questions or bugs: **support@dealtimeworlds.com**.

## License

[MIT](LICENSE).
