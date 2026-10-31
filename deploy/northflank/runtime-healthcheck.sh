#!/usr/bin/env bash
set -euo pipefail
kind="${1:-ready}"
[[ "$kind" == ready || "$kind" == live ]]
for port in 8080 8081; do
    exec 3<>/dev/tcp/127.0.0.1/"$port"
    printf 'GET /health/%s HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' "$kind" >&3
    IFS= read -r status <&3
    [[ "$status" == *" 200 "* ]]
    exec 3>&-
done
