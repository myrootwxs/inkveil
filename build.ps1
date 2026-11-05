param(
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sourceDirectory = Join-Path $root 'src'
$testDirectory = Join-Path $root 'tests'
$distDirectory = Join-Path $root 'dist'
$assetsDirectory = Join-Path $root 'assets'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$msbuild = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "未找到 C# 编译器：$csc"
}
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "未找到 MSBuild：$msbuild"
}

if (-not (Test-Path -LiteralPath $distDirectory)) {
    New-Item -ItemType Directory -Path $distDirectory | Out-Null
}

$icon = Join-Path $assetsDirectory 'app.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    & (Join-Path $root 'tools\New-AppIcon.ps1') -OutputPath $icon
}

$references = @(
    (Join-Path $framework 'System.dll')
    (Join-Path $framework 'System.Core.dll')
    (Join-Path $framework 'System.IO.Compression.dll')
    (Join-Path $framework 'System.Runtime.Serialization.dll')
    (Join-Path $framework 'System.Xml.dll')
    (Join-Path $framework 'System.Xaml.dll')
    (Join-Path $wpf 'WindowsBase.dll')
    (Join-Path $wpf 'PresentationCore.dll')
    (Join-Path $wpf 'PresentationFramework.dll')
)

Write-Host '正在编译墨隐阅读器...'
& $msbuild (Join-Path $sourceDirectory 'Inkveil.csproj') `
    /t:Rebuild `
    /p:Configuration=Release `
    /p:Platform=AnyCPU `
    /nologo `
    /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath (Join-Path $distDirectory 'Inkveil.exe'))) {
    throw "编译完成但未找到输出文件：$distDirectory\Inkveil.exe"
}

Copy-Item -LiteralPath (Join-Path $sourceDirectory 'App.config') -Destination (Join-Path $distDirectory 'Inkveil.exe.config') -Force
Write-Host "构建完成：$distDirectory\Inkveil.exe"

if ($Test) {
    $testSources = @(
        (Join-Path $sourceDirectory 'EpubBookLoader.cs')
        (Join-Path $sourceDirectory 'NovelBook.cs')
        (Join-Path $sourceDirectory 'ReaderSettings.cs')
        (Join-Path $testDirectory 'CoreTests.cs')
    )
    $testArguments = @(
        '/nologo'
        '/target:exe'
        '/platform:anycpu'
        '/optimize+'
        '/debug-'
        '/codepage:65001'
        "/out:$distDirectory\Inkveil.Tests.exe"
    ) + ($references | ForEach-Object { "/reference:$_" }) + $testSources

    Write-Host '正在运行核心测试...'
    & $csc $testArguments
    if ($LASTEXITCODE -ne 0) {
        throw "测试程序编译失败，退出码：$LASTEXITCODE"
    }
    & (Join-Path $distDirectory 'Inkveil.Tests.exe')
    if ($LASTEXITCODE -ne 0) {
        throw "测试失败，退出码：$LASTEXITCODE"
    }
}
