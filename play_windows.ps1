$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$env:DOTNET_ROLL_FORWARD = "Major"

function Resolve-Godot4Mono {
    if ($env:GODOT4_BIN -and (Test-Path $env:GODOT4_BIN)) {
        return $env:GODOT4_BIN
    }

    foreach ($cmdName in @("godot4-mono", "godot4", "godot")) {
        $found = Get-Command $cmdName -ErrorAction SilentlyContinue
        if ($found) {
            return $found.Source
        }
    }

    $candidates = @(
        "$env:LOCALAPPDATA\Godot\Godot_v4.*_mono_win64.exe",
        "$env:USERPROFILE\Downloads\Godot_v4.*_mono_win64\Godot_v4.*_mono_win64.exe",
        "$env:USERPROFILE\Desktop\Godot_v4.*_mono_win64.exe",
        "C:\Program Files\Godot\Godot_v4.*_mono_win64.exe"
    )

    foreach ($pattern in $candidates) {
        $match = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if ($match) {
            return $match.FullName
        }
    }

    return $null
}

$GodotExe = Resolve-Godot4Mono
if (-not $GodotExe) {
    Write-Error "Godot 4 Mono (.NET) executavel nao encontrado. Defina GODOT4_BIN ou adicione o Godot 4 Mono ao PATH."
    exit 1
}

Write-Host "Restaurando pacotes NuGet..."
dotnet restore (Join-Path $ScriptDir "Voracity.slnx") --nologo

Write-Host "Compilando Voracity.Client (.NET)..."
dotnet build (Join-Path $ScriptDir "src\Voracity.Client\Voracity.Client.csproj") --nologo

Write-Host "Iniciando Voracity (Godot 4 Vulkan) via $GodotExe..."
& $GodotExe --path (Join-Path $ScriptDir "src\Voracity.Client") @args
