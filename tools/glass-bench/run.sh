#!/usr/bin/env bash
cd "$(dirname "$0")"
exe="bin/Release/net8.0-windows10.0.26100.0/GlassBench.exe"
rm -rf results; mkdir -p results
run() { name=$1; shift; timeout 170 ./$exe run "$@" "$(cygpath -w "$PWD/results/$name")" "$name"; sleep 3; }
run a-control none 30 1 1 under1
run b-idle100 mag 30 1 1 under1-event-hooks
run c-control none 30 1 1 under1
run d-idle33 mag 30 1 1 under1-event-hooks-idle33
run e-control-2mon none 30 1 2 under1
run f-idle33-2mon mag 30 1 2 under1-event-hooks-idle33
for d in results/*/; do echo "=== $(basename $d)"; grep -E "PHASE (static|animation-far|animated |animated-again)|WAKE|window events|WATCHDOG|FATAL" "$d/live.log" | cut -c14-; done > results/summary.txt
echo finished
