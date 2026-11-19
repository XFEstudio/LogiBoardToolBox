param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$fileName = "罗技鼠标板载工具箱 $Version AOT.exe"
$delivery = Join-Path $OutputDirectory $fileName
if (Test-Path -LiteralPath $delivery) { throw "目标文件已存在：$delivery" }

$stage = Join-Path $repo ("work/portable-$Version-" + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $stage 'app'
$launcher = Join-Path $stage 'launcher'
$payload = Join-Path $stage 'payload.zip'
New-Item -ItemType Directory -Path $stage,$OutputDirectory -Force | Out-Null

$appArguments = @(
    'publish', (Join-Path $repo 'LogiBoardToolBox/LogiBoardToolBox/LogiBoardToolBox.csproj'),
    '-c', 'Release', '-r', 'win-x64', '-p:Platform=x64', '-p:PublishAot=true', '-p:PublishProfile=win-x64-aot',
    "-p:Version=$Version", "-p:ArtifactsPath=$(Join-Path $stage 'app-build')", '-o', $app
)
dotnet @appArguments
if ($LASTEXITCODE) { throw "AOT 应用构建失败（$LASTEXITCODE）。" }
$requiredFiles = @('LogiBoardToolBox.exe', 'resources.pri', 'Microsoft.UI.Xaml.dll', 'Assets/appicon.ico',
    'Assets/Fonts/BrownLogitechPan-Regular.otf', 'Assets/Fonts/BrownLogitechPan-Bold.otf')
foreach ($catalog in 'mice.json', 'mice-extended.json') {
    $catalogPath = Join-Path $repo "LogiBoardToolBox/LogiBoardToolBox.Core/Devices/$catalog"
    $models = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
    foreach ($model in $models) {
        $requiredFiles += $model.ImageAsset.TrimStart('/')
        if ($model.SideImageAsset) { $requiredFiles += $model.SideImageAsset.TrimStart('/') }
    }
}
foreach ($name in $requiredFiles | Sort-Object -Unique) {
    $file = Get-Item -LiteralPath (Join-Path $app $name) -ErrorAction SilentlyContinue
    if (!$file -or $file.PSIsContainer -or !$file.Length) { throw "AOT 发布缺少 $name。" }
}
if (Test-Path -LiteralPath (Join-Path $app 'coreclr.dll')) { throw '发布目录包含 CoreCLR，预期为原生 AOT 应用。' }

$archive = [IO.Compression.ZipFile]::Open($payload, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object Extension -ne '.pdb' | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($app, $_.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }

$launcherArguments = @(
    'publish', (Join-Path $PSScriptRoot 'LogiBoardToolBox.Portable.csproj'), '-c', 'Release', '-r', 'win-x64',
    "-p:Version=$Version", "-p:PortablePayload=$payload", "-p:ArtifactsPath=$(Join-Path $stage 'launcher-build')", '-o', $launcher
)
dotnet @launcherArguments
if ($LASTEXITCODE) { throw "单文件启动器构建失败（$LASTEXITCODE）。" }

[IO.File]::Copy((Join-Path $launcher 'LogiBoardToolBox.Portable.exe'), $delivery)
[ordered]@{
    File = $delivery
    Version = $Version
    Sha256 = (Get-FileHash -LiteralPath $delivery -Algorithm SHA256).Hash
    AppSha256 = (Get-FileHash -LiteralPath (Join-Path $app 'LogiBoardToolBox.exe') -Algorithm SHA256).Hash
    PayloadSha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $stage 'build.json')
