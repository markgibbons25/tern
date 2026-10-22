# Prints a running process's CPU time (ms), working set and peak working set (bytes), space-separated.
# suite.sh samples a watcher with it, since a watcher runs until it is stopped and measure.exe only
# reports on exit.
param([Parameter(Mandatory)][int]$Id)
$p = Get-Process -Id $Id -ErrorAction Stop
"{0} {1} {2}" -f [long]$p.TotalProcessorTime.TotalMilliseconds, $p.WorkingSet64, $p.PeakWorkingSet64
