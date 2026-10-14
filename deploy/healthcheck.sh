#!/usr/bin/env bash
set -euo pipefail

# Bash is already included in the Ubuntu ASP.NET image; no extra probe package.
exec 3<>/dev/tcp/127.0.0.1/8080
printf 'GET /health/ready HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n' >&3
IFS= read -r status <&3
[[ "$status" == *" 200 "* ]]
