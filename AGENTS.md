# ClonaDock — instruções para agentes

Dock estilo macOS para Windows em C#/.NET 8 WPF. Produto: [PRODUCT.md](PRODUCT.md). Arquitetura: [docs/architecture.md](docs/architecture.md). Setup e evidências: [docs/setup.md](docs/setup.md).

## Stack

.NET 8 (`net8.0-windows10.0.19041.0`), WPF + interop WinForms, P/Invoke Win32, `Microsoft.Data.Sqlite` (só para ler o banco de notificações do Windows). Configurações em JSON (`%APPDATA%\ClonaDock\settings.json`). SDK .NET 8 na máquina.

## Comandos canônicos

- Restaurar/compilar: `dotnet build` (analisadores .NET com avisos CA tratados como erro)
- Rodar: `dotnet run` — com a cópia em uso rodando (`../ClonaDock-app`), a instância única só abre as Configurações e o build local não sobe. Testar o build local exige encerrar a cópia em uso com `ClonaDock.exe --quit`: **peça ao usuário antes** e, ao terminar, reabra `../ClonaDock-app/ClonaDock.exe`
- Lint/estilo: analisadores no build; `dotnet format whitespace --verify-no-changes` é informativo (diferenças de formatação antigas, veja `docs/quality.md`)
- Tipos: compilador C# com `Nullable` habilitado
- Testes: não há projeto de testes (pendência registrada em `docs/quality.md`)
- Verificar tudo: `dotnet build`

## Regras do projeto

- O app mexe em estado global do Windows: oculta a barra de tarefas, reserva área de tela (app bar) e grava `HKCU\…\Run\ClonaDock`. Qualquer mudança nesses caminhos precisa preservar a recuperação (`taskbar-restore.json`, processo `--watchdog`, `RecoverFromCrash`).
- A cópia em uso fica em `../ClonaDock-app` (autoinício). Nunca a sobrescreva; encerrá-la para testar segue a regra de `Rodar` acima (pedir antes, reabrir depois). Teste builds locais a partir de `bin/`.
- Memória é requisito: o csproj usa GC de estação com compactação agressiva. Não adicione dependências pesadas nem timers frequentes sem medir.
- Arquivos em UTF-8 (há textos acentuados); evite ferramentas que regravem com outra codificação.
- Rede só no widget de clima (Open-Meteo). Não adicione outras chamadas sem pedido.

## Acordos de trabalho

- Leia as instruções e o código relevante antes de editar. Separe observação, premissa, decisão e evidência.
- Defina critério de aceite e comando de verificação antes de implementar. Correção de bug: reproduza e prove a regressão quando viável.
- Menor mudança suficiente; reuse padrões e dependências. Sem abstrações, opções ou dependências para necessidades não pedidas.
- Preserve mudanças locais, validação, tratamento de erro, segurança e acessibilidade.
- Atualize o documento responsável junto com a mudança: `PRODUCT.md`, `docs/architecture.md`, `docs/design.md`, `docs/onboarding.md`, `docs/quality.md`, `docs/setup.md`.
- Publicar, enviar mensagens, custos e ações destrutivas exigem autorização específica; instalar uma ferramenta não autoriza usá-la para isso.

## Continuidade

- Durante trabalho autorizado, responda perguntas de status e continue. Um bloqueio para só o que depende dele.
- Trabalho de várias sessões: mantenha um handoff curto (objetivo, estado verificado, checks, pendências, próxima ação). Ao retomar, confira arquivos, `git status`, checks e processos reais antes de confiar nele.
- Não repita a mesma tentativa sem evidência nova; não prometa execução em segundo plano sem um processo verificável.

## Ferramentas

Verifique instalação e descoberta antes de depender de uma ferramenta; ausência vira registro em `docs/setup.md`.

- **Superpowers:** skills de processo proporcionais à tarefa; instruções explícitas vencem aprovações repetidas.
- **Ponytail** (`full`): menor solução completa. `stop ponytail` desliga.
- **Caveman:** conversa concisa em pt-BR; código, docs e commits em texto normal. `stop caveman` desliga.
- **Context7:** documentação atual de WPF, .NET, Win32/CsWin32 e Microsoft.Data.Sqlite; uma consulta por conceito, sem código privado.
- **RTK:** com o hook nativo ativo, comandos normais; `rtk bypass <cmd>` para saída completa (na RTK 1.0.1 o antigo `rtk proxy` virou `bypass`; não use `llm-proxy`).
- **Graphify:** `GRAPHIFY_NO_AUTO_REFRESH=1 graphify query "…"` em `graphify-out/` (só código; regenere com `graphify extract . --code-only --no-cluster` e confirme no código).
- **Skills locais** (`.claude/skills/`): `writing-guidelines` para docs; `find-skills` quando faltar uma capacidade. As skills de React não se aplicam.
- **Modelos:** preserve o cliente, o modelo principal e a autenticação. Subagentes podem usar o modelo mais leve que cumpra o critério; aumente a capacidade para interop Win32, barra de tarefas/app bar e revisão crítica.

## Delegação e revisão

- Delegue só quando isolamento, especialização ou paralelismo independente compensarem. Cada tarefa recebe objetivo, contexto mínimo, arquivos permitidos, critério de aceite e checks. Um escritor por arquivo; csproj, configuração e git ficam com o coordenador. Revisores são somente leitura.
- Confira diff e checks antes de aceitar o resultado de um subagente.
- Revisão proporcional ao risco e ao diff real; mudanças não triviais têm revisão independente quando disponível. Termine com achados resolvidos ou com destino explícito, checks passando e riscos residuais registrados.
- Revisão de segurança (somente leitura, relatório privado fora do repositório): [docs/prompts/security-review.md](docs/prompts/security-review.md).
- Melhoria do harness (modo nativo, medido): [docs/quality.md](docs/quality.md#harness).

## Git

Repositório local, sem remoto. Sem hooks de commit/push, sem exigência de branch. Commits só quando pedidos ou ao fechar uma etapa autorizada.
