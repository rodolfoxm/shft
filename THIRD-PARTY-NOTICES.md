# Avisos de terceiros

## Dependências incluídas

| Componente | Licença | Uso |
|---|---|---|
| Microsoft.Data.Sqlite | MIT | Ler o banco local de notificações do Windows para os selos do Dock |
| SQLitePCLRaw | Apache-2.0 | Dependência do item acima |
| SQLite (e_sqlite3) | Domínio público | Dependência do item acima |
| .NET Runtime e Windows Desktop Runtime | MIT | Incluídos no instalador e no pacote portátil |
| Vortice.Windows (Direct3D11, DXGI, DirectComposition, D3DCompiler) | MIT | Desenho do vidro real na placa de vídeo |
| xUnit (somente testes) | Apache-2.0 | Testes automatizados; não vai no app publicado |
| Inno Setup (somente build) | Licença própria, permite distribuir instaladores | Gera o instalador; não é uma dependência do app |

## Código adaptado

- **Glasmio** (Copyright 2026 NEORUAA, Apache-2.0), que por sua vez adapta o **AndroidLiquidGlass** (Copyright 2025 Kyant, Apache-2.0): a curva de refração do vidro (deslocamento `amount × (1 − √(u(2−u)))` ao longo da normal de um retângulo arredondado) foi portada para HLSL em `Native/Glass/GlassShader.hlsl`. Nenhum asset do Glasmio nem da Apple foi copiado. Licença: https://www.apache.org/licenses/LICENSE-2.0

## Referências sem código copiado

- **Seelen UI** (AGPL-3.0): serviu de referência de experiência e funcionalidade. Nenhum código, ícone ou recurso dele foi copiado para o shft., que conversa direto com as APIs do Windows. Se isso mudar, as obrigações da AGPL passam a valer para o shft.
- **PrimoDock 0.6.0-beta:** comportamento observado na origem do Dock, sem código do original.

## Sistema

Os ícones da interface vêm da fonte Segoe Fluent Icons, instalada com o Windows; ela não é redistribuída. A logo escolhida pelo usuário fica só na pasta de dados dele.
