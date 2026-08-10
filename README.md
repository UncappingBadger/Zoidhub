# ZoidHub

A companion map app for Project Zomboid. Renders a real, sprite-based interactive map directly from your own local game install, with points of interest, custom marking, and optional live in-game position tracking.

**Not affiliated with The Indie Stone.** ZoidHub is an unofficial, third-party fan tool — not made by, endorsed by, or affiliated with The Indie Stone (TIS) in any way. Project Zomboid, its name, and its assets are the property of The Indie Stone. See [Attribution](#attribution) below.

## What it does

- Renders an isometric map straight from your own Project Zomboid install's actual map and texture files — real buildings, roads, and terrain, not a stylized abstraction.
- Shows points of interest (hospitals, police stations, stores, and more), extracted directly from the game's own map data.
- Lets you drop your own labeled, colored markers anywhere on the map.
- Optional live position tracking via a small companion Lua mod (`ZoidHubBridge`) — shows your current in-game location on the map in real time.
- Zoom, pan, and switch between building floors.

## What it doesn't do

- No loot, spawn, or zombie-density information of any kind — it shows what the world looks like, not what's inside it.
- Vanilla (base game) map only — custom/Workshop maps aren't supported yet.
- Live position tracking is entirely opt-in and client-side only — it never reads or transmits any other player's data.

## Installing

1. Run `ZoidHub.exe` — a single portable file, no installer or dependencies.
2. On first launch, it renders your map from your local Project Zomboid install. This happens once; it can take a while and needs ~150-250GB of free disk space (use **Change Location** if your main drive doesn't have room). If ZoidHub can't find your install automatically, use **Browse for Game...** to point it there.
3. For live position tracking, click **Install Live Position Mod**, then enable **ZoidHub Bridge** in Project Zomboid's Mods menu for your save, restart the game, and tick **Live Position** in ZoidHub.

After a Project Zomboid update changes the map, use **Re-render Map** to regenerate it.

## Attribution

Project Zomboid is © [The Indie Stone](https://projectzomboid.com/). All game assets, sprites, and map data referenced or rendered by ZoidHub belong to them. ZoidHub reads and renders these from your own locally-owned copy of the game under the terms of their [EULA / Terms & Conditions](https://store.steampowered.com/eula/108600_eula_1), which permits non-commercial derivative use of game assets with attribution — this notice is that attribution. ZoidHub does not ship, host, or redistribute any Project Zomboid game files.

ZoidHub's map rendering pipeline is built on [pzmap2dzi](https://github.com/blind-coder/pzmap2dzi) by blind-coder (MIT License), and its map viewer uses [OpenSeadragon](https://openseadragon.github.io/) (BSD-3-Clause).

## Issues

Found a bug? [Open an issue on GitHub](https://github.com/UncappingBadger/Zoidhub/issues). The app log at `%AppData%\ZoidHub\logs\zoidhub.log` is usually the most useful thing to include.

## License

Provided as-is, with no warranty. Not currently released under an open-source license.
