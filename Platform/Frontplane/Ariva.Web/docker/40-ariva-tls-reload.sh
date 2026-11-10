#!/bin/sh
# ARV-198 (CWE-295, CWE-400): the web pod serves a renewed certificate without a restart. With pod TLS the chart mounts the
# web certificate's secret as a whole directory at /etc/nginx/tls, which the kubelet updates in place (an atomic swap of its
# ..data link) when the secret is renewed; nginx reads the certificate only when it starts or reloads. The nginx image runs
# this script before nginx starts (/docker-entrypoint.d); it starts a loop that compares the certificate and key with the ones
# nginx loaded, every ARIVA_TLS_RELOAD_SECONDS (20 by default, 5 to 300), and on a change, once nginx accepts the new
# configuration (nginx -t), reloads it gracefully (nginx -s reload): new connections get the new certificate, open ones finish
# on the old workers, so none is dropped. A certificate nginx refuses is logged and the previous one stays served (the loop
# tries again at each interval). A renewal is served at most one interval after the kubelet swaps the files (the kubelet's own
# delay, about a minute by default, comes first). Without pod TLS there is no /etc/nginx/tls/tls.crt and the script does
# nothing. It runs as the image's user (10001), as nginx does, and writes nothing but its log lines.
set -eu

dir=/etc/nginx/tls
[ -f "$dir/tls.crt" ] || exit 0

interval="${ARIVA_TLS_RELOAD_SECONDS:-20}"
# At most three digits before the range check, so a number the shell's test cannot compare (it would fail as false and pass)
# never reaches it.
case "$interval" in
    ''|*[!0-9]*|????*) echo "40-ariva-tls-reload: ARIVA_TLS_RELOAD_SECONDS must be a whole number of seconds from 5 to 300" >&2; exit 1 ;;
esac
if [ "$interval" -lt 5 ] || [ "$interval" -gt 300 ]; then
    echo "40-ariva-tls-reload: ARIVA_TLS_RELOAD_SECONDS must be a whole number of seconds from 5 to 300" >&2
    exit 1
fi

fingerprint() {
    cat "$dir/tls.crt" "$dir/tls.key" 2>/dev/null | sha256sum | cut -d ' ' -f 1
}

(
    loaded="$(fingerprint)"
    refused=''
    while :; do
        if ! sleep "$interval"; then
            echo "40-ariva-tls-reload: the watcher stopped (sleep failed); a renewed certificate is served only after a restart" >&2
            exit 1
        fi
        current="$(fingerprint)"
        [ "$current" = "$loaded" ] && continue
        if nginx -t -q >/dev/null 2>&1 && nginx -s reload >/dev/null 2>&1; then
            loaded="$current"
            refused=''
            echo "40-ariva-tls-reload: a renewed certificate in $dir is served (nginx reloaded)"
        elif [ "$current" != "$refused" ]; then
            # Logged once per refused certificate; it is tried again at every interval.
            refused="$current"
            echo "40-ariva-tls-reload: the certificate in $dir changed but nginx refused it or could not reload; the previous one is still served" >&2
        fi
    done
) </dev/null &

echo "40-ariva-tls-reload: watching $dir every $interval s for a renewed certificate"
