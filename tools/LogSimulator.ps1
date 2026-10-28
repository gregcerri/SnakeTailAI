<#
.SYNOPSIS
    Writes VIU-style log lines to a file and rotates it like the real logger (x.log -> x.log.1 -> ... -> x.log.N).
    Use it to try SnakeTail AI: live tail, filters, Clear, Pause, rotation.

.EXAMPLE
    .\LogSimulator.ps1
    .\LogSimulator.ps1 -Folder C:\Temp\ViuLog\ViuPC-127.0.0.1 -LinesPerSecond 400 -RotateAtMB 2
#>
param(
    [string]$Folder = (Join-Path $env:TEMP "SnakeTailAI-sim\ViuPC-127.0.0.1"),
    [int]$LinesPerSecond = 150,
    [double]$RotateAtMB = 1,
    [int]$MaxArchives = 5,
    [int]$DurationSeconds = 0
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $Folder | Out-Null
$name = "viu-" + ((Split-Path $Folder -Leaf) -replace '^ViuPC-', '') + ".log"
$path = Join-Path $Folder $name
Write-Host "Writing to $path  ($LinesPerSecond lines/s, rotating at $RotateAtMB MB). Ctrl+C to stop."

$sources = @(
    @{ File = "Acquisition.cpp"; Fn = "acquisition_OnImageAcquired"; Msg = "acquisition_OnImageAcquired" },
    @{ File = "ElabFlowSnussEtichetta.cpp"; Fn = "run"; Msg = "End ElabFlowSnussEtichetta::run" },
    @{ File = "PVIUAppSnussEtichetta.cpp"; Fn = "onElaboration"; Msg = "[200::APP_Snus-Label-TOP] appSnuss elabFlow run Done" },
    @{ File = "IElabFlow.h"; Fn = "updateCheck"; Msg = "updating check NotchPosition of value: {0:N6}" },
    @{ File = "IElabFlow.h"; Fn = "updateCheck"; Msg = "check PASSED" },
    @{ File = "snussComponentEtichetta.cpp"; Fn = "clear"; Msg = "SnussComponentEtichetta::clear() DONE" },
    @{ File = "SnussElabEtichetta.cpp"; Fn = "DetectAndCompute"; Msg = "Num keypoints: {1} appName:200::APP_Snus-Label-TOP" }
)
$rare = @(
    @{ Level = "WARNING"; File = "snussComponentLaterale.cpp"; Fn = "elab"; Msg = "Roi TaxStamp non impostata" },
    @{ Level = "ERROR"; File = "snussComponentEtichetta.cpp"; Fn = "elab"; Msg = "Not enough matches. NMatches = {1}" },
    @{ Level = "INFO"; File = "PVIUAppSnussEtichetta.cpp"; Fn = "saveRejects"; Msg = "[200::APP_Snus-Label-TOP] saveRejects SELECTED - appTypeID = 22" }
)
$rand = [Random]::new()
$threads = @("0-31606", "10-31584", "12-31120", "16-31130", "7-31123", "2-31124")
$start = Get-Date
$batchMs = 100
$perBatch = [Math]::Max(1, [int]($LinesPerSecond * $batchMs / 1000))

function Rotate {
    $oldest = "$path.$MaxArchives"
    if (Test-Path $oldest) { Remove-Item $oldest -Force }
    for ($i = $MaxArchives - 1; $i -ge 1; $i--) {
        if (Test-Path "$path.$i") { Move-Item "$path.$i" "$path.$($i + 1)" -Force }
    }
    if (Test-Path $path) { Move-Item $path "$path.1" -Force }
    Write-Host ("{0:HH:mm:ss} rotated" -f (Get-Date))
}

while ($true) {
    $sb = [System.Text.StringBuilder]::new()
    for ($n = 0; $n -lt $perBatch; $n++) {
        $now = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss.ffffff")
        $thread = $threads[$rand.Next($threads.Count)]
        if ($rand.NextDouble() -lt 0.03) {
            $e = $rare[$rand.Next($rare.Count)]
            $level = $e.Level
        } else {
            $e = $sources[$rand.Next($sources.Count)]
            $level = "DEBUG"
        }
        $msg = [string]::Format($e.Msg, 80 + $rand.NextDouble() * 10, $rand.Next(5, 1200))
        [void]$sb.AppendLine("$now $level [$thread] Line $($rand.Next(20, 900)) - $($e.File)::$($e.Fn)(): $msg")
    }
    [System.IO.File]::AppendAllText($path, $sb.ToString())

    if ((Get-Item $path).Length -ge $RotateAtMB * 1MB) { Rotate }
    if ($DurationSeconds -gt 0 -and ((Get-Date) - $start).TotalSeconds -ge $DurationSeconds) { break }
    Start-Sleep -Milliseconds $batchMs
}
