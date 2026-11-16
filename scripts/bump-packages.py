#!/usr/bin/env python3
"""Rewrite Directory.Packages.props with the latest nuget.org version of every package.

Stable packages move to the latest stable version; packages pinned to a prerelease
move to the latest version of any kind. Used only by the weekly canary workflow.
"""
import json
import re
import sys
import urllib.request

path = sys.argv[1] if len(sys.argv) > 1 else "Directory.Packages.props"
text = open(path, encoding="utf-8").read()
pattern = re.compile(r'(<PackageVersion Include="([^"]+)" Version=")([^"]+)(")')


def latest(package: str, current: str) -> str:
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    with urllib.request.urlopen(url, timeout=30) as response:
        versions = json.load(response)["versions"]
    if "-" not in current:
        versions = [v for v in versions if "-" not in v]
    return versions[-1] if versions else current


def replace(match: re.Match) -> str:
    package, current = match.group(2), match.group(3)
    new = latest(package, current)
    if new != current:
        print(f"{package}: {current} -> {new}")
    return f"{match.group(1)}{new}{match.group(4)}"


open(path, "w", encoding="utf-8").write(pattern.sub(replace, text))
