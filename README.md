# wlrix-apps

The wlRIX user-facing applications: one .NET solution (`Wlrix.slnx`) with a
project per app plus a shared `Wlrix.Common` library. Apps recreate the IRIX
Interactive Desktop surface.

- **Language:** C# / Avalonia
- **License:** GPL-3.0-or-later
- **Theme:** references the `Wlrix.Avalonia` package (from `wlrix-avalonia`)

## Projects

| Project           | Type | Purpose                                                    |
|-------------------|------|------------------------------------------------------------|
| `Wlrix.Common`    | lib  | Shared branding, constants, and helpers.                   |
| `Wlrix.Toolchest` | app  | IRIX-style menu launcher anchored top-left of the desktop. |
| `Wlrix.Desks`     | app  | Virtual-desktop (Rooms) overview and switcher.             |

More apps (file manager, terminal, etc.) get added as sibling projects.

## Build

```sh
dotnet build
dotnet run --project src/Wlrix.Toolchest
```
