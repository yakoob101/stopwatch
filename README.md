# Stopwatch

A tiny Windows stopwatch with a pixel look: live system clock, millisecond display, laps, and an
always-on-top pin. One C# file talking straight to Win32/GDI. No frameworks, no installer, no
dependencies, no font files — the 5x7 pixel font is built in.

## Run

`Stopwatch.exe` (~18 KB) — uses the .NET Framework that ships with Windows 10/11.

## Build

Double-click `build.cmd` (uses the C# compiler built into Windows).

## Controls

| Action | Button | Key |
|---|---|---|
| Start / pause | START / PAUSE | Space or Enter |
| Reset | RESET | R |
| Lap | LAP | L |
| Always on top | PIN | T |
| Cycle colours | swatch (top right) | — |
| Set the reading (paused) | click the time | E |

Laps show split and total, newest first. The window scales from 360x280 to full screen; the pixel
font only uses whole-pixel scales, so it stays sharp at any size.

To set a reading, pause first, then click the big time or press E. Type seconds, optionally with up
to three decimals (`45`, `45.5`, `0.045`): Backspace deletes, Delete clears, Esc cancels, and
Space or Enter applies the value and starts the watch. Setting a reading clears the laps, since the
old splits no longer line up. Start resumes from the value you typed.

Closing the app saves the current reading; it reopens paused at that time. Reset clears it.
The value lives under `HKCU\Software\PixelStopwatch`.

## Footprint

~18 KB on disk, ~12 MB private RAM, 0% CPU when idle, ~3% of one core while showing live
milliseconds at the default size. GDI objects stay flat across resizes.
