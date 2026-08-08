// World-tile <-> rendered-image-pixel conversion for pzmap2dzi's isometric output.
// Adapted from pzmap2dzi's own html/pzmap/coordinates.js (fromIsoSquare/getViewportPointBySquare) -
// same MIT-licensed tool used to render the tiles, so the math has to match exactly.
// "World" x/y here are plain absolute Project Zomboid tile coordinates - the same units
// IsoPlayer:getX()/getY() return in-game.

export function worldToImagePixel(mapInfo, worldX, worldY, layer) {
    const sqr = mapInfo.sqr;
    const scale = 1 << mapInfo.skip;
    const dx = (worldX - worldY) * sqr / 2;
    const dy = (worldX + worldY) * sqr / 4 - 1.5 * layer * sqr;
    return [(mapInfo.x0 + dx) / scale, (mapInfo.y0 + dy) / scale];
}

export function imagePixelToWorld(mapInfo, imgX, imgY, layer) {
    const sqr = mapInfo.sqr;
    const scale = 1 << mapInfo.skip;
    const dx = imgX * scale - mapInfo.x0;
    const dy = imgY * scale - mapInfo.y0;
    const sum = (dy + 1.5 * layer * sqr) * 4 / sqr;
    const diff = dx * 2 / sqr;
    return [(sum + diff) / 2, (sum - diff) / 2];
}
