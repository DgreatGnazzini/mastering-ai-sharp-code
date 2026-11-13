#!/usr/bin/env bash
# Tags the current commit as the finished code for a chapter, after checking that it builds and tests.
# Usage: scripts/tag-chapter.sh 7      -> creates and pushes tag ch07
set -euo pipefail

if [[ $# -ne 1 || ! "$1" =~ ^[0-9]{1,2}$ ]]; then
  echo "usage: $0 <chapter-number>" >&2; exit 1
fi
tag=$(printf "ch%02d" "$((10#$1))")

if [[ -n "$(git status --porcelain)" ]]; then
  echo "Working tree is not clean; commit first." >&2; exit 1
fi
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
  echo "Tag $tag already exists. Published tags never move; for a fix use a chNN-r2 tag (see docs/CHAPTERS.md)." >&2; exit 1
fi

dotnet build MasteringAI.slnx --configuration Release
dotnet test MasteringAI.slnx --configuration Release --no-build

git tag -a "$tag" -m "Code for chapter $((10#$1))"
git push origin "$tag"
echo "Pushed $tag"
