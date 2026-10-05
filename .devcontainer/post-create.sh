#!/usr/bin/env bash
# ARV-076: after the dev container is created, restore everything the gates need: npm from the lock files, NuGet from the
# central package versions (Directory.Packages.props; NuGet lock files are an open ASVS item, ARV-096).
set -euo pipefail
cd "$(dirname "$0")/.."
npm ci --prefix Platform/Frontplane/Ariva.Web
npm ci --prefix Platform/Testing/Ariva.E2E
npm ci --prefix Platform/Cloud/Ariva.K8s/tests
dotnet restore Ariva.slnx
dotnet tool restore
# dotnet-stryker has its own manifest beside the unit tests (ARV-069).
(cd Platform/Backplane/Ariva.UnitTests && dotnet tool restore)
echo "Ariva dev container ready: node scripts/verify.mjs all (see docs/harness/README.md, Dev container)."
