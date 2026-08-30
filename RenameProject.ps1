$ErrorActionPreference = 'Stop'

function Replace-InFile {
    param($Path)
    $content = Get-Content $Path -Raw -Encoding UTF8
    if (-not $content) { return }
    
    $newContent = $content.Replace("OpenTEC-Hub", "OpenTEC-Hub").Replace("OpenTECHub", "OpenTECHub").Replace("OpenTECHub", "OpenTECHub").Replace("OpenTECCommand", "OpenTECCommand").Replace("OpenTEC", "OpenTEC").Replace("OpenTEC", "OpenTEC").Replace("opentec", "opentec")
                           
    if ($content -cne $newContent) {
        Write-Host "Updating $Path"
        [IO.File]::WriteAllText($Path, $newContent, [System.Text.Encoding]::UTF8)
    }
}

$files = Get-ChildItem -File -Recurse -Exclude "*.png", "*.jpg", "*.ico", "*.dll", "*.exe", "*.obj", "*.pdb", "*.cache" | Where-Object { $_.FullName -notmatch "\\\.git\\" -and $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" }

foreach ($f in $files) {
    Replace-InFile -Path $f.FullName
}

$filesToRename = Get-ChildItem -File -Recurse | Where-Object { ($_.Name -match "OpenTEC" -or $_.Name -match "opentec" -or $_.Name -match "OpenTEC") -and $_.FullName -notmatch "\\\.git\\" -and $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" }
foreach ($f in $filesToRename) {
    $newName = $f.Name.Replace("OpenTEC", "OpenTEC").Replace("opentec", "opentec").Replace("OpenTEC", "OpenTEC")
    Rename-Item -Path $f.FullName -NewName $newName
}

$dirsToRename = Get-ChildItem -Directory -Recurse | Where-Object { ($_.Name -match "OpenTEC" -or $_.Name -match "opentec" -or $_.Name -match "OpenTEC") -and $_.FullName -notmatch "\\\.git\\" -and $_.FullName -notmatch "\\bin\\" -and $_.FullName -notmatch "\\obj\\" } | Sort-Object -Property @{Expression={$_.FullName.Length}; Descending=$true}
foreach ($d in $dirsToRename) {
    $newName = $d.Name.Replace("OpenTEC", "OpenTEC").Replace("opentec", "opentec").Replace("OpenTEC", "OpenTEC")
    Rename-Item -Path $d.FullName -NewName $newName
}
