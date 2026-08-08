# ZoidHub

**DEVELOPMENT DISCONTINUED TO THE PUBLIC**

A companion map app for Project Zomboid. Renders a real, sprite-based interactive map directly from your own local game install, with points of interest, custom marking, and optional live in-game position tracking.

---

## Not affiliated with The Indie Stone

**ZoidHub is an unofficial, third-party fan tool. It is not made by, endorsed by, or affiliated with The Indie Stone (TIS) in any way.** Project Zomboid, its name, and its assets are the property of The Indie Stone. See [Attribution](#attribution) below for how ZoidHub uses them and on what basis.

---

## What it does

- **Real map, real sprites.** ZoidHub renders an isometric map straight from your own Project Zomboid install's actual map and texture files — the same buildings, roads, and terrain you'd see in-game, not a stylized or flat abstraction.
- **Points of interest.** Building-type markers (hospitals, police stations, stores, and more) extracted directly from the game's own map data — not guessed, not hand-listed. Off by default (see [Performance](#performance) below); turn on just the categories you want from the POI Filter.
- **Custom markers.** Click to drop your own labeled, colored markers anywhere on the map.
- **Live position** *(optional)*. A small companion Lua mod (`ZoidHubBridge`) that shows your current in-game location on the map in real time. Entirely opt-in, client-side only, and doesn't read or transmit any other players' data.
- **Zoom and pan** the map with your mouse (drag to pan, wheel or on-screen buttons to zoom), switch between building floors, and pick a CPU usage mode (Light/Fast) for the one-time map render so it never has to compete with the game for resources.

## What it doesn't do

- No loot, spawn, or zombie-density information of any kind. It shows what the world looks like, not what's inside it.

## Installing

1. Run `ZoidHub.exe` — it's a single portable file, no installer or dependencies. Copy it anywhere and run it.
2. On first launch, it renders your map directly from your local Project Zomboid install. This happens once and only once; expect it to take a while and use meaningful CPU in the background (choose Light or Fast mode in the render status bar depending on how much you want it competing for resources while it works).
3. For live position tracking: click **Install Live Position Mod** in the title bar, then in Project Zomboid go to **Mods**, enable **ZoidHub Bridge**, and — important — make sure it's also enabled for your specific save (Project Zomboid tracks mods per-save separately from the global list). Restart the game fully after enabling it, then tick **Live Position** in ZoidHub.

## Performance

With over 1,100 points of interest available, having them all visible at once measurably hurts pan/zoom smoothness when zoomed out. **POIs are off by default for this reason** — turn on only the specific categories you're actually looking for via **POI Filter**, rather than all of them at once.

## Attribution

Project Zomboid is © [The Indie Stone](https://projectzomboid.com/). All game assets, sprites, and map data referenced or rendered by ZoidHub belong to them. ZoidHub reads and renders these from your own locally-owned copy of the game under the terms of their [EULA / Terms & Conditions](https://store.steampowered.com/eula/108600_eula_1), which permits non-commercial derivative use of game assets with attribution — this notice is that attribution. ZoidHub does not ship, host, or redistribute any Project Zomboid game files.

ZoidHub's map rendering pipeline is built on [pzmap2dzi](https://github.com/blind-coder/pzmap2dzi) by blind-coder (MIT License), and its map viewer uses [OpenSeadragon](https://openseadragon.github.io/) (BSD-3-Clause).

## License / status

ZoidHub itself is early alpha software, provided as-is with no warranty. Not currently released under an open-source license.
