#!/usr/bin/env bash
# Runs the headend against synthetic muxes (no tuner or aerial needed).
#   tools/make_test_mux.py /tmp/testdata && tools/run_simulated.sh /tmp/testdata
set -euo pipefail
DATA="${1:-/tmp/testdata}"
cd "$(dirname "$0")/../src/DvbHeadend"
args=(--Urls=http://127.0.0.1:5004 --Headend:DataDir=/tmp/dvbh --Headend:RecordingsDir=/tmp/dvbh/recordings
      --Headend:EpgGrabIntervalMinutes=0 --Headend:IdleReleaseSeconds=5)
for i in 0 1; do
  args+=("--Headend:FileTuners:$i:Id=sim$i" "--Headend:FileTuners:$i:BitrateMbps=5"
         "--Headend:FileTuners:$i:Files:674000000=$DATA/mux_a.ts"
         "--Headend:FileTuners:$i:Files:626000000=$DATA/mux_b.ts")
done
exec dotnet run -c Release --no-launch-profile -- "${args[@]}"
