#!/usr/bin/env python3
"""Rewrite Directory.Packages.props with the latest nuget.org version of every package.

Stable packages move to the latest stable version; packages pinned to a prerelease
move to the latest version of any kind. Used only by the weekly canary workflow.

Chapter 15: with --report FILE, also writes a Markdown table of every change, classed as major,
minor, or patch, with a link to each package's page (its release notes are linked from there).
A 0.x package is classed by its minor number, because 0.x promises no compatibility: 0.19 to 0.20
can break as much as 1.0 to 2.0. Packages listed with --hold stay where they are (a package that
moves only with the model upgrade playbook, such as Microsoft.ML.OnnxRuntimeGenAI). Packages whose
name starts with a --same-major prefix move only within their major version: ASP.NET Core's
packages are versioned with the runtime, so 11.0.0 targets net11.0 and cannot restore in a net10.0
project. The canary would only fail on them; the report lists them as held.
"""
import json
import re
import sys
import urllib.request

args = sys.argv[1:]
report_path = args[args.index("--report") + 1] if "--report" in args else None
held = {args[i + 1] for i, a in enumerate(args) if a == "--hold" and i + 1 < len(args)}
same_major = [args[i + 1] for i, a in enumerate(args) if a == "--same-major" and i + 1 < len(args)]
positional = [a for i, a in enumerate(args) if not a.startswith("--") and (i == 0 or args[i - 1] not in ("--report", "--hold", "--same-major"))]
banded = []
path = positional[0] if positional else "Directory.Packages.props"
text = open(path, encoding="utf-8").read()
pattern = re.compile(r'(<PackageVersion Include="([^"]+)" Version=")([^"]+)(")')
changes = []


def latest(package: str, current: str) -> str:
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    with urllib.request.urlopen(url, timeout=30) as response:
        versions = json.load(response)["versions"]
    if "-" not in current:
        versions = [v for v in versions if "-" not in v]
    if any(package.startswith(p) for p in same_major):
        newest = versions[-1] if versions else current
        versions = [v for v in versions if v.split(".")[0] == current.split(".")[0]]
        if newest.split(".")[0] != current.split(".")[0]:
            banded.append((package, newest))
    return versions[-1] if versions else current


def kind(old: str, new: str) -> str:
    a = [int(x) for x in re.findall(r"\d+", old.split("-")[0])[:3]] + [0, 0, 0]
    b = [int(x) for x in re.findall(r"\d+", new.split("-")[0])[:3]] + [0, 0, 0]
    if a[0] != b[0] or (a[0] == 0 and a[1] != b[1]):
        return "major"
    return "minor" if a[1] != b[1] else "patch"


def replace(match: re.Match) -> str:
    package, current = match.group(2), match.group(3)
    if package in held:
        return match.group(0)
    new = latest(package, current)
    if new != current:
        print(f"{package}: {current} -> {new}")
        changes.append((package, current, new, kind(current, new)))
    return f"{match.group(1)}{new}{match.group(4)}"


open(path, "w", encoding="utf-8").write(pattern.sub(replace, text))

if report_path:
    order = {"major": 0, "minor": 1, "patch": 2}
    with open(report_path, "w", encoding="utf-8") as out:
        out.write("## Newer package versions (chapter 15)\n\n")
        if not changes:
            out.write("Every pinned package is at its latest version.\n")
        else:
            counts = {k: sum(1 for c in changes if c[3] == k) for k in order}
            out.write(f"{len(changes)} packages have newer versions: {counts['major']} major, {counts['minor']} minor, {counts['patch']} patch.\n\n")
            out.write("| Package | Pinned | Latest | Change |\n|---|---|---|---|\n")
            for package, old, new, k in sorted(changes, key=lambda c: (order[c[3]], c[0].lower())):
                out.write(f"| [{package}](https://www.nuget.org/packages/{package}/{new}) | {old} | {new} | {k.upper() if k == 'major' else k} |\n")
        if banded:
            out.write("\nKept within their major version (a newer major targets a newer .NET): " + ", ".join(f"{p} ({v} exists)" for p, v in banded) + ".\n")
        if held:
            out.write(f"\nHeld at their pinned versions (they move with the model upgrade playbook): {', '.join(sorted(held))}.\n")
