#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
mkdir -p artifacts/spikes

baseline_project=spikes/MicrosoftBaseline/MicrosoftBaseline.csproj
candidate_project=spikes/ProtocolFeasibility/ProtocolFeasibility.csproj

dotnet restore "$baseline_project" --locked-mode
dotnet build "$baseline_project" --configuration Release --no-restore
dotnet spikes/MicrosoftBaseline/bin/Release/net10.0/MicrosoftBaseline.dll \
  -result-xml artifacts/spikes/microsoft-baseline.xml \
  | tee artifacts/spikes/microsoft-baseline.log

for candidate_version in 7.1.0 7.1.1; do
  candidate_lock=packages.lock.json
  if [[ "$candidate_version" == 7.1.0 ]]; then
    candidate_lock=packages.7.1.0.lock.json
  fi
  dotnet restore "$candidate_project" --locked-mode \
    -p:DuendeOidcClientVersion="$candidate_version" -p:NuGetLockFilePath="$candidate_lock"
  dotnet build "$candidate_project" --configuration Release --no-restore \
    -p:DuendeOidcClientVersion="$candidate_version" -p:NuGetLockFilePath="$candidate_lock"
  dotnet spikes/ProtocolFeasibility/bin/Release/net10.0/ProtocolFeasibility.dll \
    -result-xml "artifacts/spikes/duende-$candidate_version.xml" \
    | tee "artifacts/spikes/duende-$candidate_version.log"
done
