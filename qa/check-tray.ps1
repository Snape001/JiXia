[CmdletBinding()]
param([string] $PreviewPath)

$ErrorActionPreference = 'Stop'
$qa = (Resolve-Path -LiteralPath $PSScriptRoot).ProviderPath
$source = (Resolve-Path -LiteralPath (Join-Path $qa '..\src')).ProviderPath
if ([String]::IsNullOrEmpty($PreviewPath)) {
    $PreviewPath = Join-Path $qa 'tray-preview.png'
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$fixture = (New-Item -ItemType Directory -Path (Join-Path $qa ('tray-' + [Guid]::NewGuid().ToString('N')))).FullName

try {
    $exe = Join-Path $fixture 'TrayCheck.exe'
    $compilerArgs = @('/nologo','/target:exe','/platform:anycpu','/langversion:5','/codepage:65001',
        '/utf8output','/main:SidePage.TrayCheck',"/out:$exe", "/win32manifest:$source\SidePage.manifest")
    foreach ($name in @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Drawing.dll','System.Windows.Forms.dll')) {
        $compilerArgs += '/reference:' + (Join-Path $framework $name)
    }
    foreach ($name in @('WindowsBase','PresentationCore','PresentationFramework','UIAutomationTypes','UIAutomationProvider')) {
        $compilerArgs += '/reference:' + (Join-Path $framework "WPF\$name.dll")
    }
    foreach ($name in @('Tasks.cs','DiscardMotion.cs','Thoughts.cs','Host.cs','TrayMenu.cs')) {
        $compilerArgs += Join-Path $source $name
    }
    $compilerArgs += @((Join-Path $qa 'DockCheck.cs'), (Join-Path $qa 'TrayCheck.cs'))
    & (Join-Path $framework 'csc.exe') @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tray QA build failed.' }

    $data = Join-Path $fixture 'data'
    & $exe (Join-Path $source 'Panel.xaml') (Join-Path $source 'SidePage.ico') $data
    if ($LASTEXITCODE -ne 0) { throw 'Tray QA failed.' }

    $preview = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PreviewPath)
    Copy-Item -LiteralPath (Join-Path $data 'tray-preview.png') -Destination $preview -Force
    Write-Output "Tray preview: $preview"
} finally {
    $temporary = (Resolve-Path -LiteralPath $fixture).ProviderPath
    if ([IO.Path]::GetDirectoryName($temporary).Equals($qa, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($temporary).StartsWith('tray-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
