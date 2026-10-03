#!/bin/sh
set -eu
# Run from this directory inside the pinned peer build stage. Only the test
# binary's linked module graph is included; no source files are imported here.
output="$(mktemp /tmp/tryagi-peer-notices.XXXXXX)"
normalized="$(mktemp /tmp/tryagi-peer-notices-normalized.XXXXXX)"
trap 'rm -f "$output" "$normalized"' EXIT
printf 'Independent local ICE/SRTP test peer: dependency notices\n' > "$output"
printf '\nThese dependencies belong to the isolated Go test peer, not tryAGI.WebRTC.\n' >> "$output"
printf '\nGo shared standard library\n' >> "$output"
go env GOVERSION >> "$output"
cat /usr/local/go/LICENSE >> "$output"
if [ -f /usr/local/go/PATENTS ]; then cat /usr/local/go/PATENTS >> "$output"; fi
go list -deps -f '{{if .Module}}{{.Module.Path}}|{{.Module.Version}}|{{.Module.Dir}}{{end}}' . | sort -u > /tmp/tryagi-peer-linked-modules.txt
while IFS='|' read -r module version directory; do
    if [ -z "$module" ] || [ "$module" = tryagi.local/webrtc-ice-peer ]; then continue; fi
    printf '\nModule: %s %s\n' "$module" "$version" >> "$output"
    found=false
    for name in LICENSE LICENSE.md LICENSE.txt COPYING COPYING.txt NOTICE PATENTS; do
        if [ -f "$directory/$name" ]; then
            printf 'Original file: %s\n' "$name" >> "$output"
            cat "$directory/$name" >> "$output"
            case "$name" in LICENSE*|COPYING*) found=true ;; esac
        fi
    done
    if [ "$found" = false ]; then printf 'Missing license for %s\n' "$module" >&2; exit 1; fi
done < /tmp/tryagi-peer-linked-modules.txt
# Retain original license words/line breaks; normalize trailing whitespace only.
sed 's/[[:blank:]]*$//' "$output" > "$normalized"
mv "$normalized" THIRD_PARTY_NOTICES.txt
