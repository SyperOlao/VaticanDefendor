#!/bin/sh
set -eu

server_binary=${VD_SERVER_EXECUTABLE:-/opt/server/VaticanDefendorServer.x86_64}
case "$server_binary" in
    /*) ;;
    *) printf '%s\n' '[ServerSupervisor] Executable path must be absolute.' >&2; exit 2 ;;
esac
if [ ! -x "$server_binary" ]; then
    printf '%s\n' '[ServerSupervisor] Dedicated Server executable is missing or not executable.' >&2
    exit 2
fi

stop_directory=$(mktemp -d "${TMPDIR:-/tmp}/vatican-stop.XXXXXX")
cleanup() {
    rm -f "$stop_directory/request"
    rmdir "$stop_directory"
}
request_stop() {
    printf '%s\n' '[ServerSupervisor] Stop signal received; requesting Unity shutdown.' >&2
    : > "$stop_directory/request"
}
trap cleanup EXIT
trap request_stop TERM INT

cd "$(dirname "$server_binary")"
"$server_binary" -batchmode -nographics -logFile - -vdRole server \
    -vdShutdownFile "$stop_directory/request" "$@" &
server_pid=$!

# wait is interrupted by a trapped signal. Keep supervising until Unity has run
# Application.Quit and released Worlds/services; preserve its actual exit status.
while :; do
    if wait "$server_pid"; then
        server_status=0
        break
    else
        server_status=$?
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
        break
    fi
done
exit "$server_status"
