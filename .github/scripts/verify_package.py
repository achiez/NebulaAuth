#!/usr/bin/env python3
"""Checks the Velopack output of one RID before anything is uploaded.

* the full package has the expected id / version / channel / RID, the main executable, the localization files,
  NLog.config and the bundled .NET runtime (self-contained), and no *.pdb;
* the portable package exists; on Windows it has the layout the 1.x updater unpacks over a 1.x folder
  (NebulaAuth.exe, Update.exe, .portable in the root, the app in current\\) and it contains no user data
  (settings.json, maFiles, ...) that could overwrite the data of an existing installation;
* every asset of releases.<channel>.json has a SHA-256.
"""
import argparse
import json
import os
import re
import sys
import zipfile

USER_DATA = re.compile(
    r"(^|/)(settings\.json|proxies\.json|maac\.json|export_templates\.json|maFiles(_backup|_removed)?/.*|settings/.*|"
    r"apply-attempt\.json|logs/.*)$",
    re.IGNORECASE,
)

errors = []


def check(condition, message):
    if not condition:
        errors.append(message)


def load_json(path):
    with open(path, "rb") as f:
        return json.loads(f.read().decode("utf-8-sig"))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--releases-dir", required=True)
    p.add_argument("--channel", required=True)
    p.add_argument("--rid", required=True)
    p.add_argument("--version", required=True)
    a = p.parse_args()

    feed = load_json(os.path.join(a.releases_dir, f"releases.{a.channel}.json"))
    assets = load_json(os.path.join(a.releases_dir, f"assets.{a.channel}.json"))
    for asset in feed.get("Assets", []):
        check(len(asset.get("SHA256") or "") == 64, f"{asset.get('FileName')}: no SHA256")

    full = [x for x in feed["Assets"] if x["Type"] == "Full" and x["Version"] == a.version]
    check(len(full) == 1, f"expected one full package of {a.version}, found {len(full)}")
    exe = "NebulaAuth.exe" if a.rid.startswith("win") else "NebulaAuth"
    if full:
        path = os.path.join(a.releases_dir, os.path.basename(full[0]["FileName"]))
        with zipfile.ZipFile(path) as z:
            names = z.namelist()
            nuspec = z.read("NebulaAuth.nuspec").decode("utf-8")
        for tag, expected in (("id", "NebulaAuth"), ("version", a.version), ("channel", a.channel), ("rid", a.rid)):
            m = re.search(rf"<{tag}>([^<]*)</{tag}>", nuspec)
            check(m is not None and m.group(1) == expected, f"nuspec {tag} = {m.group(1) if m else None}, expected {expected}")
        app = [n[len("lib/app/"):] for n in names if n.startswith("lib/app/")]
        lower = {n.lower() for n in app}
        check(any(n.endswith(exe.lower()) for n in lower), f"{exe} missing in the full package")
        check(any(n.endswith("nlog.config") for n in lower), "NLog.config missing")
        check(sum(1 for n in lower if "localization/" in n and n.endswith(".loc.json")) >= 10, "localization files missing")
        check(any(n.endswith("system.private.corelib.dll") for n in lower), ".NET runtime missing (not self-contained?)")
        check(not any(n.endswith(".pdb") for n in lower), "*.pdb in the full package")
        check(not any(USER_DATA.search(n) for n in app), "user data files in the full package")

    portable = [x["RelativeFileName"] for x in assets if x.get("Type") == "Portable"]
    check(len(portable) == 1, f"expected one portable package, found {portable}")
    if portable and a.rid.startswith("win"):
        with zipfile.ZipFile(os.path.join(a.releases_dir, portable[0])) as z:
            names = z.namelist()
            version_file = z.read("current/sq.version").decode("utf-8") if "current/sq.version" in names else ""
        for required in (".portable", "NebulaAuth.exe", "Update.exe", "current/NebulaAuth.exe", "current/NLog.config"):
            check(required in names, f"portable ZIP: {required} missing")
        check(f"<version>{a.version}</version>" in version_file, "portable ZIP: current/sq.version has another version")
        root_files = [n for n in names if "/" not in n]
        check(sorted(root_files) == [".portable", "NebulaAuth.exe", "Update.exe"],
              f"portable ZIP: unexpected root entries {root_files} (the 1.x updater unpacks them over a user folder)")
        check(not any(n.endswith(".pdb") for n in names), "portable ZIP: *.pdb")
        check(not any(USER_DATA.search(n) for n in names), "portable ZIP: user data files")
    elif portable and a.rid.startswith("osx"):
        with zipfile.ZipFile(os.path.join(a.releases_dir, portable[0])) as z:
            names = z.namelist()
        check(any(n.endswith(".app/Contents/MacOS/NebulaAuth") for n in names), "portable ZIP: .app main executable missing")
        check(any(n.endswith(".app/Contents/Info.plist") for n in names), "portable ZIP: Info.plist missing")
        check(not any(n.endswith(".pdb") for n in names), "portable ZIP: *.pdb")
    elif portable:
        with open(os.path.join(a.releases_dir, portable[0]), "rb") as f:
            check(f.read(4) == b"\x7fELF", "AppImage: not an ELF executable")

    if errors:
        for e in errors:
            print(f"::error::{a.rid}: {e}")
        return 1
    print(f"{a.rid}: package checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
