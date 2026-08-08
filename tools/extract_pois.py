"""Extracts real points of interest from the local Project Zomboid install's own map data
(.lotheader room metadata - genuine designer-assigned room-type tags, not guessed/fabricated
coordinates) and writes ZoidHub's Assets/Data/poi.json.

Each room in the game's map files carries a lowercase category tag (e.g. "policeoffice",
"hospitalroom", "gunstore") plus its exact world-tile rectangle and floor layer. This script
buckets those tags into human categories, then greedily clusters same-category/same-floor rooms
that are close together into one POI per real building instead of one marker per room.
"""
import sys
import os
import re
import glob
import json

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)) + r"\pzmap2dzi")
from pzmap2dzi import lotheader

# "vanilla" is the only map extracted today - if a custom map is added later, point MAP_DIR at
# that map's own lotheader folder and change "vanilla" below to that map's id (matching
# MapDataService's per-map naming), rather than overwriting this file.
MAP_ID = "vanilla"
MAP_DIR = r"D:\SteamLibrary\steamapps\common\ProjectZomboid\media\maps\Muldraugh, KY"
OUTPUT_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "src", "ZoidHub", "Assets", "WebMap", "data",
    f"poi-{MAP_ID}.json"
)

# Real room-type tags -> (category, display label). Built from sampling the actual local
# install's room vocabulary (see tools/render_log.txt session notes) - not guessed.
CATEGORY_MAP = {
    "Medical": {
        "hospitalroom": "Hospital", "medclinic": "Medical Clinic", "medical": "Medical Room",
        "laboratory": "Laboratory", "pharmacy": "Pharmacy", "pharmacystorage": "Pharmacy",
        "vet": "Veterinary Clinic",
    },
    "Police": {
        "policeoffice": "Police Station", "policearchive": "Police Station",
        "policegarage": "Police Station", "policegunstorage": "Police Armory",
        "policehall": "Police Station", "policelibrary": "Police Station",
        "policelocker": "Police Station", "policeoutfitstorage": "Police Station",
        "policestorage": "Police Station", "policeswat": "Police SWAT",
        "prisoncells": "Prison", "prisonarmory": "Prison Armory",
        "prisonerbelongings": "Prison", "prisonlaundry": "Prison",
        "prisonlibrary": "Prison", "prisonlocker": "Prison", "prisonstorage": "Prison",
    },
    "Military": {
        "armory": "Armory", "armystorage": "Army Base", "armysurplus": "Army Surplus Store",
        "armysurplusguns": "Army Surplus Store", "armytent": "Army Camp", "oldarmy": "Old Army Base",
        "firearmtraining": "Firing Range",
    },
    "Gun Store": {
        "gunstore": "Gun Store", "gunstorage": "Gun Store", "gunstorestorage": "Gun Store",
    },
    "Fire Station": {
        "firestorage": "Fire Station", "firegarage": "Fire Station", "firedisplay": "Fire Station",
    },
    "Grocery": {
        "grocery": "Grocery Store", "grocerystorage": "Grocery Store",
        "conveniencestore": "Convenience Store", "cornerstore": "Corner Store",
        "cornerstorecounter": "Corner Store", "cornerstorestorage": "Corner Store",
        "departmentstore": "Department Store", "generalstore": "General Store",
        "generalstorestorage": "General Store", "ww_generalstore": "General Store",
        "gasstore": "Gas Station",
    },
    "Retail": {
        "clothesstore": "Clothing Store", "clothingstore": "Clothing Store",
        "shoestore": "Shoe Store", "jewelrystore": "Jewelry Store",
        "leatherclothesstore": "Leather Store", "giftstore": "Gift Store",
        "bookstore": "Book Store", "electronicsstore": "Electronics Store",
        "electronicstore": "Electronics Store", "furniturestore": "Furniture Store",
        "gardenstore": "Garden Store", "housewarestore": "Housewares Store",
        "musicstore": "Music Store", "pawnshop": "Pawn Shop", "petstore": "Pet Store",
        "sportstore": "Sporting Goods Store", "toystore": "Toy Store",
        "toolstore": "Tool Store", "ww_toolstore": "Tool Store", "artstore": "Art Store",
        "camerastore": "Camera Store", "candystore": "Candy Store", "cdstore": "CD Store",
        "comicstore": "Comic Store", "liquorstore": "Liquor Store", "tobaccostore": "Tobacco Store",
        "zippeestore": "Zippee Store",
    },
    "Restaurant": {
        "diner": "Diner", "dinerkitchen": "Diner", "foodcourt": "Food Court",
        "barbequestore": "Restaurant",
    },
    "Bank / Post Office": {
        "bank": "Bank", "bankstorage": "Bank", "post": "Post Office", "poststorage": "Post Office",
    },
    "Education": {
        "school": "School", "schoolgymstorage": "School", "schoollab": "School",
        "schoolstorage": "School", "universityclassroom": "University",
        "universitylibrary": "University Library", "universityoffice": "University",
        "library": "Library", "musicschool": "Music School",
    },
    "Entertainment": {
        "theatre": "Theatre", "theatrekitchen": "Theatre", "gym": "Gym",
    },
    "Warehouse / Industrial": {
        "warehouse": "Warehouse", "loggingwarehouse": "Logging Warehouse",
        "storageunit": "Storage Units", "farmstorage": "Farm Storage",
        "factory": "Factory", "factorystorage": "Factory", "loggingfactory": "Logging Factory",
        "derelict_steelfactory": "Steel Factory (Derelict)",
    },
    "Church": {
        "church": "Church",
    },
}

# Flat lookup: room name -> (category, label)
FLAT_MAP = {}
for cat, entries in CATEGORY_MAP.items():
    for room_name, label in entries.items():
        FLAT_MAP[room_name] = (cat, label)

CLUSTER_DIST = 40  # world tiles - merge same-category/layer rooms within this radius into one POI


def room_center(room):
    rects = room["rects"]
    if not rects:
        return None
    x0, y0, w, h = rects[0]
    return x0 + w / 2.0, y0 + h / 2.0


def main():
    files = glob.glob(os.path.join(MAP_DIR, "*.lotheader"))
    print(f"scanning {len(files)} cells in {MAP_DIR}")

    points = []  # (category, label, world_x, world_y, layer)
    name_re = re.compile(r"(-?\d+)_(-?\d+)\.lotheader$")

    for f in files:
        m = name_re.match(os.path.basename(f))
        if not m:
            continue
        cx, cy = int(m.group(1)), int(m.group(2))
        header = lotheader.load_lotheader(MAP_DIR, cx, cy)
        if not header:
            continue
        cell_size = header["CELL_SIZE_IN_BLOCKS"] * header["BLOCK_SIZE_IN_SQUARES"]
        for room in header["rooms"]:
            name = room["name"].decode("utf8", errors="ignore")
            mapped = FLAT_MAP.get(name)
            if not mapped:
                continue
            center = room_center(room)
            if center is None:
                continue
            local_x, local_y = center
            world_x = cx * cell_size + local_x
            world_y = cy * cell_size + local_y
            cat, label = mapped
            points.append([cat, label, world_x, world_y, room["layer"]])

    print(f"matched {len(points)} category rooms before clustering")

    # Greedy clustering: same category + same layer + within CLUSTER_DIST tiles -> merge.
    points.sort(key=lambda p: (p[0], p[4], p[2], p[3]))
    clusters = []  # each: {cat, label, xs:[], ys:[], layer}
    for cat, label, x, y, layer in points:
        merged = False
        for c in clusters:
            if c["cat"] != cat or c["layer"] != layer:
                continue
            cx0 = sum(c["xs"]) / len(c["xs"])
            cy0 = sum(c["ys"]) / len(c["ys"])
            if abs(cx0 - x) <= CLUSTER_DIST and abs(cy0 - y) <= CLUSTER_DIST:
                c["xs"].append(x)
                c["ys"].append(y)
                merged = True
                break
        if not merged:
            clusters.append({"cat": cat, "label": label, "xs": [x], "ys": [y], "layer": layer})

    print(f"clustered into {len(clusters)} POIs")

    pois = []
    for c in clusters:
        pois.append({
            "name": c["label"],
            "category": c["cat"],
            "x": round(sum(c["xs"]) / len(c["xs"])),
            "y": round(sum(c["ys"]) / len(c["ys"])),
            "layer": c["layer"],
        })

    pois.sort(key=lambda p: (p["category"], p["name"]))

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf8") as f:
        json.dump(pois, f, indent=1)

    print(f"wrote {len(pois)} POIs to {OUTPUT_PATH}")


if __name__ == "__main__":
    main()
