#!/usr/bin/env bash
# Pin a Deployment's container to an image and stamp that image's tag as the
# Datadog version, in one patch (one rollout):
#   - container image
#   - DD_VERSION env (explicit env overrides the ConfigMap's envFrom "latest")
#   - tags.datadoghq.com/version on the Deployment and its pod template
#
# Usage: pin-deployment.sh <namespace> <deployment> <container> <image:tag> [extra-args...]
# Extra args go to kubectl patch (for example --dry-run=server).
set -euo pipefail

namespace="$1" deployment="$2" container="$3" image="$4"
shift 4
version="${image##*:}"

patch=$(printf '{"metadata":{"labels":{"tags.datadoghq.com/version":"%s"}},"spec":{"template":{"metadata":{"labels":{"tags.datadoghq.com/version":"%s"}},"spec":{"containers":[{"name":"%s","image":"%s","env":[{"name":"DD_VERSION","value":"%s"}]}]}}}}' \
  "$version" "$version" "$container" "$image" "$version")

kubectl patch deployment/"$deployment" -n "$namespace" --type=strategic -p "$patch" "$@"
