param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.3.5f2\Editor\Unity.exe')

$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$source = Join-Path $workspace 'external\noto-serif-cjk-kr'
$project = Join-Path $source 'unity-build'
$asset = 'NotoSerifCJKkr-Bold.otf'
$bundle = 'notoserifcjkkr-bold.bundle'
New-Item -ItemType Directory -Force -Path (Join-Path $project 'Assets\Editor'), (Join-Path $project 'Packages'), (Join-Path $project 'ProjectSettings') | Out-Null
Copy-Item -LiteralPath (Join-Path $source $asset) -Destination (Join-Path $project 'Assets') -Force
'{"dependencies":{"com.unity.modules.assetbundle":"1.0.0"}}' | Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Encoding utf8
'm_EditorVersion: 6000.3.5f2' | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Encoding utf8
@'
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

internal static class BuildKoreanFont
{
    public static void Run()
    {
        const string path = "Assets/NotoSerifCJKkr-Bold.otf";
        var importer = (TrueTypeFontImporter)AssetImporter.GetAtPath(path);
        importer.fontTextureCase = FontTextureCase.Dynamic;
        importer.includeFontData = true;
        importer.SaveAndReimport();

        var font = AssetDatabase.LoadAssetAtPath<Font>(path);
        if (!font || !font.dynamic || !font.HasCharacter('한') || !font.HasCharacter('힣'))
            throw new InvalidOperationException("The imported font must support dynamic Korean text.");

        Directory.CreateDirectory("Bundles");
        var definition = new AssetBundleBuild
        {
            assetBundleName = "notoserifcjkkr-bold.bundle",
            assetNames = new[] { path }
        };
        if (!BuildPipeline.BuildAssetBundles("Bundles", new[] { definition },
                BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64))
            throw new InvalidOperationException("Font AssetBundle build failed.");
    }
}
'@ | Set-Content -LiteralPath (Join-Path $project 'Assets\Editor\BuildKoreanFont.cs') -Encoding utf8

$log = Join-Path $workspace 'evidence\font01-unity-build.log'
$arguments = @('-batchmode', '-nographics', '-quit', '-projectPath', ('"{0}"' -f $project),
    '-buildTarget', 'Win64', '-executeMethod', 'BuildKoreanFont.Run', '-logFile', ('"{0}"' -f $log))
$editor = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
if ($editor.ExitCode -ne 0) { throw "Unity returned $($editor.ExitCode). See $log" }
Copy-Item -LiteralPath (Join-Path $project "Bundles\$bundle") -Destination $source -Force
Get-FileHash -LiteralPath (Join-Path $source $bundle)
