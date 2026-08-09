import { worldToImagePixel, imagePixelToWorld } from "./coordinates.js";
import { MAP_SYMBOLS, PEN_COLORS } from "./mapSymbols.js";

const TILES_HOST = "https://zoidmap.tiles";
// Hardcoded until a map switcher exists on the C# side (see MapDataService's doc comment) -
// POI data is per-map, named data/poi-<mapId>.json, same "vanilla" key used everywhere else.
const ACTIVE_MAP_ID = "vanilla";
const CATEGORY_COLORS = {
    "Medical": "#E05C5C", "Police": "#5C8CE0", "Military": "#7A8C5C", "Gun Store": "#8C5CE0",
    "Fire Station": "#E0805C", "Grocery": "#5CC0E0", "Retail": "#C05CE0", "Restaurant": "#E0C05C",
    "Bank / Post Office": "#5CE0A0", "Education": "#E0E05C", "Entertainment": "#E05CA0",
    "Warehouse / Industrial": "#A0A0A0", "Church": "#C0C0E0",
};
const SYMBOL_ICON_PATH = "img/mapsymbols/";
const SYMBOL_CATEGORIES = [...new Set(MAP_SYMBOLS.map(s => s.category))];
const symbolById = Object.fromEntries(MAP_SYMBOLS.map(s => [s.id, s]));

let viewer = null;
let mapInfo = null;
let currentLayer = 0;
let pois = [];
let markers = [];
let activeCategories = new Set();
let addMarkerMode = false;
let poiOverlayEls = [];
let markerOverlayEls = [];
let liveOverlayEl = null;
let liveOverlayObj = null;
let followMode = false;
let editingMarkerId = null;
let pendingNewMarkerPoint = null;
let hoverWorld = null;
let lockedWorld = null;

function hostAvailable() {
    return !!(window.chrome && window.chrome.webview);
}

function postToHost(message) {
    if (hostAvailable()) window.chrome.webview.postMessage(message);
}

async function fetchJson(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`${url}: ${res.status}`);
    return res.json();
}

function showStatus(text) {
    const el = document.getElementById("status-banner");
    el.textContent = text;
    el.classList.add("visible");
}
function hideStatus() {
    document.getElementById("status-banner").classList.remove("visible");
}

async function init() {
    try {
        mapInfo = await fetchJson(`${TILES_HOST}/base/map_info.json`);
    } catch (e) {
        showStatus("Map tiles not found yet.\nThe isometric render is still generating in the background - this view will populate once it's done.");
        return;
    }

    viewer = OpenSeadragon({
        id: "osd-viewer",
        prefixUrl: "vendor/openseadragon/images/",
        showNavigationControl: false,
        gestureSettingsMouse: { clickToZoom: false, dblClickToZoom: true, flickEnabled: true },
        springStiffness: 8,
        animationTime: 0.4,
        maxZoomPixelRatio: 4,
        visibilityRatio: 0.2,
        constrainDuringPan: true,
    });

    // Overlays are placed in viewport coordinates, so OpenSeadragon already repositions them
    // during pan/zoom on its own - "animation-finish" (once a gesture settles) is only needed to
    // re-run the zoom-based POI declutter threshold, not to keep pins glued to the map. Using
    // "animation" instead would rebuild every POI/marker DOM element on every animation frame
    // (dozens of times a second mid-gesture) for no benefit.
    viewer.addHandler("open", () => { renderPois(); renderMarkers(); if (liveOverlayObj) setLivePosition(liveOverlayObj); });
    viewer.addHandler("canvas-click", onCanvasClick);
    viewer.addHandler("animation-finish", () => { renderPois(); renderMarkers(); });
    // Manually dragging the map means "let me look at something else" - following would just
    // fight that by yanking the view back on the next position update, so treat a drag as
    // switching Follow off. The user can turn it back on with the button when they want it again.
    viewer.addHandler("canvas-drag", () => { if (followMode) setFollowMode(false); });

    buildFloorSwitcher();
    await loadPois();
    setupCategoryPanel();

    setLayer(Math.max(0, mapInfo.minlayer));

    document.getElementById("zoom-in").addEventListener("click", () => viewer.viewport.zoomBy(1.4));
    document.getElementById("zoom-out").addEventListener("click", () => viewer.viewport.zoomBy(1 / 1.4));
    document.getElementById("zoom-home").addEventListener("click", () => viewer.viewport.goHome());
    document.getElementById("add-marker-btn").addEventListener("click", toggleAddMarkerMode);
    document.getElementById("follow-toggle-btn").addEventListener("click", () => setFollowMode(!followMode));
    document.getElementById("category-toggle-btn").addEventListener("click", () => {
        document.getElementById("category-panel").classList.toggle("visible");
    });
    document.getElementById("osd-viewer").addEventListener("mousemove", onMapMouseMove);
    document.addEventListener("keydown", onGlobalKeyDown);

    hideStatus();
}

function buildFloorSwitcher() {
    const panel = document.getElementById("floor-panel");
    panel.innerHTML = "";
    const min = 0, max = 8; // matches the rendered layer_range
    for (let l = min; l <= max; l++) {
        const btn = document.createElement("button");
        btn.className = "floor-btn";
        btn.textContent = l === 0 ? "G" : String(l);
        btn.title = l === 0 ? "Ground floor" : `Floor ${l}`;
        btn.addEventListener("click", () => setLayer(l));
        btn.dataset.layer = String(l);
        panel.appendChild(btn);
    }
}

function setLayer(layer) {
    currentLayer = layer;
    document.querySelectorAll(".floor-btn").forEach(b => {
        b.classList.toggle("active", Number(b.dataset.layer) === layer);
    });
    const dziUrl = `${TILES_HOST}/base/layer${layer}.dzi`;
    const hadImage = viewer.world.getItemCount() > 0;
    const previousBounds = hadImage ? viewer.viewport.getBounds() : null;

    // Every floor is rendered as its own full-canvas DZI in the same world-pixel space, so
    // re-opening resets OpenSeadragon's viewport unless we explicitly restore it - otherwise
    // switching floors while zoomed into a building would snap back out to the full map.
    if (previousBounds) {
        viewer.addOnceHandler("open", () => viewer.viewport.fitBounds(previousBounds, true));
    }
    // Live-position update happens in the "open" handler instead of here, once the new floor's
    // image is confirmed loaded - calling it immediately would run coordinate conversion against
    // a viewport that hasn't actually switched to the new image yet.
    viewer.open(dziUrl);
}

async function loadPois() {
    try {
        pois = await fetchJson(`data/poi-${ACTIVE_MAP_ID}.json`);
    } catch (e) {
        pois = [];
    }
    // Off by default - with 1000+ POIs, having them all on hurts pan/zoom performance when
    // zoomed out (confirmed live: smooth once zoomed into a town with few visible, stuttery
    // zoomed out with hundreds on screen - see viewport-culling comment above renderPois).
    // Users can turn on just the categories they actually want via POI Filter.
    activeCategories = new Set();
}

function setupCategoryPanel() {
    const panel = document.getElementById("category-panel");
    panel.innerHTML = "";
    const categories = [...new Set(pois.map(p => p.category))].sort();
    for (const cat of categories) {
        const row = document.createElement("label");
        row.className = "cat-row";
        const cb = document.createElement("input");
        cb.type = "checkbox";
        cb.checked = false;
        cb.addEventListener("change", () => {
            if (cb.checked) activeCategories.add(cat); else activeCategories.delete(cat);
            renderPois();
        });
        const swatch = document.createElement("span");
        swatch.className = "cat-swatch";
        swatch.style.background = CATEGORY_COLORS[cat] || "#5CA0E0";
        const label = document.createElement("span");
        const count = pois.filter(p => p.category === cat).length;
        label.textContent = `${cat} (${count})`;
        row.appendChild(cb);
        row.appendChild(swatch);
        row.appendChild(label);
        panel.appendChild(row);
    }
}

function clearOverlays(list) {
    for (const el of list) {
        // If this element gets removed while a hover tooltip is showing, "mouseleave" never
        // fires (the element is just gone) and the tooltip would otherwise be stuck on screen
        // permanently - attachTooltip stashes its live tooltip element here for exactly this.
        if (el._tooltip) { el._tooltip.remove(); el._tooltip = null; }
        viewer.removeOverlay(el);
    }
    list.length = 0;
}

function worldToViewport(wx, wy, layer) {
    const [imgX, imgY] = worldToImagePixel(mapInfo, wx, wy, layer);
    return viewer.viewport.imageToViewportCoordinates(imgX, imgY);
}

// OpenSeadragon repositions every live overlay element on every pan/zoom frame, so the biggest
// lever for smooth panning isn't how often we rebuild the overlay list (already fixed - see the
// "animation-finish" comment in init) but how MANY overlay elements exist at once. With 1000+
// POIs, keeping all of them alive even when most are off-screen was the real cost. Only the
// current viewport (plus a margin, so pins don't visibly pop in right at the edge) gets elements.
function getVisibleBoundsWithMargin(marginFactor) {
    const b = viewer.viewport.getBounds(true);
    const mx = b.width * marginFactor;
    const my = b.height * marginFactor;
    return { minX: b.x - mx, maxX: b.x + b.width + mx, minY: b.y - my, maxY: b.y + b.height + my };
}

function renderPois() {
    if (!viewer || viewer.world.getItemCount() === 0) return;
    clearOverlays(poiOverlayEls);
    const zoom = viewer.viewport.getZoom(true);
    if (zoom < 0.02) return; // too zoomed out - skip to avoid clutter/cost

    const bounds = getVisibleBoundsWithMargin(0.5);

    for (const poi of pois) {
        if (poi.layer !== currentLayer) continue;
        if (!activeCategories.has(poi.category)) continue;

        const vp = worldToViewport(poi.x, poi.y, poi.layer);
        if (vp.x < bounds.minX || vp.x > bounds.maxX || vp.y < bounds.minY || vp.y > bounds.maxY) continue;

        const el = document.createElement("div");
        el.className = "map-pin poi";
        el.style.background = CATEGORY_COLORS[poi.category] || "#5CA0E0";
        attachTooltip(el, poi.name, poi.category);

        viewer.addOverlay({ element: el, location: vp, placement: OpenSeadragon.Placement.BOTTOM_LEFT });
        poiOverlayEls.push(el);
    }
}

function renderMarkers() {
    if (!viewer || viewer.world.getItemCount() === 0) return;
    clearOverlays(markerOverlayEls);
    const bounds = getVisibleBoundsWithMargin(0.5);

    for (const m of markers) {
        if (m.layer !== currentLayer) continue;
        const vp = worldToViewport(m.x, m.y, m.layer);
        if (vp.x < bounds.minX || vp.x > bounds.maxX || vp.y < bounds.minY || vp.y > bounds.maxY) continue;

        const el = document.createElement("div");
        el.className = "map-symbol";
        const symbol = symbolById[m.icon] || symbolById["X"];
        el.style.maskImage = `url("${SYMBOL_ICON_PATH}${symbol.file}")`;
        el.style.webkitMaskImage = el.style.maskImage;
        el.style.backgroundColor = m.color || "#212121";
        attachTooltip(el, m.label, m.note || "Custom marker");
        // OpenSeadragon's own mouse tracker captures the pointer on pointerdown/mousedown to
        // drive its pan/click gesture detection - once it has capture, the resulting synthetic
        // "click" event target becomes its own canvas, not this element, so a plain click
        // listener here never fires. Stopping propagation at pointerdown (before OSD's tracker
        // sees it) keeps the gesture from starting on this element in the first place.
        el.addEventListener("pointerdown", (ev) => ev.stopPropagation());
        el.addEventListener("mousedown", (ev) => ev.stopPropagation());
        el.addEventListener("click", (ev) => {
            ev.stopPropagation();
            openMarkerForm(m, vp);
        });

        viewer.addOverlay({ element: el, location: vp, placement: OpenSeadragon.Placement.CENTER });
        markerOverlayEls.push(el);
    }
}

function attachTooltip(el, title, subtitle) {
    el.addEventListener("mouseenter", () => {
        const tip = document.createElement("div");
        tip.className = "pin-tooltip";
        tip.innerHTML = `${escapeHtml(title)}<div class="cat">${escapeHtml(subtitle)}</div>`;
        const rect = el.getBoundingClientRect();
        tip.style.left = `${rect.left + rect.width / 2}px`;
        tip.style.top = `${rect.top - 6}px`;
        document.body.appendChild(tip);
        el._tooltip = tip;
    });
    el.addEventListener("mouseleave", () => { if (el._tooltip) { el._tooltip.remove(); el._tooltip = null; } });
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function toggleAddMarkerMode() {
    addMarkerMode = !addMarkerMode;
    document.getElementById("add-marker-btn").classList.toggle("active", addMarkerMode);
    document.getElementById("osd-viewer").style.cursor = addMarkerMode ? "crosshair" : "default";
}

function onCanvasClick(event) {
    if (!addMarkerMode) return;
    event.preventDefaultAction = true;
    const viewportPoint = viewer.viewport.pointFromPixel(event.position);
    const imagePoint = viewer.viewport.viewportToImageCoordinates(viewportPoint);
    const [wx, wy] = imagePixelToWorld(mapInfo, imagePoint.x, imagePoint.y, currentLayer);

    pendingNewMarkerPoint = { x: Math.round(wx), y: Math.round(wy), layer: currentLayer };
    openMarkerForm(null, viewportPoint);
}

// Live cursor -> world-coordinate readout, plus a "C" keybind to freeze ("lock") the last
// hovered value and copy it to the clipboard - lets someone reading the map hand a teleport-
// ready X,Y,Z straight to Project Zomboid's own coordinate-accepting admin/debug commands
// without needing to eyeball pixel positions or do the iso-projection math by hand themselves.
// Same worldX/worldY units IsoPlayer:getX()/getY() return in-game (see coordinates.js), and
// "layer" here is already the plain integer floor - no conversion needed since the game's own
// Z and this app's floor index are the same thing (see ZoidHubBridge.lua's math.floor(getZ())).
function onMapMouseMove(event) {
    if (lockedWorld || !viewer || viewer.world.getItemCount() === 0) return;
    const rect = document.getElementById("osd-viewer").getBoundingClientRect();
    const pixel = new OpenSeadragon.Point(event.clientX - rect.left, event.clientY - rect.top);
    const viewportPoint = viewer.viewport.pointFromPixel(pixel);
    const imagePoint = viewer.viewport.viewportToImageCoordinates(viewportPoint);
    const [wx, wy] = imagePixelToWorld(mapInfo, imagePoint.x, imagePoint.y, currentLayer);
    hoverWorld = { x: Math.round(wx), y: Math.round(wy), layer: currentLayer };
    updateCoordReadout();
}

function updateCoordReadout() {
    const el = document.getElementById("coord-readout");
    const panel = document.getElementById("coord-panel");
    if (lockedWorld) {
        el.textContent = `LOCKED  X ${lockedWorld.x}  Y ${lockedWorld.y}  Z ${lockedWorld.layer}  - copied! Press C to unlock`;
        panel.classList.add("locked");
    } else if (hoverWorld) {
        el.textContent = `X ${hoverWorld.x}  Y ${hoverWorld.y}  Z ${hoverWorld.layer}  - press C to lock`;
        panel.classList.remove("locked");
    } else {
        el.textContent = "Hover the map for coordinates - press C to lock";
        panel.classList.remove("locked");
    }
}

function toggleCoordLock() {
    if (lockedWorld) {
        lockedWorld = null;
        updateCoordReadout();
        return;
    }
    if (!hoverWorld) return;
    lockedWorld = { ...hoverWorld };
    const text = `${lockedWorld.x},${lockedWorld.y},${lockedWorld.layer}`;
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(() => {
            // The locked value is still shown on screen either way (readable/writable-down
            // regardless), so a clipboard failure here isn't fatal - just quietly log it.
            console.warn("ZoidHub: clipboard write failed, coordinates still shown on screen for manual copy.");
        });
    }
    updateCoordReadout();
}

function onGlobalKeyDown(event) {
    // Don't hijack "C"/Escape while the user is typing a marker label or anything else text-based.
    const tag = document.activeElement && document.activeElement.tagName;
    if (tag === "INPUT" || tag === "TEXTAREA") return;

    if (event.key === "c" || event.key === "C") {
        toggleCoordLock();
    } else if (event.key === "Escape" && lockedWorld) {
        lockedWorld = null;
        updateCoordReadout();
    }
}

function openMarkerForm(marker, viewportPoint) {
    editingMarkerId = marker ? marker.id : null;
    const form = document.getElementById("marker-form");
    const pixel = viewer.viewport.viewportToViewerElementCoordinates(viewportPoint);
    form.style.left = `${pixel.x + 16}px`;
    form.style.top = `${pixel.y - 10}px`;

    const labelInput = document.getElementById("marker-label-input");
    labelInput.value = marker ? marker.label : "";
    document.getElementById("marker-delete-btn").style.display = marker ? "block" : "none";

    let selectedColor = (marker && marker.color) || PEN_COLORS[0].hex;
    let selectedIcon = (marker && marker.icon) || "X";
    let activeCategory = (symbolById[selectedIcon] || symbolById["X"]).category;

    const iconTabs = document.getElementById("marker-icon-tabs");
    const iconGrid = document.getElementById("marker-icon-grid");

    function renderIconGrid() {
        iconGrid.innerHTML = "";
        for (const symbol of MAP_SYMBOLS.filter(s => s.category === activeCategory)) {
            const sw = document.createElement("div");
            sw.className = "icon-swatch" + (symbol.id === selectedIcon ? " selected" : "");
            sw.style.maskImage = `url("${SYMBOL_ICON_PATH}${symbol.file}")`;
            sw.style.webkitMaskImage = sw.style.maskImage;
            sw.title = symbol.id;
            sw.addEventListener("click", () => {
                selectedIcon = symbol.id;
                iconGrid.querySelectorAll(".icon-swatch").forEach(s => s.classList.remove("selected"));
                sw.classList.add("selected");
            });
            iconGrid.appendChild(sw);
        }
    }

    function renderIconTabs() {
        iconTabs.innerHTML = "";
        for (const cat of SYMBOL_CATEGORIES) {
            const tab = document.createElement("button");
            tab.type = "button";
            tab.className = "icon-category-tab" + (cat === activeCategory ? " active" : "");
            tab.textContent = cat;
            tab.addEventListener("click", () => {
                activeCategory = cat;
                iconTabs.querySelectorAll(".icon-category-tab").forEach(t => t.classList.remove("active"));
                tab.classList.add("active");
                renderIconGrid();
            });
            iconTabs.appendChild(tab);
        }
    }

    renderIconTabs();
    renderIconGrid();

    const colorRow = document.getElementById("marker-color-row");
    colorRow.innerHTML = "";
    for (const pen of PEN_COLORS) {
        const sw = document.createElement("div");
        sw.className = "color-swatch" + (pen.hex === selectedColor ? " selected" : "");
        sw.style.background = pen.hex;
        sw.title = pen.name;
        sw.addEventListener("click", () => {
            selectedColor = pen.hex;
            colorRow.querySelectorAll(".color-swatch").forEach(s => s.classList.remove("selected"));
            sw.classList.add("selected");
        });
        colorRow.appendChild(sw);
    }

    form.classList.add("visible");
    labelInput.focus();

    document.getElementById("marker-save-btn").onclick = () => {
        const label = labelInput.value.trim() || "Marker";
        if (editingMarkerId) {
            const existing = markers.find(m => m.id === editingMarkerId);
            existing.label = label;
            existing.color = selectedColor;
            existing.icon = selectedIcon;
        } else {
            markers.push({
                id: `m-${Date.now()}-${Math.floor(Math.random() * 1000)}`,
                label, color: selectedColor, icon: selectedIcon,
                x: pendingNewMarkerPoint.x, y: pendingNewMarkerPoint.y, layer: pendingNewMarkerPoint.layer,
                note: "",
            });
        }
        closeMarkerForm();
        persistMarkers();
        renderMarkers();
    };
    document.getElementById("marker-delete-btn").onclick = () => {
        markers = markers.filter(m => m.id !== editingMarkerId);
        closeMarkerForm();
        persistMarkers();
        renderMarkers();
    };
    document.getElementById("marker-cancel-btn").onclick = closeMarkerForm;
}

function closeMarkerForm() {
    document.getElementById("marker-form").classList.remove("visible");
    editingMarkerId = null;
    pendingNewMarkerPoint = null;
}

function persistMarkers() {
    postToHost({ type: "markersChanged", markers });
}

function positionLiveOverlay() {
    if (!liveOverlayObj || !viewer || viewer.world.getItemCount() === 0) return;
    const vp = worldToViewport(liveOverlayObj.x, liveOverlayObj.y, liveOverlayObj.layer);
    if (liveOverlayEl) {
        viewer.updateOverlay(liveOverlayEl, vp, OpenSeadragon.Placement.CENTER);
    }
}

function setLivePosition(pos) {
    const pill = document.getElementById("live-pill");
    const followBtn = document.getElementById("follow-toggle-btn");
    if (!pos || !pos.inGame) {
        pill.classList.remove("visible");
        followBtn.style.display = "none";
        if (liveOverlayEl) { viewer.removeOverlay(liveOverlayEl); liveOverlayEl = null; }
        liveOverlayObj = null;
        return;
    }

    pill.classList.add("visible");
    followBtn.style.display = "";
    pill.querySelector("span.label").textContent = pos.name || "Live";
    liveOverlayObj = pos;

    if (pos.layer !== currentLayer) {
        if (liveOverlayEl) { viewer.removeOverlay(liveOverlayEl); liveOverlayEl = null; }
        return;
    }

    if (!liveOverlayEl) {
        liveOverlayEl = document.createElement("div");
        liveOverlayEl.className = "map-pin live";
        liveOverlayEl.title = pos.name || "You";
        const vp = worldToViewport(pos.x, pos.y, pos.layer);
        viewer.addOverlay({ element: liveOverlayEl, location: vp, placement: OpenSeadragon.Placement.CENTER });
    } else {
        positionLiveOverlay();
    }

    // Continuous following: every real position update (roughly once/second while tracking)
    // re-centers, not just on window refocus - see setFollowMode for why a manual drag turns
    // this back off rather than fighting the player for control of the view.
    if (followMode) recenterOnLivePosition();
}

function setFollowMode(on) {
    followMode = on;
    document.getElementById("follow-toggle-btn").classList.toggle("active", on);
    if (on) recenterOnLivePosition();
}

// Also used for the "left the map screen and came back" case (window alt-tabbed away/minimized,
// then refocused) via the C# "recenterOnLive" message - recenters the viewport on the player's
// last known position without touching zoom. panTo() only moves the viewport's center point; it
// has no effect on zoom level at all.
function recenterOnLivePosition() {
    if (!liveOverlayObj || !viewer || viewer.world.getItemCount() === 0) return;
    if (liveOverlayObj.layer !== currentLayer) return; // nothing to center on on this floor
    const vp = worldToViewport(liveOverlayObj.x, liveOverlayObj.y, liveOverlayObj.layer);
    viewer.viewport.panTo(vp);
}

// Host bridge: markers/POIs/live position come from the C# side after page load.
if (hostAvailable()) {
    window.chrome.webview.addEventListener("message", (event) => {
        const msg = event.data;
        if (!msg || !msg.type) return;
        if (msg.type === "initMarkers") {
            markers = msg.markers || [];
            renderMarkers();
        } else if (msg.type === "livePosition") {
            setLivePosition(msg.position);
        } else if (msg.type === "recenterOnLive") {
            recenterOnLivePosition();
        }
    });
}

window.addEventListener("DOMContentLoaded", init);
