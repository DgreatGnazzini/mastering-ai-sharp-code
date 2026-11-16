# Upgrade playbook: models and AI packages

Chapter 15. Every change to a model, an embedding model, or an AI package is a migration. This is
the procedure, and the evidence each kind of change needs before it merges.

## The four steps

1. **Pin.** Nothing changes by itself. Packages are pinned in `Directory.Packages.props`, model
   weights in `models.lock.json` (by SHA-256 or Ollama digest), Azure OpenAI deployments by version
   with `NoAutoUpgrade` (`infra/main.bicep`, chapter 14).
2. **Evaluate.** The candidate runs the whole eval suite and is gated against the *current* model's
   baselines (`--baseline-provider`). The model comparison report puts both side by side.
3. **Canary.** In Azure, the new revision gets no traffic until its smoke test and gates pass, then
   10 percent for 15 minutes (chapter 14). Locally or on-premises, run the new model beside the old
   one and compare before switching.
4. **Roll back.** Keep the old thing runnable until the new one has held: the previous revision and
   its model deployment, the old Ollama tag still pulled, the old vector collection still in place.

## What each change needs

| Change | Run | Evidence in the pull request | Who approves |
|---|---|---|---|
| A patch or minor package version | CI on the pull request | Green CI, including the offline eval gate | Any maintainer |
| A major version, or any 0.x minor (`0.19` to `0.20`) | CI, plus the RAG eval job | The breaking-change notes read and summarized; the eval gate | A maintainer who read the notes |
| A new chat model version (same family) | `Model upgrade` workflow, or the deploy pipeline's gates | The model comparison report; the lock entry | CODEOWNERS for `eval/baselines.json` |
| A new chat model family or a new provider | As above, plus the red-team suite with and without guardrails | Comparison report; license review in `models.lock.json` (`reviewedBy`); whether the provider is approved for PHI | AppSec and the privacy officer |
| A new embedding model | Build a new collection beside the old one (`VectorStore:Collection`), retrieval eval and leak checks on it | Retrieval numbers on the new collection; the leak count (must be 0); the re-calibrated `Retrieval:MinScore` | CODEOWNERS; the old collection stays until no revision reads it |
| New local weights (an Ollama tag that moved, a new ONNX folder) | `scripts/get-model.py --record` or `--record-ollama`, then the eval | The diff to `models.lock.json`, which names the commit or digest | AppSec |

## Changing the embedding model

Vectors from two models are not comparable. PriorAuthBot stamps each chunk with its embedding model
(`ChunkRecord.EmbeddingModel`), ingestion refuses to write one model's vectors into another's
collection, and retrieval refuses a chunk from another model. So the change is a blue-green rebuild:

1. Add the new collection name to `Migrate:Collections` and run the migration job. It creates and
   grants the new table; the API role still cannot create anything.
2. Release a revision with the new embedding model and `VectorStore:Collection` set to the new name.
   Its ingestion fills the new collection; readiness waits for it. The old revision keeps serving
   from the old collection.
3. The deploy pipeline's gates run against the new revision: retrieval, leak checks, red team.
   Calibrate `Retrieval:MinScore` for the new model (chapter 7) before you trust the decline rate.
4. Promote. Drop the old collection only after no revision can be rolled back to it.

For a local index you can rebuild at will, `VectorStore:OnEmbeddingModelChange=Reembed` re-embeds in
place instead.

## Changing local weights

`models.lock.json` lists every model PriorAuthBot may load, with its source, revision, license, who
reviewed the license, and every file's hash. An ONNX model whose files do not match is not loaded;
an Ollama model is checked by digest with `eval --verify-models`. To add or update one:

1. Add or edit the entry by hand: source, path, license, license URL, `commercialUse`, `reviewedBy`.
   A script never sets `commercialUse`.
2. `python3 scripts/get-model.py <id> --record` (Hugging Face) or
   `python3 scripts/get-model.py --record-ollama <endpoint>` (Ollama) fills in the commit or digest
   and the hashes.
3. Review the diff, run the eval, and open the pull request.

## Weekly: the latest-packages run

`.github/workflows/latest-packages.yml` builds, tests, and runs the offline eval gate against the
newest version of every package every Monday, and lists what is newer in its summary. It never
commits. When it goes red, the next upgrade has a known cost; when it lists a major or 0.x change,
read the notes before you need to.
