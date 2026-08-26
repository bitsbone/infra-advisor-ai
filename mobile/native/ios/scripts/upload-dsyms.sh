#!/bin/sh
set -eu

# Debug and simulator symbols cannot symbolicate production device crashes. Keeping this Release-only
# also prevents ordinary local builds from attempting network access.
if [ "${CONFIGURATION:-}" != "Release" ] || [ "${PLATFORM_NAME:-}" = "iphonesimulator" ]; then
    echo "Datadog dSYM upload skipped for ${CONFIGURATION:-unknown}/${PLATFORM_NAME:-unknown}."
    exit 0
fi

# Symbol upload uses an API key at build time, never the client token shipped in the app. CI or the
# developer's shell must provide this value; do not add it to an xcconfig or tracked JSON file.
if [ -z "${DATADOG_API_KEY:-}" ]; then
    echo "Datadog dSYM upload skipped: DATADOG_API_KEY is not set."
    exit 0
fi

DSYM_PATH="${DWARF_DSYM_FOLDER_PATH:-}"
if [ -z "$DSYM_PATH" ] || [ ! -d "$DSYM_PATH" ]; then
    echo "error: Datadog dSYM upload expected a dSYM directory at '$DSYM_PATH'." >&2
    exit 1
fi

if ! command -v npx >/dev/null 2>&1; then
    echo "error: npx is required to run @datadog/datadog-ci." >&2
    exit 1
fi

export DATADOG_SITE="${DATADOG_SITE:-us3.datadoghq.com}"
npx --yes "@datadog/datadog-ci@${DATADOG_CI_VERSION:-5.21.2}" dsyms upload "$DSYM_PATH"
