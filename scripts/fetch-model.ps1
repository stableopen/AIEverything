param(
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$revision = '1427fd652930e4ba29e8149678df786c240d8825'
$expectedLength = 118620016
$expectedHash = '6C2513767FB63D008A4377BEF7A7A3555433D9436342BB53E35A3A72FFC52D4B'
$modelDirectory = Join-Path $root `
    'src\AIEverything.Desktop\Models\mmarco-mMiniLMv2-L12-H384-v1'
$destination = Join-Path $modelDirectory 'model_quint8_avx2.onnx'
$uri = 'https://huggingface.co/cross-encoder/mmarco-mMiniLMv2-L12-H384-v1/' +
    "resolve/$revision/onnx/model_quint8_avx2.onnx?download=true"

function Test-FrozenModel([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $file = Get-Item -LiteralPath $Path
    if ($file.Length -ne $expectedLength) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $expectedHash
}

if (Test-FrozenModel $destination) {
    Write-Output "PASS: frozen local model already present at $destination"
    exit 0
}

if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "Existing model failed length or SHA-256 validation. Remove it or rerun with -Force: $destination"
}

New-Item -ItemType Directory -Path $modelDirectory -Force | Out-Null
$temporary = Join-Path $modelDirectory `
    ("model_quint8_avx2.download-{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
try {
    Write-Output "Downloading frozen model revision $revision ..."
    Invoke-WebRequest -Uri $uri -OutFile $temporary -UseBasicParsing

    $download = Get-Item -LiteralPath $temporary
    if ($download.Length -ne $expectedLength) {
        throw "Downloaded model length mismatch: expected $expectedLength, actual $($download.Length)."
    }
    $actualHash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "Downloaded model SHA-256 mismatch: expected $expectedHash, actual $actualHash."
    }

    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary -Force
    }
}

Write-Output "PASS: downloaded and verified $destination"
Write-Output "SHA256: $expectedHash"
