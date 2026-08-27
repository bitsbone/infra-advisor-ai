#!/bin/sh
set -eu

namespace="${AIRFLOW_NAMESPACE:-airflow}"
release="${AIRFLOW_RELEASE:-airflow}"
expected_image="${EXPECTED_AIRFLOW_IMAGE:-}"
failed=0

for command in helm kubectl jq; do
    if ! command -v "$command" >/dev/null 2>&1; then
        echo "airflow-preflight: required command is missing: $command" >&2
        exit 2
    fi
done

release_status="$(helm status "$release" --namespace "$namespace" -o json 2>/dev/null | jq -r '.info.status // "unknown"' || true)"
if [ "$release_status" != "deployed" ]; then
    echo "airflow-preflight: release status is '$release_status'; take a metadata backup and perform operator-reviewed recovery before upgrading" >&2
    failed=1
fi

images="$(kubectl get deployment,statefulset,pod --namespace "$namespace" -o jsonpath='{range .items[*]}{range .spec.template.spec.initContainers[*]}{.image}{"\n"}{end}{range .spec.template.spec.containers[*]}{.image}{"\n"}{end}{range .spec.initContainers[*]}{.image}{"\n"}{end}{range .spec.containers[*]}{.image}{"\n"}{end}{end}' | grep 'airflow' | sort -u || true)"
image_count="$(printf '%s\n' "$images" | sed '/^$/d' | wc -l | tr -d ' ')"
if [ "$image_count" -ne 1 ]; then
    echo "airflow-preflight: expected one Airflow workload image, found $image_count" >&2
    printf '%s\n' "$images" | sed '/^$/d' >&2
    failed=1
elif [ -n "$expected_image" ] && [ "$images" != "$expected_image" ]; then
    echo "airflow-preflight: workload image '$images' does not match expected immutable image '$expected_image'" >&2
    failed=1
fi

if ! kubectl get secret airflow-azure-secret --namespace "$namespace" -o json | jq -e '.data.AZURE_STORAGE_CONNECTION_STRING != null and .data.AZURE_STORAGE_CONNECTION_STRING != ("DefaultEndpointsProtocol=https;AccountName=placeholder;AccountKey=placeholder;EndpointSuffix=core.windows.net" | @base64) and .data.SAMGOV_API_KEY != null' >/dev/null; then
    echo "airflow-preflight: airflow-azure-secret requires a non-placeholder storage connection and SAMGOV_API_KEY" >&2
    failed=1
fi

if ! kubectl get secret ghcr-pull-secret --namespace "$namespace" -o json | jq -e '.type == "kubernetes.io/dockerconfigjson" and .data[".dockerconfigjson"] != null' >/dev/null; then
    echo "airflow-preflight: ghcr-pull-secret is missing or invalid in namespace '$namespace'" >&2
    failed=1
fi

if [ "$failed" -ne 0 ]; then
    exit 1
fi

kubectl exec --namespace "$namespace" deployment/airflow-api-server -c api-server -- airflow db check >/dev/null
kubectl exec --namespace "$namespace" deployment/airflow-api-server -c api-server -- airflow db check-migrations --migration-wait-timeout 5 >/dev/null
echo "airflow-preflight: release, image, application secret, registry secret, database, and migration checks passed"
