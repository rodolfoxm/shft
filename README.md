# ClonaDock

Dock no estilo do macOS para Windows, com widgets. Aplicativo desktop em C# / .NET 8 (WPF, com interop WinForms para o ícone da bandeja). Segundo a tela "Sobre", é uma reimplementação independente, feita a partir do comportamento observado do PrimoDock 0.6.0-beta, sem código do original.

Versão no `.csproj`: `0.1.0`.

## O que faz (confirmado no código)

- Barra (dock) em uma das quatro bordas da tela, em qualquer monitor, com ampliação dos ícones ao passar o mouse e animação de abertura (bounce).
- Apps fixados, apps abertos (após um separador), pastas, arquivos, espaçadores e widgets; reordenar arrastando, remover arrastando para fora, soltar arquivos para adicionar.
- Botão de apps (lançador pesquisável com os atalhos do menu Iniciar), pasta Downloads, Lixeira e selos de notificação lidos do Windows.
- 13 widgets: Relógio, Calendário, Clima, Nota adesiva, Lista de tarefas, Timer de foco, Atividade do sistema, Atividade de rede, Bateria, Beber água, Contagem regressiva, Progresso do tempo e Tocando agora.
- Aparência: tema automático/claro/escuro, vidro (desfoque ou acrílico), "Liquid Glass", cores e arredondamento ajustáveis.
- Comportamento: ocultar automaticamente, reservar espaço de tela (app bar), esconder com app em tela cheia, ocultar/auto-ocultar a barra de tarefas do Windows e iniciar com o Windows.
- Ícone na bandeja (Configurações, Adicionar um app, Encerrar) e janela de configurações com cinco páginas.

A interface está em português do Brasil, escrita diretamente no código (não há arquivos de recursos de idioma).

## Compilar e executar

Requisitos: Windows 10 (build 19041) ou superior e .NET 8 SDK. Detalhes em [docs/onboarding.md](docs/onboarding.md).

```powershell
dotnet build
dotnet run
```

- O projeto não define perfil de publicação nem script de empacotamento/instalador. Publicação e distribuição não estão documentadas porque não há mecanismo no código ou no `.csproj`; `dotnet publish` padrão não foi verificado.
- O app é instância única (mutex `ClonaDock.SingleInstance`): abrir uma segunda cópia só faz a primeira mostrar as configurações.
- `ClonaDock.exe --quit` pede ao processo em execução que encerre de forma limpa (restaura a barra de tarefas e o espaço reservado).
- `ClonaDock.exe --watchdog <pid>` é de uso interno (processo auxiliar que restaura a barra de tarefas).

## Onde ficam os dados

Pasta `%APPDATA%\ClonaDock` (o botão "Abrir pasta de dados" em Configurações > Sobre abre ela):

| Arquivo | Conteúdo |
|---|---|
| `settings.json` | Todas as configurações e os itens do Dock (JSON, escrita via arquivo `.tmp` e troca atômica) |
| `settings.json.broken` | Cópia do arquivo quando a leitura falha |
| `clonadock.log` | Log de diagnóstico; ao passar de 512 KB vira `clonadock.log.old` |
| `taskbar-restore.json` | Marcador temporário enquanto a barra de tarefas está oculta, usado para recuperar de uma falha |

O app também cria a chave `ClonaDock` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` quando "Abrir o ClonaDock quando o Windows iniciar" está ligado.

## Documentação

- [Produto e escopo](PRODUCT.md)
- [Arquitetura](docs/architecture.md)
- [Design e acessibilidade](docs/design.md)
- [Preparação local](docs/onboarding.md)
- [Qualidade](docs/quality.md)
- [Setup e evidências](docs/setup.md)
- [Instruções para agentes](AGENTS.md)
