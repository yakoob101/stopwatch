# Stopwatch

A tiny Windows stopwatch: live system clock, millisecond display, laps, and an always-on-top toggle.
One C# file talking straight to Win32/GDI. No frameworks, no installer, no dependencies.

## Run

`Stopwatch.exe` (16 KB) — uses the .NET Framework that ships with Windows 10/11.

## Build

Double-click `build.cmd` (uses the C# compiler built into Windows).

## Controls

| Action | Button | Key |
|---|---|---|
| Start / pause | START / PAUSE | Space |
| Reset | RESET | R |
| Lap | LAP | L |
| Always on top | ON TOP | T |

Laps show split and total, newest first. The window scales from 280x260 to full screen.

## Footprint

~16 KB on disk, ~10 MB private RAM, 0% CPU when idle, ~4% of one core while showing live
milliseconds. GDI objects stay flat across resizes.
