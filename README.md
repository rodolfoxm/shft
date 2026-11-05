# shft.

Ambiente de desktop para Windows num único app nativo: barra superior, Dock, Central de Controle e Applications, com a mesma identidade visual. C# / .NET 8 (WPF), sem WebView.

Substitui o ClonaDock e o Seelen UI modificado. O Dock é o que existia; a barra e os painéis foram escritos para este projeto, usando direto as APIs do Windows.

## O que faz

- **Barra superior** em cada monitor: logo (clique: menu do usuário; botão direito: trocar), nome, app em foco e os menus clássicos dele quando existem, memória (clique libera RAM ociosa), ícones fixados da bandeja, Bluetooth, rede, volume (a roda do mouse ajusta), notificações, bandeja, Central de Controle e relógio/calendário.
- **Três fundos para a barra:** sólido, translúcido e adaptativo. No adaptativo ela fica transparente e cada ponta escolhe texto claro ou escuro pelo que está atrás, com uma camada suave quando o fundo é misto.
- **Central de Controle:** rede, Bluetooth e dispositivos pareados, mídia em reprodução, modo escuro, HDR, luz noturna, limpar RAM, brilho por monitor (DDC/CI), volume e microfone com escolha de dispositivo, bateria quando há, energia.
- **Applications:** busca estilo Spotlight de todos os apps do Windows, incluindo os da Store. Setas, Enter e Esc; favoritos e recentes; aceita caminho, endereço e programa do PATH.
- **Dock:** ampliação, apps abertos, pastas, 13 widgets, Lixeira e selos, como antes.
- **Configurações** centralizadas; quase tudo aplica na hora.

## Compilar e executar

Requisitos: Windows 10 (build 19041) ou superior e .NET 8 SDK. Detalhes em [docs/onboarding.md](docs/onboarding.md).

```powershell
dotnet build shft.csproj
dotnet test tests/shft.Tests
dotnet publish shft.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o ../shft-app
```

| Comando | Efeito |
|---|---|
| `shft.exe` | Abre, ou mostra as Configurações se já estiver aberto |
| `shft.exe --quit` | Encerra de forma limpa, restaurando a barra de tarefas e o espaço reservado |
| `shft.exe --toggle-apps` | Abre ou fecha Applications (útil no Stream Deck) |
| `shft.exe --toggle-controls` | Abre ou fecha a Central de Controle |

`--watchdog <pid>` e `--purge` são de uso interno. A variável `SHFT_DATA` aponta o app para outra pasta de dados, para testar ao lado da cópia em uso.

## Onde ficam os dados

Pasta `%APPDATA%\shft`. Na primeira execução, as configurações de `%APPDATA%\ClonaDock` e a logo da barra do Seelen são copiadas para lá; os originais ficam intactos.

| Arquivo | Conteúdo |
|---|---|
| `settings.json` | Configurações e itens do Dock (troca atômica via `.tmp`) |
| `settings.json.broken` | Cópia do arquivo quando a leitura falha |
| `logo.png` | Logo da barra |
| `shft.log` | Log de diagnóstico; passa para `.old` ao chegar a 512 KB |
| `taskbar-restore.json` | Marcador temporário enquanto a barra de tarefas está oculta |

## Documentação

[Produto](PRODUCT.md), [Arquitetura](docs/architecture.md), [Design](docs/design.md), [Preparação local](docs/onboarding.md), [Qualidade](docs/quality.md), [Setup](docs/setup.md), [Avisos de terceiros](THIRD-PARTY-NOTICES.md), [Instruções para agentes](AGENTS.md).
