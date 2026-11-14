#!/usr/bin/env bash
# Downloads the cross-encoder reranker for chapter 8: cross-encoder/ms-marco-MiniLM-L6-v2 as ONNX,
# with its WordPiece vocabulary, into models/ms-marco-MiniLM-L6-v2 (about 90 MB, Apache 2.0).
# The model folder is git-ignored. Set REVISION to a commit hash to pin the exact model files;
# the script prints their SHA-256 so you can record what you measured with.
set -euo pipefail

REVISION="${REVISION:-main}"
DEST="${1:-$(dirname "$0")/../models/ms-marco-MiniLM-L6-v2}"
mkdir -p "$DEST"

fetch() {
  # $1 = repository, $2 = file in it, $3 = local name
  curl -fsSL --retry 3 -o "$DEST/$3" "https://huggingface.co/$1/resolve/$REVISION/$2"
}

if ! fetch cross-encoder/ms-marco-MiniLM-L6-v2 onnx/model.onnx model.onnx; then
  echo "No ONNX export at cross-encoder/ms-marco-MiniLM-L6-v2@$REVISION; trying the Xenova export." >&2
  fetch Xenova/ms-marco-MiniLM-L-6-v2 onnx/model.onnx model.onnx
fi

fetch cross-encoder/ms-marco-MiniLM-L6-v2 vocab.txt vocab.txt || fetch Xenova/ms-marco-MiniLM-L-6-v2 vocab.txt vocab.txt

ls -l "$DEST"
sha256sum "$DEST/model.onnx" "$DEST/vocab.txt" 2>/dev/null || shasum -a 256 "$DEST/model.onnx" "$DEST/vocab.txt"
