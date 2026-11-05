# Desenvolvimento da GUI Linux/Avalonia

Este documento descreve o baseline portátil da GUI do Disc Image Studio. A interface Avalonia está no início da implementação e exibe um Hello World; os motores de CD, DVD e Blu-ray, o Core, a CLI, ImageIO e ISO já fazem parte da solução Linux.

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
- `DiscImageStudio.Dvd`, usando a hospedagem portátil atual do solver;
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

## Executar o Hello World

Execute dentro de uma sessão gráfica do Fedora:

```bash
dotnet run --project src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj
```

A janela deve exibir `Hello, Linux!`. O backend desktop atual usa a detecção de plataforma do Avalonia e funciona em X11 ou XWayland. Executar em terminal/headless sem `DISPLAY` disponível não abre a janela.

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
| Inicialização desktop | Completa | Hello World | 01, 05 |
| Home e ciclo do projeto | Completa | Não implementado | 07 |
| Novo projeto/configurações | Completa | Não implementado | 07 |
| Design/camadas/tom | Completa | Não implementado | 08, 09 |
| Undo/redo | Não disponível | Planejado | 09 |
| Prévia e readback | Completa | Não implementado | 10 |
| Exportação | Completa | Não implementado | 03, 11 |
| Calibração | Completa | Não implementado | 12 |
| Gravação | IMAPI/MMC | Não disponível | 13 |
| Temas/localização | Completa | Tema Fluent padrão | 06, 14 |
| Settings e Sobre | Completa | Não implementado | 05, 07 |
| Empacotamento | MSIX/release Windows | Não implementado | 16 |

## Diagnóstico rápido

- `NETSDK` incompatível: confirme que `dotnet --info` respeita o `global.json`.
- Falha ao abrir janela: confirme que a sessão possui `DISPLAY` e que as bibliotecas X11 estão instaladas.
- Falha de restore: confirme acesso ao NuGet e tente novamente sem `--no-restore`.
- Erro WindowsDesktop ao compilar no Linux: use `DiscImageStudio.Linux.slnx`, não a solução completa.
