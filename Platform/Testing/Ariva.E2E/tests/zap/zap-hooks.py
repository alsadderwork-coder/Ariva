# ARV-063: hooks for ZAP's packaged scans (zap-api-scan.py, zap-baseline.py --hook). Keeps the active scan and the
# spider off the sign-in and session endpoints, which would end the scan account's own session or lock it out, and off
# the live hub, whose WebSocket negotiation is not a REST API. zap.spec.ts also removes them from the OpenAPI documents,
# because ZAP's import calls every documented operation before these exclusions apply. Everything else is attacked.

EXCLUDED = [
    r".*/api/auth/.*",
    r".*/hubs/.*",
]


def zap_started(zap, target):
    for pattern in EXCLUDED:
        zap.ascan.exclude_from_scan(pattern)
        zap.spider.exclude_from_scan(pattern)
