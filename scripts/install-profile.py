#!/usr/bin/env python3
"""Creates the "Spotify" OpenDeck profile for the Ulanzi D200X and (optionally) puts the volume + layout-switch
dial on the main profile.

  scripts/install-profile.py                     # write profiles/<device>/Spotify.json (backs up an existing one)
  scripts/install-profile.py --main-dial 0       # also replace dial 0 of the main profile with the Spotify volume dial
                                                 # whose press switches to the Spotify layout (backs up the main profile)
  scripts/install-profile.py --wide-mode icon --wide-fit stretch   # per-layout wide-screen settings for the D200X plugin ≥ 1.3
  scripts/install-profile.py --device ulanzi-d200x --main Default --name Spotify

Layout (5×3 grid, D200X):
  row 0 │ Made for you 1–5
  row 1 │ Most played 1–5
  row 2 │ Suggested 1–2 │ Like / unlike │ Now playing (wide screen) │ D200X wide-screen setting (copied from the main profile)
  dials │ 0: Spotify volume, press = back to the main layout │ 1: copied from the main profile │ 2: browse dial
  side  │ 1: previous track │ 2: next track

OpenDeck keeps loaded profiles in memory and writes them back on exit: a new profile is picked up when it is
first selected, but a change to the main profile only shows after OpenDeck is restarted.
"""
import argparse, json, os, shutil, sys, time

PLUGIN = "com.josbol.spotifymusicpicker.sdPlugin"
PREFIX = "com.josbol.spotifymusicpicker."
WIDE_ACTION = "com.ulanzi.d200x.widescreen"

def config_dir(override=None):
    for d in (override, os.path.expanduser("~/.config/opendeck"), os.path.expanduser("~/.var/app/me.amankhanna.opendeck/config/opendeck")):
        if not d:
            continue
        if os.path.isdir(d):
            return d
    sys.exit("OpenDeck config directory not found")

def state(image, show=False):
    return {"alignment": "middle", "background_colour": "#000000", "colour": "#FFFFFF", "family": "Liberation Sans",
            "image": image, "image_scale": 100, "name": "", "show": show, "size": 16, "stroke_colour": "#000000",
            "stroke_size": 3, "style": "Regular", "text": "", "underline": False}

def load_manifest(cfg):
    path = os.path.join(cfg, "plugins", PLUGIN, "manifest.json")
    if not os.path.exists(path):
        sys.exit(f"{path} not found: install the plugin first (scripts/install.sh --link)")
    with open(path) as f:
        return json.load(f)

def action_def(manifest, short):
    uuid = PREFIX + short
    a = next(x for x in manifest["Actions"] if x["UUID"] == uuid)
    icon = f"plugins/{PLUGIN}/{a['Icon']}@2x.png"
    enc = a.get("Encoder")
    encoder = None
    if enc:
        td = enc.get("TriggerDescription", {})
        encoder = {"background": enc.get("background", ""), "icon": enc.get("Icon", ""), "layout": enc.get("layout", ""),
                   "stack_color": enc.get("StackColor", ""),
                   "trigger_description": {"long_touch": td.get("LongTouch", ""), "push": td.get("Push", ""), "rotate": td.get("Rotate", ""), "touch": td.get("Touch", "")}}
    return {
        "controllers": a.get("Controllers", ["Keypad"]),
        "disable_automatic_states": a.get("DisableAutomaticStates", False),
        "encoder": encoder,
        "icon": icon,
        "name": a["Name"],
        "plugin": PLUGIN,
        "property_inspector": f"plugins/{PLUGIN}/{a['PropertyInspectorPath']}" if a.get("PropertyInspectorPath") else "",
        "states": [state(f"plugins/{PLUGIN}/{s['Image']}@2x.png", s.get("ShowTitle", True)) for s in a["States"]],
        "supported_in_multi_actions": a.get("SupportedInMultiActions", False),
        "tooltip": a.get("Tooltip", ""),
        "uuid": uuid,
        "visible_in_action_list": a.get("VisibleInActionsList", True),
    }

def instance(manifest, short, controller, position, settings=None):
    a = action_def(manifest, short)
    return {"action": a, "children": None, "context": f"{controller}.{position}.0", "current_state": 0,
            "settings": settings or {}, "states": [dict(s) for s in a["states"]]}

def retarget(inst, controller, position, settings=None):
    """Copy an instance from another profile onto a slot (optionally with different settings)."""
    if inst is None:
        return None
    inst = json.loads(json.dumps(inst))
    inst["context"] = f"{controller}.{position}.0"
    if settings is not None:
        inst["settings"] = settings
    return inst

def build_profile(manifest, main_profile, name, wide_mode, wide_fit, like_key=12):
    keys = [None] * 17   # 5x3 grid (0-14) + 2 touchpoints (15, 16)
    sliders = [None] * 3
    for pos in range(0, 5):
        keys[pos] = instance(manifest, "slot", "Keypad", pos, {"row": "madeforyou", "slot": pos + 1})
    for pos in range(5, 10):
        keys[pos] = instance(manifest, "slot", "Keypad", pos, {"row": "frequent", "slot": pos - 4})
    slot = 1
    for pos in range(10, 13):
        if pos == like_key:
            keys[pos] = instance(manifest, "like", "Keypad", pos)
        else:
            keys[pos] = instance(manifest, "slot", "Keypad", pos, {"row": "recommended", "slot": slot})
            slot += 1
    keys[13] = instance(manifest, "nowplaying", "Keypad", 13, {"layout": "wide"})
    main_keys = (main_profile or {}).get("keys", [None] * 17)
    wide = next((k for k in main_keys if k and k["action"]["uuid"] == WIDE_ACTION), None)
    wide_settings = None
    if wide_mode or wide_fit:
        wide_settings = dict(wide["settings"]) if wide else {}
        if wide_mode:
            wide_settings["wideMode"] = wide_mode
        if wide_fit:
            wide_settings["wideFit"] = wide_fit
    keys[14] = retarget(wide, "Keypad", 14, wide_settings)   # the D200X "Wide screen" action (dead cell), per-layout settings
    keys[15] = instance(manifest, "previous", "Keypad", 15)   # side button 1 (no display)
    keys[16] = instance(manifest, "next", "Keypad", 16)       # side button 2 (no display)
    sliders[0] = instance(manifest, "volumedial", "Encoder", 0, {"mode": "main"})
    if main_profile:
        ms = main_profile.get("sliders", [])
        sliders[1] = retarget(ms[1] if len(ms) > 1 else None, "Encoder", 1)   # e.g. the PipeWire master volume, as on the main layout
    sliders[2] = instance(manifest, "browsedial", "Encoder", 2)
    return {"id": name, "keys": keys, "sliders": sliders, "infobars": []}

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--config", default=None, help="OpenDeck config directory (default: ~/.config/opendeck or the Flatpak one)")
    ap.add_argument("--device", default="ulanzi-d200x")
    ap.add_argument("--main", default="Default", help="main profile id (source of the wide-screen action and dial 1)")
    ap.add_argument("--name", default="Spotify", help="name of the Spotify profile to create")
    ap.add_argument("--main-dial", type=int, default=None, help="replace this dial of the main profile with the Spotify volume dial (press → Spotify layout); needs an OpenDeck restart")
    ap.add_argument("--wide-mode", default="icon", choices=["", "clock", "stats", "icon", "off"], help="wide-screen mode for this layout (D200X plugin ≥ 1.3; '' = leave the global setting)")
    ap.add_argument("--wide-fit", default="stretch", choices=["", "letterbox", "stretch"], help="how the D200X shows the wide image ('stretch' for the plugin's 2:1 now-playing image)")
    ap.add_argument("--like-key", type=int, default=12, help="key (10-12) for the Like / unlike button, -1 for none (three suggestions instead)")
    args = ap.parse_args()

    cfg = config_dir(args.config)
    manifest = load_manifest(cfg)
    pdir = os.path.join(cfg, "profiles", args.device)
    os.makedirs(pdir, exist_ok=True)
    main_path = os.path.join(pdir, f"{args.main}.json")
    main_profile = json.load(open(main_path)) if os.path.exists(main_path) else None

    out = os.path.join(pdir, f"{args.name}.json")
    if os.path.exists(out):
        shutil.copy(out, out + f".bak-{int(time.time())}")
    with open(out, "w") as f:
        json.dump(build_profile(manifest, main_profile, args.name, args.wide_mode, args.wide_fit, args.like_key), f, indent=2)
    print(f"wrote {out}")

    if args.main_dial is not None and main_profile is not None:
        d = args.main_dial
        sliders = main_profile.setdefault("sliders", [])
        while len(sliders) <= d:
            sliders.append(None)
        old = sliders[d]["action"]["uuid"] if sliders[d] else "nothing"
        shutil.copy(main_path, main_path + f".bak-{int(time.time())}")
        sliders[d] = instance(manifest, "volumedial", "Encoder", d, {"mode": "picker"})
        with open(main_path, "w") as f:
            json.dump(main_profile, f, indent=2)
        print(f"dial {d} of {main_path}: {old} → Spotify volume dial (press = Spotify layout); restart OpenDeck to load it")

if __name__ == "__main__":
    main()
