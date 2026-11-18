#!/bin/sh
# Works from any working directory; credentials remain in the ignored local .env.
set -eu
exec python3 "$(dirname "$0")/stack.py" "$@"
