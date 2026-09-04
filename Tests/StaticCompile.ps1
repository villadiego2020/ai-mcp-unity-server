param(
    [Parameter(Mandatory = $true)]
    [string]$UnityProject,

    [string]$PipelinePackage
)

$ErrorActionPreference = "Stop"
$projectFile = Join-Path $UnityProject "AIUnityMCPServer.Editor.csproj"
if (-not (Test-Path -LiteralPath $projectFile)) {
    throw "AIUnityMCPServer.Editor.csproj was not found under '$UnityProject'. Open the project in Unity once to generate it."
}

[xml]$project = Get-Content -Raw -LiteralPath $projectFile
$unityEditorPath = [string]$project.Project.PropertyGroup.UnityEditorPath
if (-not $unityEditorPath) {
    $unityEditorPath = [string]$project.Project.PropertyGroup.UnityProjectGeneratorVersion
}
$unityRoot = Split-Path -Parent $unityEditorPath
if (-not (Test-Path -LiteralPath $unityRoot)) {
    $unityRoot = "C:\Program Files\Unity\Hub\Editor\6000.0.75f1\Editor"
}
$dataRoot = Join-Path $unityRoot "Data"
$mono = Join-Path $dataRoot "MonoBleedingEdge\bin\mono.exe"
$compiler = Join-Path $dataRoot "MonoBleedingEdge\lib\mono\msbuild\Current\bin\Roslyn\csc.exe"
$output = Join-Path ([IO.Path]::GetTempPath()) "AIUnityMCPServer.StaticTests.dll"

$arguments = @(
    "/nologo"
    "/target:library"
    "/langversion:preview"
    "/define:UNITY_EDITOR,UNITY_INCLUDE_TESTS"
    "/out:`"$output`""
)
foreach ($hintPath in $project.Project.ItemGroup.Reference.HintPath | Where-Object { $_ }) {
    $referencePath = [string]$hintPath
    if (-not [IO.Path]::IsPathRooted($referencePath)) {
        $referencePath = Join-Path $UnityProject $referencePath
    }
    if (Test-Path -LiteralPath $referencePath) {
        $arguments += "/reference:`"$referencePath`""
    }
}
$editorNewtonsoft = Join-Path $dataRoot "Managed\Newtonsoft.Json.dll"
if (Test-Path -LiteralPath $editorNewtonsoft) {
    $arguments += "/reference:`"$editorNewtonsoft`""
}
$nunit = Get-ChildItem (Join-Path $UnityProject "Library\PackageCache") -Recurse -Filter "nunit.framework.dll" -File | Select-Object -First 1
if (-not $nunit) { throw "nunit.framework.dll was not found in the Unity project cache." }
$arguments += "/reference:`"$($nunit.FullName)`""
$ugui = Join-Path $UnityProject "Library\ScriptAssemblies\UnityEngine.UI.dll"
if (Test-Path -LiteralPath $ugui) {
    $arguments += "/reference:`"$ugui`""
}

$packageRoot = Split-Path -Parent $PSScriptRoot
foreach ($source in Get-ChildItem (Join-Path $packageRoot "Editor") -Recurse -Filter "*.cs" -File | Where-Object { $_.FullName -notlike "*\Editor\TestRunner\*" }) {
    $arguments += "`"$($source.FullName)`""
}
foreach ($source in Get-ChildItem (Join-Path $packageRoot "Tests\Editor") -Recurse -Filter "*.cs" -File) {
    $arguments += "`"$($source.FullName)`""
}
if ($PipelinePackage) {
    $pipelineCommon = Join-Path $PipelinePackage "Runtime\Common"
    foreach ($name in "CliCommandAttribute.cs", "CliArgAttribute.cs") {
        $source = Join-Path $pipelineCommon $name
        if (-not (Test-Path -LiteralPath $source)) {
            throw "$name was not found under '$pipelineCommon'."
        }
        $arguments += "`"$source`""
    }
}

$response = Join-Path ([IO.Path]::GetTempPath()) ("AIUnityMCPServer-" + [Guid]::NewGuid().ToString("N") + ".rsp")
[IO.File]::WriteAllLines($response, $arguments)
try {
    & $mono $compiler "@$response"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Write-Output "Static C# compilation passed for production and Editor tests."
}
finally {
    Remove-Item -LiteralPath $response -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
}
