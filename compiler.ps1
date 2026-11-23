param([switch]$Tester)
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
$compilateur = Join-Path $framework 'csc.exe'
if (!(Test-Path -LiteralPath $compilateur)) { throw 'Le compilateur du .NET Framework est introuvable.' }
$references = @('System.dll', 'System.Core.dll', 'System.IO.Compression.dll', 'System.Runtime.Serialization.dll', 'System.Xaml.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll', 'WPF\WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$fichiers = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$references += '/reference:' + (Join-Path $framework 'System.Runtime.dll')
$references += @('System.Drawing.dll', 'System.Web.Extensions.dll', 'System.Runtime.WindowsRuntime.dll', 'System.Runtime.InteropServices.WindowsRuntime.dll') | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
foreach ($metadata in @('Windows.Foundation', 'Windows.Storage', 'Windows.Data', 'Windows.Graphics')) {
    $references += '/reference:' + (Join-Path $env:WINDIR "System32\WinMetadata\$metadata.winmd")
}
$references += '/reference:' + (Join-Path $framework 'System.Windows.Forms.dll')
$references += '/reference:' + (Join-Path $env:WINDIR 'System32\WinMetadata\Windows.Networking.winmd')
$options = @('/nologo', '/utf8output', '/codepage:65001', '/warnaserror+', '/optimize+', '/platform:anycpu')
$options += '/win32manifest:' + (Join-Path $PSScriptRoot 'src\app.manifest')
$sortie = Join-Path $PSScriptRoot 'Toolbox.exe'
$options += '/resource:' + (Join-Path $PSScriptRoot 'src\Fenetre.xaml') + ',AtelierPC.Fenetre.xaml'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\ImageMagick\ImageMagick-7.1.2-36-Q8-x86.zip') + ',AtelierPC.ImageEngine.zip'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\FFmpeg\FFmpeg-9.1-essentials-x64.zip') + ',AtelierPC.VideoEngine.zip'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\YouTube\yt-dlp-2026.10.26-win-x64.zip') + ',AtelierPC.YoutubeEngine.zip'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\YouTube\deno-2.9.8-win-x64.zip') + ',AtelierPC.Deno.zip'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\YouTube\Deno-LICENSE.md') + ',AtelierPC.Deno.LICENSE'
$options += '/resource:' + (Join-Path $PSScriptRoot 'vendor\qpdf\qpdf-12.4.2-x86.zip') + ',AtelierPC.PdfEngine.zip'
$options += '/resource:' + (Join-Path $PSScriptRoot 'src\ImagePolicy.xml') + ',AtelierPC.ImagePolicy.xml'
$options += '/resource:' + (Join-Path $PSScriptRoot 'src\RouterPageCapture.js') + ',AtelierPC.RouterPageCapture.js'
$webViewSdk = Join-Path $PSScriptRoot 'vendor\WebView2'
foreach ($assembly in @('Core', 'Wpf')) {
    $library = Join-Path $webViewSdk ("lib\Microsoft.Web.WebView2.$assembly.dll")
    $references += '/reference:' + $library
    $options += "/resource:$library,AtelierPC.WebView2.$assembly.dll"
}
foreach ($architecture in @('x86', 'x64', 'arm64')) {
    $loader = Join-Path $webViewSdk "$architecture\WebView2Loader.dll"
    $options += "/resource:$loader,AtelierPC.WebView2.$architecture.dll"
}
foreach ($notice in @('LICENSE.txt', 'NOTICE.txt')) {
    $options += '/resource:' + (Join-Path $webViewSdk $notice) + ",AtelierPC.WebView2.$notice"
}
if ($Tester) {
    $sortie = Join-Path ([System.IO.Path]::GetTempPath()) ('Toolbox.Tests-' + [Guid]::NewGuid() + '.exe')
    $options += '/main:AtelierPC.Tests'
    $options += '/target:exe'
    $fichiers += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Filter '*.cs' | ForEach-Object FullName)
} else {
    $options += '/win32icon:' + (Join-Path $PSScriptRoot 'src\Toolbox.ico')
    $options += '/target:winexe'
}
$commun = Join-Path $PSScriptRoot 'updater\UpdateFiles.cs'
$assistant = Join-Path ([System.IO.Path]::GetTempPath()) ('Toolbox.UpdateHelper-' + [Guid]::NewGuid() + '.exe')
try {
    & $compilateur /nologo /utf8output /codepage:65001 /warnaserror+ /optimize+ /target:exe /reference:System.Windows.Forms.dll ("/out:" + $assistant) $commun (Join-Path $PSScriptRoot 'updater\Program.cs')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $options += '/resource:' + $assistant + ',AtelierPC.UpdateHelper.exe'
    $fichiers += $commun
    & $compilateur @options @references ("/out:" + $sortie) @fichiers
} finally {
    if (Test-Path -LiteralPath $assistant) { Remove-Item -LiteralPath $assistant }
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Tester) {
    try { & $sortie; $resultat = $LASTEXITCODE } finally { Remove-Item -LiteralPath $sortie }
    exit $resultat
}
Write-Output $sortie
