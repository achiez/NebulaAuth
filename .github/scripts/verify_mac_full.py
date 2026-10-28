#!/usr/bin/env python3
"""Checks that the full package of a macOS channel holds exactly the signed bundle of the portable ZIP.

The portable ZIP (ditto) keeps the extended attributes in which codesign stores the signatures of non-Mach-O files
in Contents/MacOS (the .NET assemblies), so its .app is checked with `codesign --verify --deep --strict` by the
workflow. The full package is a plain zip (Velopack: lib/app/ = the bundle, symbolic links as *.__symlink entries)
without extended attributes, and Velopack's updater extracts it as is: the bundle can't pass a deep check there.
What must hold instead: every file is byte-identical to the verified bundle, so every Mach-O file (apphost,
UpdateMac, native libraries) carries the same valid embedded signature, and Info.plist and the resources are the
ones that signature was made for.
"""
import argparse
import os
import sys
import zipfile

MACHO_MAGICS = {b"\xfe\xed\xfa\xce", b"\xce\xfa\xed\xfe", b"\xfe\xed\xfa\xcf", b"\xcf\xfa\xed\xfe",
                b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca"}
SYMLINK_SUFFIX = ".__symlink"


def bundle_entries(app):
    files, links = {}, {}
    for root, dirs, names in os.walk(app):
        for name in dirs + names:
            path = os.path.join(root, name)
            rel = os.path.relpath(path, app).replace(os.sep, "/")
            if os.path.islink(path):
                links[rel] = os.readlink(path)
            elif os.path.isfile(path):
                files[rel] = path
    return files, links


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--app", required=True, help=".app of the portable ZIP (already verified with codesign)")
    p.add_argument("--nupkg", required=True, help="full package of the same version")
    a = p.parse_args()

    files, links = bundle_entries(a.app)
    errors, seen_files, seen_links, macho = [], set(), set(), 0
    with zipfile.ZipFile(a.nupkg) as z:
        for info in z.infolist():
            if not info.filename.startswith("lib/app/") or info.is_dir():
                continue
            rel = info.filename[len("lib/app/"):]
            data = z.read(info)
            if rel.endswith(SYMLINK_SUFFIX):
                rel = rel[:-len(SYMLINK_SUFFIX)]
                seen_links.add(rel)
                if links.get(rel) != data.decode("utf-8"):
                    errors.append(f"{rel}: link target {data.decode('utf-8')!r}, bundle {links.get(rel)!r}")
                continue
            seen_files.add(rel)
            if rel not in files:
                errors.append(f"{rel}: not in the signed bundle")
                continue
            with open(files[rel], "rb") as f:
                if f.read() != data:
                    errors.append(f"{rel}: differs from the signed bundle")
            if data[:4] in MACHO_MAGICS:
                macho += 1
    for rel in sorted(set(files) - seen_files):
        errors.append(f"{rel}: missing in the full package")
    for rel in sorted(set(links) - seen_links):
        errors.append(f"{rel}: symbolic link missing in the full package")
    if macho == 0:
        errors.append("no Mach-O file in the full package")

    if errors:
        for e in errors[:50]:
            print(f"::error::{os.path.basename(a.nupkg)}: {e}")
        return 1
    print(f"{os.path.basename(a.nupkg)}: {len(seen_files)} file(s) and {len(seen_links)} link(s) identical to the "
          f"signed bundle, {macho} Mach-O file(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
