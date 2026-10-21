# Preparação local

## Pré-requisitos

- Windows 10 build 19041 ou superior (o projeto mira `net8.0-windows10.0.19041.0`; o app usa APIs do Windows e não roda em outros sistemas).
- .NET 8 SDK. Confira com `dotnet --list-sdks` (deve listar uma versão 8.x).
- Um editor C# (Visual Studio, Rider ou VS Code); não há solução `.sln`, só `ClonaDock.csproj`.
- Acesso à internet só é necessário para restaurar o pacote NuGet `Microsoft.Data.Sqlite` e, em execução, para o widget Clima.

## Primeira execução

```powershell
cd ClonaDock
dotnet build
dotnet run
```

- `dotnet run` abre o Dock na borda inferior e, na primeira vez (sem `%APPDATA%\ClonaDock\settings.json`), a janela de Configurações.
- O app é instância única. Se já houver um ClonaDock rodando (por exemplo o instalado em execução automática), `dotnet run` apenas pede a ele que mostre as Configurações e termina; para testar o build local, encerre o outro com `ClonaDock.exe --quit` (se for a cópia em uso do usuário, peça antes) e reabra `../ClonaDock-app/ClonaDock.exe` ao terminar.
- Cuidado ao testar: o app pode ocultar a barra de tarefas do Windows e reservar área de tela. Se algo falhar, o marcador `%APPDATA%\ClonaDock\taskbar-restore.json` e o próximo start restauram a barra; ver [architecture.md](architecture.md).
- Não há variáveis de ambiente, segredos nem banco de dados a configurar.

## Depurar

- Abra o `.csproj` no IDE e rode com F5; o ponto de entrada é `Program.Main`.
- Logs: `%APPDATA%\ClonaDock\clonadock.log` (também acessível em Configurações > Sobre > Abrir pasta de dados).
- As configurações são um JSON legível em `%APPDATA%\ClonaDock\settings.json`; apague-o para voltar aos padrões.

## Por onde começar a ler

1. `Program.cs` e `App.xaml.cs`: inicialização e encerramento.
2. `Dock/DockController.cs`: o que o Dock mostra e o que cada clique faz.
3. `Dock/DockWindow.cs` e `Native/AppBar.cs`: posição, espaço reservado, auto-ocultar.
4. `Native/WindowTracker.cs` e `Native/TaskbarControl.cs`: janelas abertas e barra de tarefas.
5. `Core/AppSettings.cs` e `Core/SettingsStore.cs`: o que é persistido.
6. `Widgets/WidgetBase.cs` e `Widgets/WidgetFactory.cs`: como criar um widget novo.

Mapa completo: [architecture.md](architecture.md).

## Verificar

| Verificação | Comando | Resultado esperado |
|---|---|---|
| Compilação e analisadores | `dotnet build` | Sem erros. Avisos CA (analisadores .NET, `AnalysisLevel=8.0`) são tratados como erro por `CodeAnalysisTreatWarningsAsErrors` |
| Formatação (informativo) | `dotnet format whitespace --verify-no-changes` | Hoje relata diferenças de formatação preexistentes; não bloqueia |
| Testes automatizados | não existem | Não há projeto de teste nem comando de teste. A verificação do comportamento é manual, a partir dos critérios de [PRODUCT.md](../PRODUCT.md) |

Detalhes de qualidade: [quality.md](quality.md).

## Agentes

Leia [AGENTS.md](../AGENTS.md). Consulte [setup.md](setup.md) para skills locais, integrações e escopos configurados.

## Problemas conhecidos

- Segunda instância não inicia: comportamento esperado do mutex de instância única (ver acima).
- Arquivos `.cs` contêm texto em português com acentos; abra e salve sempre em UTF-8, ou os textos da interface aparecem corrompidos.
- Nenhum outro problema foi observado e documentado.
