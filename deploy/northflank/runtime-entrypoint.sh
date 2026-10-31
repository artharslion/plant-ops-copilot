#!/usr/bin/env bash
set -euo pipefail

# Finite commands are Jobs: never start the simulator or background workers.
if (( $# > 0 )); then
    cd /app/worker
    exec dotnet PlantOps.Worker.dll "$@"
fi

worker_pid=''
simulator_pid=''
cleanup() {
    trap '' TERM INT
    if [[ -n "$worker_pid" ]]; then kill -TERM "$worker_pid" 2>/dev/null || true; fi
    if [[ -n "$simulator_pid" ]]; then kill -TERM "$simulator_pid" 2>/dev/null || true; fi
    # Bound shutdown even if one child stops responding to SIGTERM.
    ( sleep 15; kill -KILL ${worker_pid:+"$worker_pid"} ${simulator_pid:+"$simulator_pid"} 2>/dev/null || true ) &
    local watchdog_pid=$!
    wait "$worker_pid" "$simulator_pid" 2>/dev/null || true
    kill "$watchdog_pid" 2>/dev/null || true
    wait "$watchdog_pid" 2>/dev/null || true
}
trap 'cleanup; exit 0' TERM INT

(
    cd /app/simulator
    # Keep simulator-only URL/auth/storage settings out of the Worker process.
    export ASPNETCORE_URLS=http://0.0.0.0:8081
    export INDUSTRIALSIM_DEVICE_CONFIG=/app/config/device.yaml
    export ConnectionStrings__IndustrialSim='Data Source=/app/simulator-data/industrial-sim.db'
    export Auth__Mode=Disabled
    exec dotnet IndustrialSim.Web.dll
) &
simulator_pid=$!
(
    cd /app/worker
    exec dotnet PlantOps.Worker.dll
) &
worker_pid=$!

# A container with only one surviving child must not continue serving traffic.
set +e
wait -n "$worker_pid" "$simulator_pid"
status=$?
set -e
echo "A runtime process exited (status $status); stopping the combined service." >&2
cleanup
if (( status == 0 )); then status=1; fi
exit "$status"
