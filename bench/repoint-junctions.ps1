# Re-points every CLI 7 mirror's .sitecore\package-cache junction at the plugin cache outside %TEMP%.
# The workspace and tools folders are suite.sh's: TERN_BENCH and TERN_BENCH_TOOLS, under the home folder by default.
$bench = $env:TERN_BENCH ?? (Join-Path $HOME 'tern-bench')
$tools = $env:TERN_BENCH_TOOLS ?? (Join-Path $HOME 'tern-bench-tools')
$root = Join-Path $bench 'v7'
$target = Join-Path $tools 'v7plugins\.sitecore\package-cache'
$count = 0
Get-ChildItem -Path $root -Directory -Recurse -Force -Filter '.sitecore' -ErrorAction SilentlyContinue | ForEach-Object {
    $cache = Join-Path $_.FullName 'package-cache'
    $item = Get-Item -LiteralPath $cache -Force -ErrorAction SilentlyContinue
    if ($item -and $item.LinkType -eq 'Junction') {
        # Removing the junction itself, never what it points at.
        [System.IO.Directory]::Delete($cache, $false)
        New-Item -ItemType Junction -Path $cache -Target $target | Out-Null
        $count++
    }
}
"re-pointed $count junctions"
