import { worldToImagePixel, imagePixelToWorld } from "./coordinates.js";

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
const MARKER_COLORS = ["#E0A030", "#5CA0E0", "#5CE07A", "#E05C5C", "#C05CE0", "#E0E05C"];

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
let editingMarkerId = null;
let pendingNewMarkerPoint = null;

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

    buildFloorSwitcher();
    await loadPois();
    setupCategoryPanel();

    setLayer(Math.max(0, mapInfo.minlayer));

    document.getElementById("zoom-in").addEventListener("click", () => viewer.viewport.zoomBy(1.4));
    document.getElementById("zoom-out").addEventListener("click", () => viewer.viewport.zoomBy(1 / 1.4));
    document.getElementById("zoom-home").addEventListener("click", () => viewer.viewport.goHome());
    document.getElementById("add-marker-btn").addEventListener("click", toggleAddMarkerMode);
    document.getElementById("category-toggle-btn").addEventListener("click", () => {
        document.getElementById("category-panel").classList.toggle("visible");
    });

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
        el.className = "map-pin custom";
        el.style.background = m.color || "#E0A030";
        attachTooltip(el, m.label, m.note || "Custom marker");
        el.addEventListener("click", (ev) => {
            ev.stopPropagation();
            openMarkerForm(m, vp);
        });

        viewer.addOverlay({ element: el, location: vp, placement: OpenSeadragon.Placement.BOTTOM_LEFT });
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

function openMarkerForm(marker, viewportPoint) {
    editingMarkerId = marker ? marker.id : null;
    const form = document.getElementById("marker-form");
    const pixel = viewer.viewport.viewportToViewerElementCoordinates(viewportPoint);
    form.style.left = `${pixel.x + 16}px`;
    form.style.top = `${pixel.y - 10}px`;

    const labelInput = document.getElementById("marker-label-input");
    labelInput.value = marker ? marker.label : "";
    document.getElementById("marker-delete-btn").style.display = marker ? "block" : "none";

    let selectedColor = marker ? marker.color : MARKER_COLORS[0];
    const colorRow = document.getElementById("marker-color-row");
    colorRow.innerHTML = "";
    for (const c of MARKER_COLORS) {
        const sw = document.createElement("div");
        sw.className = "color-swatch" + (c === selectedColor ? " selected" : "");
        sw.style.background = c;
        sw.addEventListener("click", () => {
            selectedColor = c;
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
        } else {
            markers.push({
                id: `m-${Date.now()}-${Math.floor(Math.random() * 1000)}`,
                label, color: selectedColor,
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
    if (!pos || !pos.inGame) {
        pill.classList.remove("visible");
        if (liveOverlayEl) { viewer.removeOverlay(liveOverlayEl); liveOverlayEl = null; }
        liveOverlayObj = null;
        return;
    }

    pill.classList.add("visible");
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
        }
    });
}

window.addEventListener("DOMContentLoaded", init);
