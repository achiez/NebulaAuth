#!/usr/bin/env python3
"""Puts the previous full package of a channel into the vpk output directory, so `vpk pack` builds a delta package.

The committed feed (NebulaAuth/feeds/releases.<channel>.json on master, see build_feed.py) lists its packages with
ABSOLUTE download URLs, which `vpk download` can't handle. This script picks the newest full package of the feed,
downloads it, checks its SHA-256 against the feed and writes releases.<channel>.json with the plain file name next to
it (the layout vpk pack reads).

No feed or no full package in it: the first release of the channel, nothing to do. A feed that lists a previous
release which can't be downloaded or doesn't match its SHA-256 fails the build: the release would ship without a
delta package.
"""
import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.request

SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")


def version_key(version):
    m = SEMVER.match(version or "")
    if not m:
        raise SystemExit(f"invalid version {version}")
    major, minor, patch, pre = m.groups()
    return int(major), int(minor), int(patch), pre is None, pre or ""


def download(url, path, attempts=3):
    for attempt in range(1, attempts + 1):
        try:
            with urllib.request.urlopen(url, timeout=120) as response, open(path, "wb") as f:
                while chunk := response.read(1 << 20):
                    f.write(chunk)
            return
        except OSError as e:
            if attempt == attempts:
                raise SystemExit(f"::error::{url} could not be downloaded: {e}")
            print(f"{url}: {e}, retrying")
            time.sleep(5 * attempt)


def sha256_of(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        while chunk := f.read(1 << 20):
            digest.update(chunk)
    return digest.hexdigest()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--feed", required=True, help="committed feed of the channel (may not exist)")
    p.add_argument("--channel", required=True)
    p.add_argument("--version", required=True, help="version being released")
    p.add_argument("--out", required=True, help="vpk output directory")
    p.add_argument("--allow-http", action="store_true", help="local test feeds only")
    a = p.parse_args()

    if not os.path.exists(a.feed):
        print(f"{a.feed} not found: first release of {a.channel}, no delta package")
        return 0
    with open(a.feed, "rb") as f:
        feed = json.loads(f.read().decode("utf-8-sig"))
    if feed.get("Nebula", {}).get("Channel") != a.channel:
        raise SystemExit(f"::error::{a.feed} is not a feed of {a.channel}")
    full = [x for x in feed.get("Assets", []) if x.get("Type") == "Full"]
    if not full:
        print(f"{a.feed} has no full package: first release of {a.channel}, no delta package")
        return 0

    previous = max(full, key=lambda x: version_key(x.get("Version")))
    if version_key(previous["Version"]) >= version_key(a.version):
        raise SystemExit(f"::error::{a.channel} already has {previous['Version']}: {a.version} must be newer")
    url = previous.get("FileName", "")
    if not url.startswith("https://") and not (a.allow_http and url.startswith("http://")):
        raise SystemExit(f"::error::{url}: not an absolute https URL")
    expected = (previous.get("SHA256") or "").lower()
    if len(expected) != 64:
        raise SystemExit(f"::error::{url}: no SHA-256 in the feed")

    name = os.path.basename(urllib.request.urlparse(url).path)
    os.makedirs(a.out, exist_ok=True)
    target = os.path.join(a.out, name)
    partial = target + ".partial"
    print(f"Previous release of {a.channel}: {previous['Version']} ({url})")
    download(url, partial)
    actual = sha256_of(partial)
    if actual != expected:
        os.remove(partial)
        raise SystemExit(f"::error::{name}: SHA-256 {actual} does not match the feed ({expected})")
    os.replace(partial, target)

    entry = dict(previous)
    entry["FileName"] = name
    with open(os.path.join(a.out, f"releases.{a.channel}.json"), "wb") as f:
        f.write(json.dumps({"Assets": [entry]}, indent=2).encode("utf-8"))
    print(f"{target}: verified, base of the delta package")
    return 0


if __name__ == "__main__":
    sys.exit(main())
