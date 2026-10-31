#!/usr/bin/env bash
set -euo pipefail
if (( $# > 0 )); then
    exec dotnet /app/tools/PlantOps.Worker.dll "$@"
fi
if [[ "${PLANTOPS_INITIALIZE_DB:-false}" == 'true' ]]; then
    # Run before accepting requests and write seed files to this service's volume.
    dotnet /app/tools/PlantOps.Worker.dll --initialize-db
fi
exec dotnet /app/PlantOps.Api.dll
