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
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf = Join-Path $framework 'WPF'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "未找到 C# 编译器：$csc"
}

if (-not (Test-Path -LiteralPath $distDirectory)) {
    New-Item -ItemType Directory -Path $distDirectory | Out-Null
}

$icon = Join-Path $assetsDirectory 'app.ico'
if (-not (Test-Path -LiteralPath $icon)) {
    & (Join-Path $root 'tools\New-AppIcon.ps1') -OutputPath $icon
}

$sources = Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' |
    Sort-Object Name |
    ForEach-Object { $_.FullName }

$references = @(
    (Join-Path $framework 'System.dll')
    (Join-Path $framework 'System.Core.dll')
    (Join-Path $framework 'System.Runtime.Serialization.dll')
    (Join-Path $framework 'System.Xml.dll')
    (Join-Path $framework 'System.Xaml.dll')
    (Join-Path $wpf 'WindowsBase.dll')
    (Join-Path $wpf 'PresentationCore.dll')
    (Join-Path $wpf 'PresentationFramework.dll')
)

$appArguments = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/debug-'
    '/codepage:65001'
    "/out:$distDirectory\Inkveil.exe"
    "/win32icon:$icon"
    "/win32manifest:$sourceDirectory\app.manifest"
) + ($references | ForEach-Object { "/reference:$_" }) + $sources

Write-Host '正在编译墨隐阅读器...'
& $csc $appArguments
if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码：$LASTEXITCODE"
}

Copy-Item -LiteralPath (Join-Path $sourceDirectory 'App.config') -Destination (Join-Path $distDirectory 'Inkveil.exe.config') -Force
Write-Host "构建完成：$distDirectory\Inkveil.exe"

if ($Test) {
    $testSources = @(
        (Join-Path $sourceDirectory 'NovelBook.cs')
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
