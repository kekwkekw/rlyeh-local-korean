param(
    [Parameter(Mandatory)][string]$GameRoot,
    [string]$LoaderRoot
)
$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GameRoot).Path
$loader = if ($LoaderRoot) { (Resolve-Path -LiteralPath $LoaderRoot).Path } else { $game }
$project = Join-Path $PSScriptRoot 'runtime/Rlyeh.Localizer/Rlyeh.Localizer.csproj'
dotnet build $project -c Release "-p:GameDir=$game" "-p:LoaderDir=$(Join-Path $loader 'BepInEx/core')"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
