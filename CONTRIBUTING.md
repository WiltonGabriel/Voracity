# Guia de Arquitetura e Contribuição — Voracity

A arquitetura do **Voracity** segue princípios **SOLID**, separação modular inspirada no **OpenVic** e mecânicas centradas no **Europa Universalis V** com toques dinásticos enxutos de **CK3**.

## 1. Estrutura Modular de Diretórios

```text
voracity-repo/
├── Voracity.slnx
├── Directory.Build.props
├── play_linux.sh / play_windows.ps1 / play_windows.bat
├── data/
│   ├── base/                       # 34 bens, 6 mercados, 182 Locations (36 áreas), 27 nações, pt-BR/en-US
│   ├── archive_v02/                # Leis, tecnologias e journal entries arquivados para pós-v0.1
│   └── mods/                       # Mods declarativos em camadas (manifest.json + override por ID)
└── src/
    ├── Voracity.Core/              # Simulação pura (net8.0;net10.0, ZERO referência Godot)
    │   ├── Data/                   # ModdingDataLoader, WorldDatabaseValidator (FluentValidation), Saves/Settings
    │   ├── Engine/                 # SimulationWorld, SimulationState, CommandBus, 5 ITickSystems EU5, SaveLoadMapper
    │   ├── Mapping/                # Topologia Planar (QuikGraph/NTS), MapBakePipeline (.vmap), AtlasValidator
    │   └── Models/                 # LocationModels, SimulationModels, GameplayCatalogModels, WorldModels
    ├── Voracity.Client/            # Cliente Godot 4.7.2 Vulkan (.NET 8 + RollForward) Híbrido
    │   ├── Scenes/UI/              # Cenas .tscn modulares de HUD e menus (editáveis no Editor Godot)
    │   ├── Themes/                 # VoracityTheme.tres (tema global editável no Inspector)
    │   ├── Shaders/                # map_parchment_lenses.gdshader + map_borders.gdshader + edge fog
    │   └── Scripts/                # Camera/, Campaign/, Map/ (6 módulos), UI/Hud/ ([Export]), Editor/, QA/
    ├── Voracity.Cli/               # Runner headless de terminal
    ├── Voracity.Benchmarks/        # BenchmarkDotNet (ticks EU5 e bake topológico)
    └── Voracity.Tests/             # xUnit + Shouldly + Verify.Xunit + NetArchTest.Rules
        ├── Unit/                   # Fórmulas isoladas, localização e ArchitectureTests
        └── Integration/            # Datasets canônicos, mods, mercados e roundtrip determinístico de saves
```

## 2. Regras Obrigatórias

1. **Dependências 100% Open-Source Gratuitas:** Somente pacotes MIT / Apache-2.0 / BSD-3 / Boost são permitidos. Pacotes com transição comercial (`FluentAssertions v8+`, `MediatR v13+`, `AutoMapper v15+`, `MassTransit v9+`) são proibidos.
2. **Fronteiras Limpas (`ArchitectureTests`):** `Voracity.Core` nunca pode importar `Godot.*`. Toda entrada do cliente entra como `GameCommand` no `SimulationCommandBus`.
3. **Arquitetura Híbrida Dual-Mode (Código C# + Editor Godot sem código):** Tudo o que for criado ou evoluído em `src/Voracity.Client/` deve funcionar **tanto via código quanto diretamente pelo Editor da Godot sem programar**:
   - **Via Editor:** UI/menus em cenas modulares `.tscn` (`Scenes/UI/*.tscn`), estilização em `.tres` (`Themes/VoracityTheme.tres` e `Resource`s), parâmetros visuais/câmera/lentes/shaders expostos com `[Export]` / `[ExportGroup]` / `[ExportSubgroup]` e eventos `[Signal]`.
   - **Via Código:** lógica determinística em `Voracity.Core`, construtor sem parâmetros + `Initialize(...)` nos scripts de cena, `GameCommand`/`RenderSnapshot` e fallbacks seguros no `_Ready()`. Nunca trancar layout, cores ou parâmetros visuais apenas em C# nem sobrescrever alterações visuais feitas nas cenas `.tscn`/`.tres`. Listas dinâmicas continuam com zero `new List<>`/LINQ nos loops de *tick* e em `_Draw()`.
4. **Higiene do Repositório:** Caches `.vmap`, arquivos de editor (`.editor_versions`/`.editor_exports`) e documentos privados ficam fora de `voracity-repo/`.
5. **Portão de Build & Testes:** `dotnet build Voracity.slnx` e `dotnet test` devem fechar com **0 erros e 0 avisos**.
