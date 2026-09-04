param(
    [Parameter(Mandatory = $true)]
    [string]$UnityProject,

    [Parameter(Mandatory = $true)]
    [string]$ResponseFile,

    [Parameter(Mandatory = $true)]
    [string]$UnityEditor,

    [string]$PackageRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$responsePath = [IO.Path]::GetFullPath((Join-Path $UnityProject $ResponseFile))
if (-not (Test-Path -LiteralPath $responsePath)) {
    throw "Generated response file was not found: $responsePath"
}

$editorRoot = [IO.Path]::GetFullPath($UnityEditor)
$mono = Join-Path $editorRoot "Data\MonoBleedingEdge\bin\mono.exe"
$compiler = Join-Path $editorRoot "Data\MonoBleedingEdge\lib\mono\msbuild\Current\bin\Roslyn\csc.exe"
$temporaryStem = Join-Path ([IO.Path]::GetTempPath()) ("AIUnityMCPServer-Generated-" + [Guid]::NewGuid().ToString("N"))
$temporaryResponse = $temporaryStem + ".rsp"
$temporaryOutput = $temporaryStem + ".dll"
$temporaryReference = $temporaryStem + ".ref.dll"
$packageEditor = (Join-Path ([IO.Path]::GetFullPath($PackageRoot)) "Editor").Replace('\', '/')
$sourceCount = 0

$arguments = Get-Content -LiteralPath $responsePath | ForEach-Object {
    if ($_ -match '^"Library/PackageCache/com\.villadiego\.ai-mcp-unity-server@[^/]+/Editor/(.+)"$') {
        $sourceCount++
        '"' + $packageEditor + '/' + $Matches[1] + '"'
    }
    elseif ($_ -like '-out:*') {
        '-out:"' + $temporaryOutput.Replace('\', '/') + '"'
    }
    elseif ($_ -like '-refout:*') {
        '-refout:"' + $temporaryReference.Replace('\', '/') + '"'
    }
    else {
        $_
    }
}

if ($sourceCount -eq 0) {
    throw "The response file did not contain package Editor sources."
}

[IO.File]::WriteAllLines($temporaryResponse, $arguments)
try {
    Push-Location $UnityProject
    try {
        & $mono $compiler "@$temporaryResponse"
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    finally {
        Pop-Location
    }
    Write-Output "Generated response-file compilation passed with $sourceCount local package sources."
}
finally {
    Remove-Item -LiteralPath $temporaryResponse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporaryOutput -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporaryReference -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath ($temporaryStem + ".pdb") -Force -ErrorAction SilentlyContinue
}
