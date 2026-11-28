# Voracity

> **Aviso:** Este é um **projeto pessoal**, ainda bem informal e sem nada sério por enquanto. Só pretendo focar nele de verdade daqui a cerca de um ano — até lá, vou mexer aqui apenas de vez em quando para uso próprio. **Não estou aceitando contribuições, Pull Requests ou ajuda externa neste momento.**

Rascunho/protótipo pessoal de simulação em C# (`Voracity.Core`) com cliente Godot 4 (`Voracity.Client`).

---

## O que a base `v0.1` implementa

- **`Voracity.Core` (`.NET 8 / 10` puro, zero dependência Godot):**
  - Pipeline modular determinístico (`TickPipeline` em 4 cadências: **6h / diário / semanal / mensal** + `SimulationCommandBus`).
  - **Mapa EU5 de 182 Locations:** `134 Habitáveis`, `3 Intransponíveis`, `3 Corredores` e `42 Zonas Marítimas` clicáveis em `36 Áreas / 10 Regiões / 4 Super-Regiões`, com topologia planar via `NetTopologySuite` e `QuikGraph`.
  - **Proximidade & Controle:** Dijkstra ponderado por estradas, rios, costa e zonas marítimas calculando `Proximity`, `MaxControl` e `EffectiveTaxBase`.
  - **R.G.O., Comida (3 Camadas), 6 Mercados & Demografia:** `34 bens` ativos, precificação flutuante (`-75%..+75%`), compra priorizada por `MarketAccess`, estoque provincial + fome, `Cost of the Court`, `8 Classes Sociais` + consolidação automática de micro-*Pops*.
  - **Dados Modáveis (`data/base/` e `data/mods/`):** JSON declarativo validado por `FluentValidation`, sobrescrita por `id` e *saves* v2 determinísticos.
- **`Voracity.Client` (Godot 4.7.2 Vulkan *Code-First*):**
  - Arquitetura modular em C# (`Camera/`, `Campaign/`, `Map/`, `UI/Hud/`) com shaders 2D cartográficos para *Habitable*, *Corridor* e *SeaZones*, 5 lentes EU5, *Nested Tooltips* e editor de mapas integrado.

---

## Setup & Testes (Linux e Windows 11)

Não é necessário compilar bibliotecas externas manualmente: todos os pacotes (`QuikGraph`, `NetTopologySuite`, `FluentValidation`, `xUnit`, `Shouldly`, `Verify.Xunit`, `NetArchTest.Rules`) são restaurados automaticamente via NuGet.

| Pré-requisito | Uso |
| :--- | :--- |
| **.NET SDK 8.0+ / 10.0** | `Core`, `Cli`, `Benchmarks`, `Tests` e *scripts* C# |
| **Godot 4.7.2+ (.NET / Mono)** | Apenas para executar `Voracity.Client` (Vulkan `Forward+`) |

### 1. Restaurar pacotes, compilar e testar (Linux & Windows)
```bash
git clone https://github.com/WiltonGabriel/Voracity.git
cd Voracity

dotnet restore Voracity.slnx
dotnet build Voracity.slnx
dotnet test src/Voracity.Tests/Voracity.Tests.csproj --no-build
```

### 2. Executar o jogo (Cliente Vulkan)
- **Linux:**
  ```bash
  ./play_linux.sh
  ```
- **Windows 11 (PowerShell ou Prompt):**
  ```powershell
  .\play_windows.ps1
  ```
  *(ou execute `play_windows.bat` / abra [`src/Voracity.Client/project.godot`](src/Voracity.Client/project.godot) na Godot 4.7.2 .NET)*

### 3. Simulação *headless* & Benchmarks
```bash
dotnet run --project src/Voracity.Cli/Voracity.Cli.csproj -- --nation AET --days 360
dotnet run --project src/Voracity.Benchmarks/Voracity.Benchmarks.csproj -c Release
```

### Uso rápido do núcleo via código
```csharp
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

var db = ModdingDataLoader.Load("data");
var world = new SimulationWorld(db, playerNationId: "AET", seed: 13800101UL);

world.EnqueueCommand(new UpgradeLocationCommand("prov_valenford", LocationUpgradeKind.ExpandRgo));
world.StepDays(30);

RenderSnapshot snapshot = world.BuildRenderSnapshot();
Console.WriteLine($"{snapshot.Date.ToDisplayString()} — Tesouro: {snapshot.Treasury.ToUiString()}");
```

---

## Estrutura

```text
.
├── Voracity.slnx                   # Solution .NET unificada (Core, Client, Cli, Benchmarks, Tests)
├── Directory.Build.props           # RollForward=Major e warnings limpos
├── play_linux.sh / play_windows.*  # Runners Vulkan cross-platform
├── data/
│   ├── base/                       # Catálogos declarativos (34 bens, 182 locations, 6 mercados, 27 nações)
│   ├── archive_v02/                # Catálogos reservados para futuras expansões
│   └── mods/                       # Mods em camadas (manifest.json + override por id)
└── src/
    ├── Voracity.Core/              # Engine EU5 pura (.NET 8/10, zero Godot)
    ├── Voracity.Client/            # Cliente Godot 4 Vulkan Code-First + shaders
    ├── Voracity.Cli/               # Runner headless de terminal
    ├── Voracity.Benchmarks/        # Benchmarks (BenchmarkDotNet)
    └── Voracity.Tests/             # Unit + Integration + ArchitectureTests (xUnit)
```

Referência de dados/mods: [`data/mods/README.md`](data/mods/README.md).
