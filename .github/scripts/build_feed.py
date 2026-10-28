#!/usr/bin/env python3
"""Builds the update feed of one channel for 2.x clients: NebulaAuth/feeds/releases.<channel>.json.

Input: the Velopack output of this release (releases.<channel>.json, assets.<channel>.json) and the feed currently
committed in the repository. Output: a feed with
  * "Assets": the new full/delta packages plus older ones from the committed feed (for delta chains), each with an
    ABSOLUTE GitHub release download URL (no GitHub API calls on the client) and its SHA-256;
  * "Nebula": release metadata covered by the signature (channel, version, publish time, release page, manual package).
The client (src/NebulaAuth/Core/Updates/SignedFeedSource.cs) verifies the detached signature (.sig, created by the
workflow with openssl), the channel and the publish time before it uses anything from the feed.
"""
import argparse
import datetime
import json
import os
import re
import sys

SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")


def version_key(version):
    m = SEMVER.match(version)
    if not m:
        raise SystemExit(f"invalid version {version}")
    major, minor, patch, pre = m.groups()
    # a release sorts after its pre-releases
    return int(major), int(minor), int(patch), pre is None, pre or ""


def load_json(path):
    with open(path, "rb") as f:
        return json.loads(f.read().decode("utf-8-sig"))


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--channel", required=True)
    p.add_argument("--version", required=True)
    p.add_argument("--releases-dir", required=True, help="vpk output directory of this channel")
    p.add_argument("--asset-base-url", required=True, help="URL the release assets are downloaded from (ends with /)")
    p.add_argument("--release-url", required=True, help="release page of this version")
    p.add_argument("--previous", help="feed committed so far (optional)")
    p.add_argument("--out", required=True)
    p.add_argument("--published", help="ISO-8601 UTC publish time (default: now)")
    p.add_argument("--keep", type=int, default=10, help="number of versions kept in the feed")
    p.add_argument("--allow-http", action="store_true", help="local test feeds only (the app accepts https in release builds)")
    a = p.parse_args()

    channel, version = a.channel, a.version
    base = a.asset_base_url if a.asset_base_url.endswith("/") else a.asset_base_url + "/"
    vpk_feed = load_json(os.path.join(a.releases_dir, f"releases.{channel}.json"))
    vpk_assets = load_json(os.path.join(a.releases_dir, f"assets.{channel}.json"))

    new_assets = []
    for asset in vpk_feed.get("Assets", []):
        if asset.get("Version") != version:
            continue  # a previous full package downloaded only to build the delta
        if not asset.get("SHA256") or len(asset["SHA256"]) != 64:
            raise SystemExit(f"{asset.get('FileName')}: missing SHA256")
        asset = dict(asset)
        asset["FileName"] = base + os.path.basename(asset["FileName"])
        new_assets.append(asset)
    if not any(x["Type"] == "Full" for x in new_assets):
        raise SystemExit(f"releases.{channel}.json has no full package of {version}")

    old_assets = []
    if a.previous and os.path.exists(a.previous):
        previous = load_json(a.previous)
        if previous.get("Nebula", {}).get("Channel") != channel:
            raise SystemExit(f"{a.previous} is not a feed of {channel}")
        for asset in previous.get("Assets", []):
            if asset.get("Version") == version:
                raise SystemExit(f"{version} is already in the committed feed of {channel}: never republish a version")
            if not str(asset.get("FileName", "")).startswith("http://" if a.allow_http else "https://"):
                raise SystemExit(f"{asset.get('FileName')}: previous feed asset without an absolute https URL")
            old_assets.append(asset)

    versions = sorted({x["Version"] for x in old_assets} | {version}, key=version_key, reverse=True)[: a.keep]
    if version_key(versions[0]) != version_key(version):
        raise SystemExit(f"{version} is older than {versions[0]} in the committed feed of {channel}")
    assets = new_assets + [x for x in old_assets if x["Version"] in versions]

    manual = next((x["RelativeFileName"] for x in vpk_assets if x.get("Type") == "Portable"), None)
    if manual is None:
        raise SystemExit(f"assets.{channel}.json has no portable package")
    published = a.published or datetime.datetime.now(datetime.timezone.utc).replace(microsecond=0).isoformat()

    feed = {
        "Assets": assets,
        "Nebula": {
            "Channel": channel,
            "Version": version,
            "Published": published,
            "ReleaseUrl": a.release_url,
            "ManualUrl": base + os.path.basename(manual),
        },
    }
    os.makedirs(os.path.dirname(os.path.abspath(a.out)), exist_ok=True)
    with open(a.out, "wb") as f:
        f.write(json.dumps(feed, indent=2).encode("utf-8"))
    print(f"{a.out}: {len(assets)} asset(s), versions {', '.join(versions)}")


if __name__ == "__main__":
    sys.exit(main())
