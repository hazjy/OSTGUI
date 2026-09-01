param([int]$Width = 360)
$cfg = "$env:LOCALAPPDATA\OSTGUI\config.json"
$raw = [IO.File]::ReadAllText($cfg)
$raw = $raw -replace '"NavigationPaneWidth":\s*\d+', "`"NavigationPaneWidth`": $Width"
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText($cfg, $raw, $utf8)
Write-Host "NavigationPaneWidth set to $Width. Restart app to apply."
