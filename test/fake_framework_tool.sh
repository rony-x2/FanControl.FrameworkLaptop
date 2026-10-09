#!/bin/bash
# Simuliert framework_tool (Framework 16, zwei Lüfter)
D=$(dirname "$0")
echo "$(date +%s.%N) $*" >> "$D/calls.log"
st="$D/state"; [ -f "$st" ] || echo "auto auto" > "$st"
read f0 f1 < "$st"
case "$1" in
  --fansetduty)
    if [ $# -eq 3 ]; then [ "$2" = 0 ] && f0=$3 || f1=$3; else f0=$2; f1=$2; fi
    echo "$f0 $f1" > "$st";;
  --autofanctrl)
    if [ -n "$2" ]; then [ "$2" = 0 ] && f0=auto || f1=auto; else f0=auto; f1=auto; fi
    echo "$f0 $f1" > "$st";;
  --thermal)
    rpm(){ [ "$1" = auto ] && echo 2100 || echo $(( $1 * 55 )); }
    cat <<OUT
  F75303_Local: 40 C
  F75303_CPU:   38 C
  F75303_DDR:   38 C
  APU:          61 C
  dGPU VR:      0 C
  dGPU temp:    NotPowered
  Fan Speed:  $(rpm $f0) RPM
  Fan Speed:  $(rpm $f1) RPM
  AP Throttle Status
    Soft (AMD SPPT):  false
    Hard (PROCHOT#):  false
OUT
    ;;
esac
