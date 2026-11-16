#!/usr/bin/env bash
# Downloads the cross-encoder reranker for chapter 8: cross-encoder/ms-marco-MiniLM-L6-v2 as ONNX,
# with its WordPiece vocabulary, into models/ms-marco-MiniLM-L6-v2 (about 90 MB, Apache 2.0).
# The model folder is git-ignored. The files are checked against the SHA-256 hashes of the files
# chapter 8 was measured with; if Hugging Face serves different files, the script fails. Set
# REVISION to a commit hash to fetch a specific version, and SKIP_HASH_CHECK=1 to accept new files
# deliberately (then record their hashes here, and re-run the chapter 8 measurements).
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

EXPECTED_MODEL=5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a
EXPECTED_VOCAB=07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3

hash() { sha256sum "$1" 2>/dev/null | cut -d' ' -f1 || shasum -a 256 "$1" | cut -d' ' -f1; }
ls -l "$DEST"
MODEL_HASH=$(hash "$DEST/model.onnx")
VOCAB_HASH=$(hash "$DEST/vocab.txt")
echo "model.onnx $MODEL_HASH"
echo "vocab.txt  $VOCAB_HASH"
if [ "${SKIP_HASH_CHECK:-0}" != "1" ] && { [ "$MODEL_HASH" != "$EXPECTED_MODEL" ] || [ "$VOCAB_HASH" != "$EXPECTED_VOCAB" ]; }; then
  echo "The downloaded files do not match the ones chapter 8 was measured with. See the comment at the top of this script." >&2
  exit 1
fi
