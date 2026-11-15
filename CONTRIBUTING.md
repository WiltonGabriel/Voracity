# Guia de Arquitetura e Contribuição — Voracity

A estrutura de **Voracity** segue princípios modulares inspirados no **OpenVic** e na arquitetura **Clausewitz / Jomini**:

## 1. Estrutura de Diretórios

```text
voracity-repo/
├── Voracity.slnx
├── play_linux.sh
├── data/
│   ├── base/                       # Catálogos declarativos (goods, ancestries, nations, states, buildings, laws, technologies, journal_entries, localization)
│   └── mods/                       # Mods em camadas (manifest.json + sobrescrita por ID)
└── src/
    ├── Voracity.Core/              # Simulação pura (C# .NET, ZERO dependência da Godot)
    │   ├── Data/                   # ModdingDataLoader, LocalizationCatalog, SettingsStorage, SaveGameStorage
    │   ├── Engine/                 # SimulationWorld, loop de ticks, micro-pop consolidation, save/load
    │   └── Models/                 # MonetaryAmount (100:1), DualAncestry, Goods, Gameplay, WorldModels
    ├── Voracity.Client/            # Cliente Godot 4.7.2 Vulkan (.NET 8) 100% Code-First
    │   ├── Shaders/                # Shaders Vulkan (pergaminho, lentes, hover/seleção)
    │   └── Scripts/                # Main, MapView2D, MainMenuView, SettingsModal, HudRoot, NestedTooltipManager
    ├── Voracity.Cli/               # Runner headless de terminal e benchmark
    └── Voracity.Tests/
        ├── Unit/                   # Testes unitários isolados (fórmulas, modelos, datas, localização/settings)
        └── Integration/            # Testes de integração (datasets, mods, determinismo, política/tech/diplomacia, save/load)
```

## 2. Padrões Obrigatórios

1. **Testes em Toda Nova Funcionalidade:** Qualquer função ou regra de média/alta importância adicionada em `Voracity.Core` ou `data/` deve incluir testes correspondentes em `Voracity.Tests/Unit/` e/ou `Voracity.Tests/Integration/`.
2. **Code-First na Godot 4:** Não criar dependência de edições complexas em `.tscn`; toda UI e objetos de mapa nascem em C# consumindo `RenderSnapshot` e emitindo `GameCommand`.
3. **Anti-GC no Hot Path:** Evitar alocações temporárias (`LINQ`, `new List`) dentro dos loops de *tick* do `SimulationWorld`.
4. **Build Limpo:** `dotnet build` e `dotnet test` devem passar com **0 erros e 0 avisos** antes de qualquer commit.
