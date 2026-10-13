#!/usr/bin/env bash
# Wait for a Deployment's rollout; on timeout, re-pin it to the image it ran
# before (with pin-deployment.sh, so DD_VERSION follows) and exit non-zero.
#
# Usage: rollout-or-rollback.sh <namespace> <deployment> <container> <previous-image|""> [timeout]
set -uo pipefail

namespace="$1" deployment="$2" container="$3" previous="$4" timeout="${5:-10m}"
here="$(dirname "$0")"

if kubectl rollout status deployment/"$deployment" -n "$namespace" --timeout="$timeout"; then
  echo "OK: $deployment rollout finished"
  exit 0
fi

if [ -z "$previous" ]; then
  echo "::error::$deployment rollout did not finish, and no previous image was captured — investigate now"
  exit 1
fi

echo "::error::$deployment rollout did not finish — rolling back to $previous"
"$here/pin-deployment.sh" "$namespace" "$deployment" "$container" "$previous"
kubectl rollout status deployment/"$deployment" -n "$namespace" --timeout=5m \
  && echo "::warning::$deployment rolled back to $previous; check the new image's startup logs" \
  || echo "::error::$deployment rollback to $previous also failed to stabilize — investigate now"
exit 1
