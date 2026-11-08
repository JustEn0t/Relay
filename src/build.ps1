param([string]$OutputFile = 'Relay.exe')
$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
if (-not [System.IO.Path]::IsPathRooted($OutputFile)) { $OutputFile = Join-Path $PSScriptRoot $OutputFile }
$relayCompilerArguments = @('/nologo', '/codepage:65001', '/target:winexe', '/main:Relay.RelayApp', '/reference:System.Drawing.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll', '/reference:System.Xaml.dll', ('/out:' + $OutputFile), ('/win32icon:' + (Join-Path $PSScriptRoot 'Assets\relay.ico')))
$relayCompilerArguments += '/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')
foreach ($reference in @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll')) { $relayCompilerArguments += '/reference:' + (Join-Path $wpf $reference) }
$relayResources = [ordered]@{ 'Shell.xaml' = 'Relay.Shell.xaml'; 'Assets\cover.png' = 'Relay.Cover.png'; 'Assets\relay.ico' = 'Relay.Icon.ico'; 'Assets\sample-live.jpg' = 'Relay.SampleLive.jpg'; 'Assets\sample-ended.jpg' = 'Relay.SampleEnded.jpg' }
$relayResources['Assets\sample-ongoing.jpg'] = 'Relay.SampleOngoing.jpg'
foreach ($resource in $relayResources.GetEnumerator()) { $relayCompilerArguments += '/resource:' + (Join-Path $PSScriptRoot $resource.Key) + ',' + $resource.Value }
foreach ($source in @('Core.cs', 'Cards.cs', 'Desktop.cs', 'AssemblyInfo.cs')) { $relayCompilerArguments += Join-Path $PSScriptRoot $source }
& $compiler @relayCompilerArguments
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }

