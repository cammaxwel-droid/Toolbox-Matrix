# Creates a new private distribution candidate. Never launches the app or invokes Git.
[CmdletBinding()]
param([string]$OutputDirectory, [string]$WorkDirectory, [string]$ReferenceCandidate)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ("preview/V1-Public-Candidates/1.0.0-candidate-sanitized-" + $stamp) }
if (!$WorkDirectory) { $WorkDirectory = Join-Path $root (".local/public-build-" + $stamp) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = [IO.Path]::GetFullPath($WorkDirectory)
if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output + '.zip')) -or (Test-Path -LiteralPath $work)) {
    throw 'Output/work destination already exists. Nothing will be overwritten.'
}
if ($output -eq $work -or $output.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $work.StartsWith($output + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Work and distribution folders must be separate.'
}
$env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
$env:NUGET_PACKAGES = Join-Path $work 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'
New-Item -ItemType Directory -Path $work | Out-Null
$artifacts = Join-Path $work 'artifacts'
$props = @("-p:PathMap=$root=/_/ToolboxMatrix", '-p:DebugType=none', '-p:DebugSymbols=false',
    '-p:Deterministic=true', '-p:ContinuousIntegrationBuild=true',
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:EnableSourceControlManagerQueries=false',
    '-p:EnableSourceLink=false', '-p:EmbedUntrackedSources=false', '-p:UseSharedCompilation=false')
function Run-Dotnet([string[]]$Arguments, [string]$Log) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $work $Log) | Write-Output
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed; inspect $Log. Partial files are preserved." }
}
function Get-ChecksAssembly([string]$Configuration) {
    $files = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'bin/ReleaseConfigurationSmoke') -Recurse -File -Filter 'ReleaseConfigurationSmoke.dll' |
        Where-Object { $_.Directory.Name -ieq $Configuration })
    if ($files.Count -ne 1) { throw 'Cannot identify the exact console-check assembly.' }
    return $files[0].FullName
}
$testProject = Join-Path $root 'tests/ReleaseConfigurationSmoke/ReleaseConfigurationSmoke.csproj'
foreach ($configuration in @('V1','Release')) {
    Run-Dotnet (@('build',$testProject,'-c',$configuration,'--artifacts-path',$artifacts,'--nologo') + $props) ("build-$configuration.log")
    $checksAssembly = Get-ChecksAssembly $configuration
    Run-Dotnet @($checksAssembly,$configuration,$root) ("checks-$configuration.log")
}
$appProject = Join-Path $root 'src/WinGetToolbox/WinGetToolbox.csproj'
$raw = Join-Path $work 'raw-publish'
Run-Dotnet (@('publish',$appProject,'-c','V1','--no-build','--no-restore','--no-self-contained','--artifacts-path',$artifacts,'-o',$raw,'--nologo') + $props) 'publish.log'
$runtimeFiles = @('WinGetToolbox.exe','WinGetToolbox.dll','WinGetToolbox.deps.json','WinGetToolbox.runtimeconfig.json')
$rawNames = @(Get-ChildItem -LiteralPath $raw -File -Recurse | ForEach-Object {$_.FullName.Substring($raw.Length+1).Replace('\','/')} | Sort-Object)
if (($rawNames -join '|') -cne (($runtimeFiles | Sort-Object) -join '|')) { throw 'Publish produced an unexpected payload. Inspect it; nothing is silently discarded.' }
$testedDll = Join-Path (Split-Path -Parent (Get-ChecksAssembly 'V1')) 'WinGetToolbox.dll'
if ((Get-FileHash -LiteralPath $testedDll).Hash -ne (Get-FileHash -LiteralPath (Join-Path $raw 'WinGetToolbox.dll')).Hash) {
    throw 'Published DLL differs from the tested V1 DLL.'
}
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$hostPath = Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Host.win-x64/10.0.13/runtimes/win-x64/native/apphost.exe'
if (!(Test-Path -LiteralPath $hostPath)) { throw 'The reviewed .NET 10.0.13 apphost is unavailable. Update and review notices before using another host.' }
New-Item -ItemType Directory -Path $output | Out-Null
foreach ($file in $runtimeFiles) { Copy-Item -LiteralPath (Join-Path $raw $file) -Destination (Join-Path $output $file) }
Copy-Item -LiteralPath (Join-Path $root 'scripts/tlbxm.cmd') -Destination (Join-Path $output 'tlbxm.cmd')
foreach ($file in @('LICENSE','ARTWORK_NOTICE.md','THIRD_PARTY_NOTICES.md','docs/LICENSING.md',
    'docs/licenses/dotnet-host-10.0.13-LICENSE.txt','docs/licenses/dotnet-host-10.0.13-THIRD-PARTY-NOTICES.txt')) {
    $destination = Join-Path $output $file
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
}
@'
Toolbox Matrix 1.0.0 candidate — private review only
Licence decision pending. Not approved for public redistribution.
Keep this entire folder together. Windows 11 x64, the .NET 10 Windows Desktop
Runtime (x64), and a working WinGet installation are prerequisites.
For a separately approved interactive review, open WinGetToolbox.exe.
The adjacent tlbxm.cmd uses this folder's executable.
This framework-dependent package includes no runtime, WinGet or installers.
Source paths/debugging symbols were removed during compilation. The original
icon and V1's seven visible collections are retained. Static checks are not
a fresh visual review or proof of installation, UAC, recovery, clean-machine
compatibility or signing. Consult LICENSE, ARTWORK_NOTICE.md,
THIRD_PARTY_NOTICES.md and docs/LICENSING.md.
'@ | Set-Content -LiteralPath (Join-Path $output 'README.txt') -Encoding utf8
$manifest = @(Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{Path=$_.FullName.Substring($output.Length+1).Replace('\','/');Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})
[ordered]@{
    Status='PRIVATE SANITIZED V1 CANDIDATE - LICENCE UNDECIDED - PUBLICATION NOT APPROVED'
    Version='1.0.0-candidate';Configuration='V1';CreatedUtc=[DateTime]::UtcNow.ToString('O')
    Runtime='Framework-dependent Windows x64; .NET 10 Windows Desktop Runtime required'
    DebugSymbols='None; no PDB or application CodeView path'
    VisibleCategories=@('AI & Automation','Gaming','Development','Media / Creative','Studying','Security / Diagnostics','Everyday Essentials')
    Checks=@('V1 console configuration suite passed','Development Release console configuration suite passed','Published DLL matches tested V1 assembly')
    OpenGates=@('Licence decision','Public distribution approval','Real install/UAC','Failure/cancellation/recovery','Clean-machine prerequisites','Optional registration','Signing/download behaviour','Exact rebuilt-binary visual review')
    Files=$manifest
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'CANDIDATE.json') -Encoding utf8
$auditProject = Join-Path $root 'tests/DistributionAudit/DistributionAudit.csproj'
$auditArtifacts = Join-Path $work 'audit-artifacts'
Run-Dotnet (@('build',$auditProject,'-c','Release','--artifacts-path',$auditArtifacts,'--nologo') + $props) 'audit-build.log'
$auditDll = @(Get-ChildItem -LiteralPath (Join-Path $auditArtifacts 'bin/DistributionAudit') -Recurse -File -Filter 'DistributionAudit.dll')
if ($auditDll.Count -ne 1) { throw 'Cannot identify the static audit console assembly.' }
$auditArgs = @($auditDll[0].FullName,$output,$root,$hostPath,(Join-Path $work 'distribution-audit.json'))
if ($ReferenceCandidate) { $auditArgs += [IO.Path]::GetFullPath($ReferenceCandidate) }
Run-Dotnet $auditArgs 'distribution-audit.log'
Compress-Archive -LiteralPath $output -DestinationPath ($output + '.zip') -CompressionLevel Optimal
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($output + '.zip')
try {
    $archiveFiles = @($archive.Entries | Where-Object {$_.Name})
    if ($archiveFiles.Count -ne 13) { throw 'Archive file count differs from audited distribution.' }
    foreach ($entry in $archiveFiles) {
        $relative = $entry.FullName.Substring($entry.FullName.IndexOf('/') + 1)
        if ($relative -match '(^|/)\.\.(/|$)' -or [IO.Path]::IsPathRooted($relative)) { throw 'Unsafe archive path.' }
        $stream = $entry.Open()
        try { $sha = [Security.Cryptography.SHA256]::Create(); $digest = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-',''); $sha.Dispose() }
        finally { $stream.Dispose() }
        if ($digest -cne (Get-FileHash -LiteralPath (Join-Path $output $relative)).Hash) { throw 'Archive content does not match audited file.' }
    }
} finally { $archive.Dispose() }
[ordered]@{Candidate=$output;Archive=($output+'.zip');ArchiveSHA256=(Get-FileHash -LiteralPath ($output+'.zip')).Hash;Files=13;Work=$work;Status='HEADLESS CHECKS PASSED; PUBLIC RELEASE GATES OPEN'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'build-result.json') -Encoding utf8
Write-Output "Sanitized candidate: $output"
Write-Output 'No app, UI, provider, installer, registration, Git or remote publication action was executed.'
