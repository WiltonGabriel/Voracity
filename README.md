# Voracity

![Status](https://img.shields.io/badge/status-Concep%C3%A7%C3%A3o%20%2F%20Alpha-amber)
[![CI](https://github.com/WiltonGabriel/Voracity/actions/workflows/ci.yml/badge.svg)](https://github.com/WiltonGabriel/Voracity/actions/workflows/ci.yml)
![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%2010.0-512BD4)
![Godot](https://img.shields.io/badge/Godot-4.7.2%20Vulkan%20(.NET)-478CBF)
![Tests](https://img.shields.io/badge/xUnit-28%20tests-brightgreen)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

> [!IMPORTANT]
> **Projeto em fase de concepção e Alpha inicial.** Mecânicas, balanceamento, interface, esquemas JSON e visual de mapa estão em evolução ativa e sujeitos a mudanças profundas.

**Voracity** é um projeto de código aberto de **Grand Strategy & Simulação Dinástica** ambientado em **Pontária (~1380 D.Q.)**, combinando simulação socioeconômica *headless* determinística em **C# (.NET)** com renderização **Godot 4 (Vulkan) *Code-First*** e camadas declarativas de *modding* inspiradas no **OpenVic** e na linha *Clausewitz / Jomini*.

---

## Sumário

- [O que o projeto faz](#o-que-o-projeto-faz)
- [Por que esta arquitetura](#por-que-esta-arquitetura)
- [Primeiros passos](#primeiros-passos)
  - [Pré-requisitos](#pré-requisitos)
  - [Compilar e rodar os testes](#compilar-e-rodar-os-testes)
  - [Executar o jogo (Cliente Vulkan)](#executar-o-jogo-cliente-vulkan)
  - [Rodar simulação *headless* (CLI)](#rodar-simulação-headless-cli)
  - [Usar o motor *Core* via código](#usar-o-motor-core-via-código)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Onde obter ajuda](#onde-obter-ajuda)
- [Manutenção e contribuições](#manutenção-e-contribuições)

---

## O que o projeto faz

Em seu estágio Alpha atual, o **Voracity** implementa um *vertical slice* jogável e simulável de estratégia continental:

- **Núcleo de Simulação Desacoplado (`Voracity.Core`):**
  - Loop de *ticks* determinístico em 4 cadências (**6h / diário / semanal / mensal**).
  - **Mapa e Províncias Contíguas (`PontariaMapGenerator`):** Geração determinística (`1440×840`) de **19 Estados** e **72 Províncias** jogáveis em 13 nações, com *lookup* de província em $O(1)$, adjacências reais, costas, relevo (*heightmap*), Sedes (*Holdings*), fortificações e ocupação militar em guerras.
  - **Economia & População:** Sistema monetário em base inteira `100:1` (Cobre / Prata / Ouro), mercado flutuante (`-75%` a `+75%`) com 34 bens, 8 construções com troca de Métodos de Produção (*PMs*), consolidação automática de micro-*Pops* e **Dupla Ancestralidade** (herança 50%/50% de traços demográficos e militares).
  - **Política, Ciência & Diplomacia:** 8 Grupos de Interesse, tramitação de leis, 3 árvores tecnológicas, pactos comerciais/alianças/guerras e *Journal Entries*.
- **Cliente Gráfico Vulkan (`Voracity.Client`):**
  - Arquitetura **Code-First** (cena mínima [`Main.tscn`](src/Voracity.Client/Main.tscn)) com *shader* cartográfico híbrido **Pergaminho Político ↔ Relevo**, 5 lentes de mapa, menu inicial com pré-visualização territorial, *Nested Tooltips* com trava (*Action Lock*), *Save/Load* em JSON e idioma automático ou manual (`pt-BR` / `en-US`).

---

## Por que esta arquitetura

- **Separação estrita Simulação ↔ Renderização:** `Voracity.Core` compila para `net8.0;net10.0` sem nenhuma dependência da Godot (`Godot.*`), permitindo evolução visual 2D → 3D, execução em servidores/CLI e testes ultrarrápidos sem reescrever regras.
- **Desempenho determinístico (*Zero-GC Hot Path*):** *Pops* e províncias vivem em `struct`s e *arrays* contíguos (`PopRecord`, `ProvinceSlot`, `StateBuildingSlot`), reduzindo pausas de *Garbage Collector* durante avanços rápidos.
- **Modding nativo em JSON:** Todo o conteúdo base reside em [`data/base/`](data/base/) e aceita sobrescrita ou adição por `id` via [`data/mods/`](data/mods/).
- **Manutenibilidade:** UI, câmera e camadas de mapa são escritas em C# tipado, evitando quebras de cenas `.tscn` em revisões ou desenvolvimento assistido por IA.

---

## Primeiros passos

### Pré-requisitos

| Ferramenta | Uso | Observação |
| :--- | :--- | :--- |
| **.NET SDK 8.0+ / 10.0** | `Core`, `Cli`, `Tests` e *scripts* C# | Multi-target `net8.0;net10.0` no núcleo |
| **Godot Engine 4.7.2+ (.NET / Mono)** | Cliente gráfico Vulkan (`Forward+`) | Necessário apenas para executar `Voracity.Client` |
| **GPU compatível com Vulkan** | Renderização do mapa e shaders | Testado nativamente no Linux Vulkan |

### Compilar e rodar os testes

Clone o repositório e execute a suíte completa na raiz (`voracity-repo/`):

```bash
git clone <url-do-repositorio>
cd voracity-repo

dotnet build Voracity.slnx
dotnet test --no-build
```

### Executar o jogo (Cliente Vulkan)

No **Linux**, utilize o script de execução [`play_linux.sh`](play_linux.sh), que configura `DOTNET_ROLL_FORWARD=Major`, compila o projeto Godot e inicia via `godot4-mono`:

```bash
./play_linux.sh
```

No **Windows** (ou abrindo via Editor Godot), abra [`src/Voracity.Client/project.godot`](src/Voracity.Client/project.godot) na **Godot 4.7.2 .NET** e execute o projeto.

### Rodar simulação *headless* (CLI)

Para testar desempenho, balanceamento ou simular anos inteiros de Pontária diretamente no terminal (sem precisar da Godot):

```bash
dotnet run --project src/Voracity.Cli/Voracity.Cli.csproj -- --nation AET --days 360
```

Parâmetros suportados em [`Program.cs`](src/Voracity.Cli/Program.cs):
- `--nation <ID>`: código da nação inicial (ex.: `AET`, `HAS`, `DAL`, `NEC`; padrão `AET`).
- `--days <N>`: quantidade de dias a simular (1 dia = 4 sub-ticks de 6h; padrão `360`).

### Usar o motor *Core* via código

Exemplo mínimo de carregamento de dados/mods, emissão de comandos e extração de *snapshot* em C#:

```csharp
using Voracity.Core.Data;
using Voracity.Core.Engine;
using Voracity.Core.Models;

var db = ModdingDataLoader.Load("data");
var world = new SimulationWorld(db, playerNationId: "AET", seed: 13800101UL);

world.EnqueueCommand(new UpgradeProvinceHoldingCommand("prov_grimwall", "holding"));
world.StepDays(30);

RenderSnapshot snapshot = world.BuildRenderSnapshot();
Console.WriteLine($"{snapshot.Date.ToDisplayString()} — Tesouro: {snapshot.Treasury.ToUiString()}");
```

---

## Estrutura do repositório

```text
.
├── Voracity.slnx                   # Solution .NET unificada
├── CONTRIBUTING.md                 # Diretrizes de arquitetura, Anti-GC e testes
├── play_linux.sh                   # Build + launch Vulkan no Linux
├── data/
│   ├── base/                       # Dados declarativos oficiais (bens, nações, províncias, idiomas)
│   └── mods/                       # Mods em camadas (manifest.json + override por id)
└── src/
    ├── Voracity.Core/              # Simulação pura .NET (zero dependência Godot)
    ├── Voracity.Client/            # Cliente Godot 4 Vulkan Code-First + shaders
    ├── Voracity.Cli/               # Runner headless de simulação e benchmark
    └── Voracity.Tests/             # Testes Unitários e de Integração (xUnit)
```

---

## Onde obter ajuda

- **Arquitetura e fluxo de desenvolvimento:** consulte [`CONTRIBUTING.md`](CONTRIBUTING.md).
- **Criação e carregamento de mods JSON:** veja os exemplos e regras de mesclagem em [`data/mods/README.md`](data/mods/README.md).
- **Exemplos reais de uso do motor:** explore os cenários determinísticos em [`src/Voracity.Tests/`](src/Voracity.Tests/) e o ponto de entrada CLI em [`src/Voracity.Cli/Program.cs`](src/Voracity.Cli/Program.cs).
- **Bugs e sugestões:** abra uma *Issue* no repositório indicando sistema operacional, comando executado e se há mods ativos em `data/mods/`.

---

## Manutenção e contribuições

O projeto é mantido por [Wilton Gabriel](https://github.com/WiltonGabriel) e colaboradores em fase **Alpha / Concepção**, com abertura para contribuições na simulação (`Voracity.Core`), ferramentas de mapa/UI (`Voracity.Client`), localização e conteúdo declarativo (`data/base/`).

- Antes de abrir um *Pull Request*, leia [`CONTRIBUTING.md`](CONTRIBUTING.md) para seguir as convenções de arquitetura modular, regras *Anti-GC* nos loops de simulação e cobertura obrigatória com testes `xUnit`.
- Toda participação na comunidade segue nosso [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md).
- Distribuído sob os termos da licença indicada em [`LICENSE`](LICENSE).
