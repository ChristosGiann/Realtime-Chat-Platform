#!/bin/sh
set -eu

if [ "$(id -u)" = 0 ]; then
    # Railway mounts volumes as root. Only the dedicated key mount may be changed.
    if [ "${SharedDataProtection__KeyRingPath:-}" != /keys ] || [ ! -d /keys ]; then
        echo 'The persistent key volume must be mounted at /keys.' >&2
        exit 1
    fi
    chown 1654:1654 /keys
    chmod 700 /keys
    exec setpriv --reuid=1654 --regid=1654 --clear-groups dotnet Chat.Api.dll
fi

exec dotnet Chat.Api.dll
