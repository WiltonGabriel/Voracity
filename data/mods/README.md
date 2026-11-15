# Sistema de Mods de Voracity (Camadas Declarativas)

Inspirado na filosofia *data-driven* de **OpenVic** e **Clausewitz/Jomini**:

1. O motor carrega primeiro os arquivos oficiais de `../base/`.
2. Em seguida, carrega em ordem alfabética (ou pelo `load_order`) cada subpasta de mod que contenha um `manifest.json` habilitado.
3. Regras de mesclagem:
   - Registros com mesmo `id` **sobrescrevem** a definição base (`goods`, `ancestries`, `nations`, `states`).
   - Novos `id`s são **adicionados** ao catálogo sem alterar código C#.

## Exemplo de `data/mods/meu_mod/manifest.json`

```json
{
  "id": "meu_mod",
  "name": "Meu Mod de Balanceamento",
  "version": "0.1.0",
  "loadOrder": 100,
  "enabled": true
}
```
