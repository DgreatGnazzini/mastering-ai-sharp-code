#!/usr/bin/env bash
# Starts a Container Apps job execution and waits for it (chapter 14). The job runs inside the
# virtual network, which is the only place the private endpoints and the internal API can be reached.
#   scripts/deploy/run-job.sh <resource-group> <job> <image> [NAME=value ...]
# Extra NAME=value pairs become environment variables of this execution only (the eval image reads
# its arguments from PRIORAUTHBOT_EVAL_ARGS). They replace the job template's variables for that
# execution, so pass every variable the execution needs, not only the new one.
# Exits 0 only if the execution succeeded. Prints the execution's console log either way.
set -euo pipefail
rg="$1"; job="$2"; image="$3"; shift 3

start=(az containerapp job start --resource-group "$rg" --name "$job" --image "$image" --query name -o tsv)
if [ "$#" -gt 0 ]; then start+=(--env-vars "$@"); fi
execution=$("${start[@]}")
echo "Started $job execution $execution"

for _ in $(seq 1 240); do
  status=$(az containerapp job execution show --resource-group "$rg" --name "$job" --job-execution-name "$execution" --query properties.status -o tsv)
  case "$status" in
    Succeeded|Failed|Stopped|Degraded) break ;;
  esac
  sleep 15
done

az containerapp job logs show --resource-group "$rg" --name "$job" --execution "$execution" --container "$(az containerapp job show -g "$rg" -n "$job" --query 'properties.template.containers[0].name' -o tsv)" --format text 2>/dev/null || true
echo "$job execution $execution: $status"
[ "$status" = "Succeeded" ]
