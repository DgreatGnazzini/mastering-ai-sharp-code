#!/usr/bin/env bash
# Compares the canary revision's failure rate with the stable revision's over the canary window
# (chapter 14), from the requests Application Insights recorded. Fails when the canary's rate of 5xx
# responses is above the limit, or more than twice the stable revision's.
#   scripts/deploy/canary-health.sh <log-analytics-workspace-guid> <canary-revision> <stable-revision> <minutes> <max-rate>
set -euo pipefail
workspace="$1"; canary="$2"; stable="$3"; minutes="$4"; max_rate="$5"

query="AppRequests
| where TimeGenerated > ago(${minutes}m)
| extend revision = case(AppRoleInstance startswith '${canary}', 'canary', AppRoleInstance startswith '${stable}', 'stable', 'other')
| where revision != 'other'
| summarize requests = count(), failures = countif(toint(ResultCode) >= 500) by revision
| extend rate = todouble(failures) / requests"

result=$(az monitor log-analytics query --workspace "$workspace" --analytics-query "$query" -o json)
echo "$result"
python3 - "$result" "$max_rate" <<'PY'
import json, sys
rows = {r["revision"]: r for r in json.loads(sys.argv[1])}
limit = float(sys.argv[2])
canary, stable = rows.get("canary"), rows.get("stable")
if canary is None or int(canary["requests"]) == 0:
    sys.exit("The canary served no requests in the window: nothing to judge, so it is not promoted.")
rate = float(canary["rate"])
stable_rate = float(stable["rate"]) if stable else 0.0
print(f"canary {rate:.4f} over {canary['requests']} requests, stable {stable_rate:.4f}, limit {limit:.4f}")
if rate > limit or (stable and rate > 2 * stable_rate and rate > 0.001):
    sys.exit("The canary fails more often than allowed.")
PY
