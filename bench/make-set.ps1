# Creates a benchmark set on a CM: N items under /sitecore/system/Modules/Tern Bench <Name>, in
# folders of at most 100 children. Sitecore assigns every ID and stamps the statistics, as it
# would for content an editor made; nothing is generated outside the CM.
#
# Run it through SPE: the Sitecore MCP server's run-powershell-script, `tern script run -f`, or
# the SPE console, after setting $Name, $Kind (content or media) and $Count at the top.
#
#   content  Sample/Sample Item, one en version with Title and Text
#   media    Unversioned files made by Sitecore's MediaCreator, each a 16 KB blob
#
# Items are added with events disabled, which is 30 times faster, and then each one is saved
# once with events on, which stamps __Created, __Revision and __Updated as an editor's save would.
# Re-running fills in what is missing and changes nothing else. It prints the count it ends with.
param([string]$Name, [ValidateSet("content", "media")][string]$Kind = "content", [int]$Count = 100)

$db = [Sitecore.Configuration.Factory]::GetDatabase("master")
$en = [Sitecore.Globalization.Language]::Parse("en")
$modules = $db.GetItem("/sitecore/system/Modules")
$folderTemplate = if ($Kind -eq "media") { "{FE5DD826-48C6-436D-B87A-7C4210C7413B}" } else { "{A87A00B1-E6DB-45AB-8B54-636FEC3B5523}" }
$folderT = $db.GetTemplate($folderTemplate)
$sampleT = $db.GetTemplate("{76036F5E-CBCE-46D1-AF0A-4143F9B557AA}")
$rootName = "Tern Bench $Name"

# Adds a child with events disabled. (New-UsingBlock runs its block in another scope, where this
# script's variables are not set.)
function Add-Quietly($parent, [string]$childName, $template) {
    $disabler = New-Object Sitecore.Data.Events.EventDisabler
    try { $parent.Add($childName, $template) } finally { $disabler.Dispose() }
}

function Stamp($item) {
    $i = $db.GetItem($item.ID, $en)
    if ($i.Fields["__Revision"].HasValue) { return }
    $i.Editing.BeginEdit()
    $i.Editing.EndEdit($true, $false) | Out-Null
}

$root = $modules.Children[$rootName]
if (-not $root) { $root = $modules.Add($rootName, $folderT) }
Stamp $root

$folders = [math]::Ceiling($Count / 100)
foreach ($fi in 0..($folders - 1)) {
    $fname = "f{0:D2}" -f $fi
    $f = $root.Children[$fname]
    if (-not $f) { $f = Add-Quietly $root $fname $folderT }
    Stamp $f
    $have = @{}; foreach ($c in $f.Children) { $have[$c.Name] = $c }
    foreach ($k in 0..([math]::Min(100, $Count - $fi * 100) - 1)) {
        $num = $fi * 100 + $k
        $name = "i{0:D7}" -f $num
        if ($Kind -eq "media") {
            if ($have[$name]) { continue }
            $rnd = New-Object System.Random $num
            $bytes = New-Object byte[] 16384; $rnd.NextBytes($bytes)
            $options = New-Object Sitecore.Resources.Media.MediaCreatorOptions
            $options.Destination = "$($f.Paths.FullPath)/$name"
            $options.Database = $db
            $options.Versioned = $false
            $options.IncludeExtensionInItemName = $false
            $stream = New-Object System.IO.MemoryStream (, $bytes)
            (New-Object Sitecore.Resources.Media.MediaCreator).CreateFromStream($stream, "$name.bin", $options) | Out-Null
            continue
        }
        $item = $have[$name]
        if (-not $item) { $item = Add-Quietly $f $name $sampleT }
        $i = $db.GetItem($item.ID, $en)
        if ($i.Fields["Title"].HasValue -and $i.Fields["__Revision"].HasValue) { continue }
        $i.Editing.BeginEdit()
        $i["Title"] = "Item $num"
        $i["Text"] = "<p>Bench item $num</p>"
        $i.Editing.EndEdit($true, $false) | Out-Null
    }
}

[Sitecore.Caching.CacheManager]::ClearAllCaches()
$root = $db.GetItem($root.ID)
"$rootName" + ": " + $root.Axes.GetDescendants().Count + " items below the root"
