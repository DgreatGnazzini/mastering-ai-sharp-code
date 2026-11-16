#!/usr/bin/env python3
"""Downloads a local model listed in models.lock.json and checks every file (chapter 15).

    python3 scripts/get-model.py phi-4-mini-instruct-onnx-cpu-int4 [DEST]
    python3 scripts/get-model.py phi-4-mini-instruct-onnx-cpu-int4 [DEST] --record
    python3 scripts/get-model.py --record-ollama http://localhost:11434

For a Hugging Face model, the script resolves the revision in the lock (or, for an entry that is not
pinned yet, the current commit of main) to a commit hash, lists the folder at that commit, downloads
each file, and checks its SHA-256 twice: against the publisher's own record (Hugging Face stores the
SHA-256 of every large file) and against models.lock.json. Any difference fails the script.

An entry with no hashes yet is the one moment trust is extended: --record writes the commit and every
file's size and hash into models.lock.json. Review that diff like a dependency upgrade, then commit it.
From then on the same files are the only ones that load.

--record-ollama records the manifest digest of every Ollama model in the lock that the server has
pulled, so eval --verify-models can tell when a tag starts pointing at different weights.

Standard library only, so it runs on a bare CI runner.
"""
import hashlib
import json
import os
import sys
import urllib.request

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
LOCK = os.path.join(ROOT, "models.lock.json")
HF = "https://huggingface.co"


def get_json(url):
    request = urllib.request.Request(url, headers={"User-Agent": "priorauthbot-get-model"})
    with urllib.request.urlopen(request, timeout=60) as response:
        return json.load(response)


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def download(url, dest):
    tmp = dest + ".part"
    request = urllib.request.Request(url, headers={"User-Agent": "priorauthbot-get-model"})
    with urllib.request.urlopen(request, timeout=600) as response, open(tmp, "wb") as out:
        while True:
            block = response.read(1 << 20)
            if not block:
                break
            out.write(block)
    os.replace(tmp, dest)


def save(lock):
    with open(LOCK, "w", encoding="utf-8", newline="\n") as f:
        json.dump(lock, f, indent=2)
        f.write("\n")


def record_ollama(endpoint, lock):
    tags = {m["name"]: m["digest"] for m in get_json(endpoint.rstrip("/") + "/api/tags").get("models", [])}
    changed = 0
    for model in lock["models"]:
        if model["kind"] != "Ollama":
            continue
        name = model["source"].removeprefix("ollama:")
        digest = tags.get(name)
        if digest is None:
            print(f"{name}: not pulled on {endpoint}, left as it is")
        elif model.get("revision") in (None, ""):
            model["revision"] = digest
            changed += 1
            print(f"{name}: recorded {digest}")
        elif model["revision"] != digest:
            print(f"{name}: the server has {digest}, the lock pins {model['revision']}. NOT changed: pull the pinned version or review the new one.", file=sys.stderr)
            return 1
        else:
            print(f"{name}: matches {digest}")
    if changed:
        save(lock)
    return 0


def main(argv):
    with open(LOCK, encoding="utf-8") as f:
        lock = json.load(f)
    if argv[:1] == ["--record-ollama"]:
        return record_ollama(argv[1] if len(argv) > 1 else "http://localhost:11434", lock)

    args = [a for a in argv if not a.startswith("--")]
    record = "--record" in argv
    if not args:
        print(__doc__)
        return 2
    model_id = args[0]
    entry = next((m for m in lock["models"] if m["id"] == model_id), None)
    if entry is None:
        print(f"{model_id} is not in models.lock.json. Add an entry, with its license read and reviewedBy filled in, first.", file=sys.stderr)
        return 1
    if not entry["source"].startswith("huggingface:"):
        print(f"{model_id} comes from {entry['source']}; this script downloads Hugging Face models.", file=sys.stderr)
        return 1

    repo = entry["source"].removeprefix("huggingface:")
    folder = entry.get("path") or ""
    dest = args[1] if len(args) > 1 else os.path.join(ROOT, "models", model_id)
    pinned = bool(entry.get("files")) and all(f.get("sha256") for f in entry["files"])
    if not pinned and not record:
        print(f"{model_id} is not pinned yet. Run with --record, review the change to models.lock.json, and commit it.", file=sys.stderr)
        return 1

    # A branch name moves; a commit does not. Resolve whatever the lock says to a commit hash.
    revision = entry.get("revision") or "main"
    commit = get_json(f"{HF}/api/models/{repo}/revision/{revision}")["sha"]
    print(f"{repo} at {revision} is commit {commit}")
    if entry.get("revision") and entry["revision"] != commit:
        print(f"The lock pins {entry['revision']}, which is not a commit hash. Pin the commit.", file=sys.stderr)
        return 1

    tree = get_json(f"{HF}/api/models/{repo}/tree/{commit}/{folder}".rstrip("/"))
    files = [t for t in tree if t.get("type") == "file"]
    if not files:
        folders = [t["path"] for t in tree if t.get("type") == "directory"]
        print(f"No files at {repo}/{folder}. Folders there: {folders}", file=sys.stderr)
        return 1

    os.makedirs(dest, exist_ok=True)
    expected = {f["name"]: f for f in entry.get("files") or []}
    recorded, problems = [], []
    for item in sorted(files, key=lambda t: t["path"]):
        name = item["path"][len(folder):].lstrip("/") if folder else item["path"]
        if name.lower().endswith((".md", ".gitattributes")) or name.lower() == "license":
            continue
        if pinned and name not in expected:
            problems.append(f"{name} is in the repository but not in the lock.")
            continue
        target = os.path.join(dest, name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        if not (os.path.exists(target) and pinned and sha256(target) == expected[name]["sha256"]):
            print(f"downloading {name} ({item.get('size', 0):,} bytes)")
            download(f"{HF}/{repo}/resolve/{commit}/{item['path']}", target)
        actual = sha256(target)
        size = os.path.getsize(target)
        publisher = (item.get("lfs") or {}).get("oid")
        if publisher and publisher != actual:
            problems.append(f"{name}: SHA-256 {actual}, but Hugging Face records {publisher}.")
        if pinned and expected[name]["sha256"] != actual:
            problems.append(f"{name}: SHA-256 {actual}, but the lock says {expected[name]['sha256']}.")
        print(f"{name}  {size:>14,}  {actual}{'  (matches the publisher)' if publisher == actual else ''}")
        recorded.append({"name": name, "bytes": size, "sha256": actual})

    if pinned:
        missing = set(expected) - {r["name"] for r in recorded}
        problems.extend(f"{name} is in the lock but not in the repository at {commit}." for name in sorted(missing))
    if problems:
        print("\n".join(problems), file=sys.stderr)
        return 1
    if record and not pinned:
        entry["revision"] = commit
        entry["files"] = recorded
        save(lock)
        print(f"Recorded {len(recorded)} files for {model_id} at {commit} in models.lock.json. Review the diff, then commit it.")
    else:
        print(f"{model_id}: every file matches models.lock.json.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
