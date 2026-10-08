# Pawprint Viewer

A lightweight Second Life and OpenSimulator viewer for Linux with a native GTK3
interface. Multiple accounts share one window, with chat, inventory, attachments,
friends, nearby objects, a world map, RLV/RLVa and desktop notifications. GTK uses
the system theme and font. The viewer has no 3D view or in-world audio.

## Requirements

- .NET 10 SDK to build; .NET 10 runtime to run a framework-dependent build.
- GTK3 runtime on Linux.
- `libsecret` and a desktop Secret Service provider for saved passwords.
- `libnotify` and a desktop notification service for notification popups.

## Build and run

Run these commands from the repository root:

```sh
dotnet build -c Release
./Radegast.Gtk/bin/Release/net10.0/PawprintViewer
```

For development:

```sh
dotnet run --project Radegast.Gtk/Radegast.Gtk.csproj
```

To collect the viewer and its dependencies in one directory:

```sh
dotnet publish Radegast.Gtk/Radegast.Gtk.csproj -c Release -o bin/PawprintViewer
./bin/PawprintViewer/PawprintViewer
```

These builds use the installed .NET runtime and system GTK3 libraries.

## Repository layout

| Path | Purpose |
| --- | --- |
| `PawprintViewer.sln` | GTK viewer, shared core and their tests |
| `Radegast.Gtk/` | Pawprint Viewer interface, icon and grid list |
| `Radegast.Core/` | Shared grid communication and supporting code |
| `Radegast.Gtk.Tests/` | GTK account integration checks and native UI checks |
| `Radegast.Core.Tests/` | Shared core unit tests |
| `docs/` | GTK development plan |

The source namespaces and project directory names retain `Radegast` to match the
shared upstream code. This fork builds the GTK3 viewer only.

## Tests

```sh
dotnet test Radegast.Core.Tests/Radegast.Core.Tests.csproj
dotnet run --project Radegast.Gtk.Tests/Radegast.Gtk.Tests.csproj
```

The account integration checks use simulated packets and do not log in to a grid.
Display-dependent checks and further setup instructions are documented in the
[GTK viewer README](Radegast.Gtk/README.md).

## Documentation

- [Viewer features, setup and testing](Radegast.Gtk/README.md)
- [GTK3 client development plan](docs/Gtk3ClientPlan.md)

## Origin

Pawprint Viewer is derived from the Radegast Metaverse Client. The original
project's authors, license and acknowledgments are retained below. The WinForms
and Avalonia clients, their plugins and their installers are not included in
this GTK3 fork.

## Upstream authors

### Project founder:

* **Latif Khalifa**

### Upstream maintainer and lead developer:

* **Cinder Roxley** (email cinder sdf.org)

### Contributors:

* **nooperation**
* **Luminous Luminos**

### Alumni Contributors:

* **Douglas R. Miles**
* **Mojito Sorbet**
* **Robin Cornelius**
* **Revolution Smythe**

## License

**Radegast Metaverse Client**
* Copyright © 2009-2018, Radegast Development Team
* Copyright © 2017-2026, Sjofn, LLC.
* All rights reserved.

 Radegast is free software: you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

 This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

## Contributors

<a href="https://github.com/cinderblocks/Radegast/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=cinderblocks/Radegast" />
</a>

## Acknowledgments

Based on SLeek
Copyright © 2006-2008, Paul Clement (a.k.a. Delta)
All rights reserved.

PrimMesher
Copyright © 2010 Dahlia Trimble
http://forge.opensimulator.org/gf/project/primmesher/

CoreJ2K
Copyright © 2024-2025 Sjofn LLC

AIMLBot
Copyright © 2006 Nicholas H.Tollervey (http://ntoll.org)

CommandLineParser
Copyright © 2005 - 2015 Giacomo Stelluti Scala & Contributors

CSAT Library
Copyright © 2011 mjt

log4net
Copyright © 2004-2024 Apache Software Foundation

Ogg Vorbis
Copyright © 2002, Xiph.org Foundation

OpenTK 3D
Copyright © 2006-2014 Stefanos Apostolopoulos <stapostol@gmail.com> for the Open Toolkit library.

MemoryPack
Copyright © 2022 Cysharp, Inc.

SkiaSharp
Copyright © 2015-2016 Xamarin, Inc.

SmartIrc4net
Copyright © 2003-2005 Mirco Bauer
http://www.meebey.net/projects/smartirc4net/

XmlRpcCore
Copyright © 2020-2024, Sjofn LLC.

zlib.net
Copyright © 1996-2017 Greg Roelofs, Jean-loup Gailly and Mark Adler.

Artwork files are licenced under the
Creative Commons Share and Share Alike 3.0
Copyright © 2002-2009 Linden Research Inc.
