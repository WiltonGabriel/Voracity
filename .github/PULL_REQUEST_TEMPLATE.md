## Resumo da Mudança

Descreva de forma objetiva o que foi alterado ou adicionado nesta contribuição.

## Tipo de Alteração

- [ ] Motor de Simulação (`Voracity.Core`)
- [ ] Cliente Gráfico / Shader / HUD (`Voracity.Client` — Godot 4 Vulkan Code-First)
- [ ] Dados / Conteúdo Declarativo / Localização (`data/base/`)
- [ ] Suporte a Mods (`data/mods/`)
- [ ] Testes (`Voracity.Tests`) ou Documentação

## Checklist Obrigatória do Projeto

- [ ] `Voracity.Core` permanece **100% livre** de dependências `Godot.*` (`net8.0;net10.0`).
- [ ] Loops de *tick* (6h / diário / semanal / mensal) respeitam a regra **Anti-GC** (sem alocações `new List` / `LINQ` no *hot path*).
- [ ] UI e nós em `Voracity.Client` seguem o padrão **Code-First** (sem edições manuais complexas em `.tscn`).
- [ ] Toda nova regra de média/alta importância possui testes automatizados em `Voracity.Tests/Unit/` ou `Voracity.Tests/Integration/`.
- [ ] Chaves novas de interface/conteúdo possuem paridade em `pt-BR.json` e `en-US.json`.
- [ ] `dotnet build Voracity.slnx` e `dotnet test` passaram localmente com **0 erros e 0 avisos**.
