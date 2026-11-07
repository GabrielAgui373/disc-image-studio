# Desenvolvimento da GUI Linux/Avalonia

Este documento descreve o desenvolvimento da GUI portátil do Disc Image Studio. O host Avalonia compõe os serviços de plataforma, persiste preferências e recentes em diretórios XDG e já oferece shell responsivo, navegação, feedback global e diagnóstico técnico. O ciclo funcional de projetos começa na Task 07. Os motores de CD, DVD e Blu-ray, Core, CLI, ImageIO e ISO também fazem parte da solução Linux.

O backlog completo está em [project-analysis/tasks/00.index.md](project-analysis/tasks/00.index.md).

## Escopo da solução Linux

`DiscImageStudio.Linux.slnx` contém somente projetos compiláveis como `net10.0` no Linux:

- `DiscImageStudio.App.Avalonia`;
- `DiscImageStudio.Application`, com casos de uso e estado tipado compartilháveis;
- `DiscImageStudio.Cli`;
- `DiscImageStudio.Core`;
- `DiscImageStudio.ImageIO`, usando o codec portátil;
- `DiscImageStudio.Iso`;
- `DiscImageStudio.Cd`;
- `DiscImageStudio.Dvd.Engine`, proprietário portátil do encoder e solver de DVD;
- `DiscImageStudio.Dvd`, com o módulo e os adapters de geração;
- `DvdImageSolver`, host fino dos comandos históricos e do self-test;
- `DiscImageStudio.BluRay`;
- `DiscImageStudio.Generation`, com requests, planos, validação e geração file/stream tipados;
- `DiscImageStudio.PortableArchitectureTests`.

Os projetos WPF, Imaging/WPF, Burning/IMAPI e o harness Windows não pertencem a essa solução. Eles continuam na solução principal e no workflow Windows.

## Pré-requisitos no Fedora

Instale o SDK definido por `global.json` e as bibliotecas do backend desktop X11/XWayland:

```bash
sudo dnf install dotnet-sdk-10.0 libX11 libICE libSM fontconfig
```

Confirme o SDK selecionado:

```bash
dotnet --info
```

O template Avalonia só é necessário para criar novos projetos/controles via `dotnet new`; ele não é necessário para compilar o repositório. Para instalá-lo na versão usada pelo projeto:

```bash
dotnet new install Avalonia.Templates@12.1.4
```

## Restaurar, compilar e testar

Na raiz do repositório:

```bash
dotnet restore DiscImageStudio.Linux.slnx
dotnet build DiscImageStudio.Linux.slnx --configuration Release --no-restore
dotnet test tests/DiscImageStudio.PortableArchitectureTests \
  --configuration Release --no-build --no-restore
```

Os testes portáveis bloqueiam referências acidentais a WPF, Windows Forms, WindowsDesktop, `DiscImageStudio.App`, `DiscImageStudio.Imaging` e `DiscImageStudio.Burning` na nova GUI e nos assemblies portáveis.

Os contratos, ownership e regras de threading da camada portátil estão em [APPLICATION_LAYER.md](APPLICATION_LAYER.md).
O pipeline compartilhado de geração está em [GENERATION_PIPELINE.md](GENERATION_PIPELINE.md).
A decisão e o contrato da composição de imagem portátil estão em [IMAGE_COMPOSITION.md](IMAGE_COMPOSITION.md).
O modelo de edição, o histórico (undo/redo) e a opacidade no schema 3 estão em [DESIGN_EDITING.md](DESIGN_EDITING.md).

## Executar o host Avalonia

Execute dentro de uma sessão gráfica do Fedora:

```bash
dotnet run --project src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj
```

A janela de diagnóstico mostra informações de versão/runtime e diretórios XDG, além de ações para copiar o diagnóstico ou abrir a pasta de logs. O backend desktop requer uma sessão X11/XWayland; se `DISPLAY` estiver ausente, o processo encerra com uma mensagem de orientação em vez de uma falha pouco clara. A abertura da janela deve ser confirmada manualmente em desktop Fedora; o build/teste não substitui esse smoke visual.

### Executar com reload durante o desenvolvimento

Para manter a aplicação aberta e observar alterações nos arquivos C# e Avalonia XAML (`.axaml`), use:

```bash
dotnet watch run --project src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj
```

O `dotnet watch` aplica Hot Reload às alterações C# compatíveis. Alterações `.axaml` recompilam e reiniciam o processo da aplicação; nesse caso o estado em memória é perdido. O `Ctrl+R` no terminal força rebuild e reinício. A lista `Watch` do projeto inclui explicitamente os arquivos `.axaml`, que não apareciam na lista padrão observada pelo SDK.

### Checklist visual do shell

Em uma sessão Fedora, valide manualmente:

- janela em 1024×720 e em largura ampla, com conteúdo alcançável por scroll;
- sidebar expandida e compacta, sem deslocamento dos ícones nem clipping da seleção;
- tooltips dos itens compactos, foco visível e atalhos `Alt+H/D/P/E/B/S`;
- temas claro, escuro e preferência do sistema;
- toast, histórico do `JobDock` e prompt modal, incluindo cancelamento por `Escape`;
- textos longos com wrapping/trimming sem esconder ações críticas.

Snapshots e testes Avalonia Headless serão mantidos em um módulo de testes dedicado; não fazem parte da conclusão atual da Task 06.

Preferências e recentes são gravados sob os caminhos XDG (`XDG_CONFIG_HOME`, `XDG_DATA_HOME`, `XDG_CACHE_HOME` e `XDG_STATE_HOME`, com fallback para `~/.config`, `~/.local/share`, `~/.cache` e `~/.local/state`). Não é necessário executar como root.

## Publicar um smoke framework-dependent

```bash
dotnet publish src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output artifacts/linux-gui/publish
```

Esse pacote exige o runtime .NET 10 instalado. Empacotamento self-contained, RPM, associação de `.discproj` e integração completa com desktop pertencem à Task 16.

## Guardrails arquiteturais

- Avalonia é uma camada de apresentação; motores e casos de uso não podem depender dela.
- A GUI Linux não referencia a aplicação WPF nem assemblies Windows-only.
- `DiscImageStudio.Linux.slnx` é o comando de build Linux; `DiscImageStudio.slnx` continua sendo a solução completa validada em Windows.
- Gravação física ainda é Windows-only. O futuro backend Linux será ativado por capacidade e não bloqueará geração/exportação.
- Parsing de argumentos da CLI não deve se tornar a API interna da GUI.

## Matriz inicial de paridade

| Área | WPF | Avalonia/Linux | Task responsável |
| --- | --- | --- | --- |
| Inicialização desktop | Completa | Host, shell responsivo, navegação e diagnóstico técnico | 01, 05, 06 |
| Home e ciclo do projeto | Completa | Não implementado | 07 |
| Novo projeto/configurações | Completa | Não implementado | 07 |
| Design/camadas/tom | Completa | Não implementado | 08, 09 |
| Undo/redo | Não disponível | Planejado | 09 |
| Prévia e readback | Completa | Não implementado | 10 |
| Exportação | Completa | Não implementado | 03, 11 |
| Calibração | Completa | Não implementado | 12 |
| Gravação | IMAPI/MMC | Não disponível | 13 |
| Temas/localização | Completa | Tema do sistema/claro/escuro e inglês inicial; i18n planejada | 05, 06, 14 |
| Settings e Sobre | Completa | Persistência e diagnóstico técnico; telas ainda não implementadas | 05, 07 |
| Empacotamento | MSIX/release Windows | Não implementado | 16 |

## Diagnóstico rápido

- `NETSDK` incompatível: confirme que `dotnet --info` respeita o `global.json`.
- Falha ao abrir janela: confirme que a sessão possui `DISPLAY` e que as bibliotecas X11 estão instaladas.
- Falha de restore: confirme acesso ao NuGet e tente novamente sem `--no-restore`.
- Erro WindowsDesktop ao compilar no Linux: use `DiscImageStudio.Linux.slnx`, não a solução completa.
