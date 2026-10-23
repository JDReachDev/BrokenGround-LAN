# Broken Ground LAN - dedicated server console.
# Starts the game headless as a server, shows its log and sends typed commands to it.
# The server stops with the "stop" command or when this window is closed.
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$exe = Join-Path $dir 'BrokenGround.exe'
$log = Join-Path $dir 'BGLan_server.log'
$cmd = Join-Path $dir 'BGLan_server_cmd.txt'
$unityLog = Join-Path $dir 'BGLan_server_unity.log'
$Host.UI.RawUI.WindowTitle = 'Broken Ground LAN - Dedicated Server'

if (-not (Test-Path $exe)) {
    Write-Host "BrokenGround.exe not found in $dir" -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

Remove-Item $log, $cmd -ErrorAction SilentlyContinue
$arguments = @('-batchmode', '-nographics', '-bglan-server', '-bglan-parent', $PID, '-logFile', "`"$unityLog`"")
$game = Start-Process -FilePath $exe -WorkingDirectory $dir -ArgumentList $arguments -PassThru

Write-Host 'Broken Ground LAN - Dedicated Server' -ForegroundColor Cyan
Write-Host 'Type help for the commands. Type stop (or close this window) to stop the server.' -ForegroundColor DarkGray
Write-Host ''

$position = 0
$typed = ''
while (-not $game.HasExited) {
    if (Test-Path $log) {
        try {
            $stream = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            if ($stream.Length -lt $position) { $position = 0 }
            [void]$stream.Seek($position, 'Begin')
            $reader = New-Object IO.StreamReader($stream)
            $text = $reader.ReadToEnd()
            $position = $stream.Position
            $reader.Close()
            foreach ($line in ($text -split "`r?`n")) {
                if ($line -eq '') { continue }
                $color = if ($line -match '\[ERROR\]') { 'Red' } elseif ($line -match '\[WARN\]') { 'Yellow' } else { 'Gray' }
                Write-Host $line -ForegroundColor $color
            }
        } catch { }
    }
    while ([Console]::KeyAvailable) {
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq 'Enter') {
            Write-Host ''
            if ($typed.Trim() -ne '') { Add-Content -Path $cmd -Value $typed }
            $typed = ''
        } elseif ($key.Key -eq 'Backspace') {
            if ($typed.Length -gt 0) {
                $typed = $typed.Substring(0, $typed.Length - 1)
                Write-Host -NoNewline "`b `b"
            }
        } elseif ($key.KeyChar -ne [char]0) {
            $typed += $key.KeyChar
            Write-Host -NoNewline $key.KeyChar -ForegroundColor White
        }
    }
    Start-Sleep -Milliseconds 200
}

Write-Host ''
Write-Host 'The server has stopped.' -ForegroundColor Cyan
Start-Sleep -Seconds 3
