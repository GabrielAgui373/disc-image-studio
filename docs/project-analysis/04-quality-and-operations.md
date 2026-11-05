# Qualidade, testes e operação

[← Código e fluxos](03-code-and-flows.md) · [Índice](README.md) · [Riscos e roadmap →](05-risks-and-roadmap.md)

## Postura geral de qualidade

O projeto demonstra uma cultura forte de regressão. Há testes para algoritmos físicos, formatos, persistência, ViewModels, controles, temas, localização, layout e lifecycle de gravação. Comentários no código normalmente explicam invariantes e motivos, não apenas a operação óbvia.

Os principais mecanismos preventivos são:

- `Nullable` e `TreatWarningsAsErrors` nos projetos principais;
- builds determinísticos;
- validação estrita de JSON e escrita atômica;
- planners antes de operações sequenciais longas;
- cancelamento e gates de concorrência;
- testes de byte order, ECC, layouts e sidecars;
- CI em Windows, Linux e macOS para os alvos aplicáveis.

## Estratégia de testes

### Harness principal

`DiscImageStudio.ArchitectureTests` é um executável próprio. `Program.cs` chama diretamente mais de 50 grupos de testes, que cobrem aproximadamente 574 métodos/casos reconhecíveis por inspeção estática.

Cobertura relevante:

- catálogo modular, presets, projeto, sessão e dirty state;
- composição, zoom, previews e posição de filesystem;
- CD binário/quatro níveis, SIMD, byte order e DAO;
- DVD sampling, cubic pitch, padrões e filesystems;
- UDF, arquivos grandes, encoding e layouts BD;
- gravação sequencial, cleanup, progress e buffer;
- calibração matemática, foto, canvas, burn e publicação;
- settings, cache, temas, localização e bounds de janela.

O harness sem framework reduz dependências e inicia rápido. Em contrapartida:

- `dotnet test` não descobre os casos;
- falhas não aparecem como testes individuais em IDE/CI;
- filtering, traits e cobertura exigem infraestrutura própria;
- todo o projeto targeta `net10.0-windows`, mesmo quando muitos testes são portáveis.

A recomendação é incremental: manter vetores/self-tests especializados, mas mover testes de Core, ISO, CD, BD e persistência para um projeto `net10.0` descobrível. Testes WPF/IMAPI permanecem em um projeto Windows.

### Self-tests e pacotes

`DvdImageSolver` mantém um comando `selftest` próprio, executado separadamente no CI. A CLI publicada é extraída e testada no target real em Linux x64/ARM64 e macOS x64/ARM64. O pipeline Windows também gera um CD mínimo, valida seu tamanho exato e renderiza uma captura WPF.

Essa combinação encontra problemas de publishing que um simples build não encontra. Ao extrair o engine DVD para biblioteca, o self-test deve continuar como consumidor público da mesma biblioteca.

## CI e release

O workflow principal executa:

1. publish self-contained da CLI por RID;
2. smoke test do pacote extraído;
3. restore/build Release no Windows;
4. validação dos scripts de release;
5. harness de arquitetura;
6. self-tests do DVD e do catálogo modular;
7. smoke CD;
8. snapshot WPF.

Workflows separados produzem MSIX e releases. Licenças e notices são incluídos na distribuição.

Pontos positivos:

- o binário publicado, não apenas o projeto, é testado;
- Windows e portabilidade são validados separadamente;
- release depende do workflow de validação;
- permissões do GitHub Actions são mínimas onde possível;
- pacotes são self-contained.

Melhorias recomendadas:

- cache de NuGet;
- SBOM e inventário de dependências no artefato;
- assinatura/verificação automatizada do MSIX;
- resultados de testes em formato padrão;
- job portável que rode testes de Core/ISO/motores em Linux;
- snapshot comparativo, não apenas geração da imagem.

## Build local observado

Foi tentado:

```bash
dotnet build DiscImageStudio.slnx --configuration Release
```

O ambiente da análise não possui `dotnet`, e o shell retornou:

```text
/bin/bash: dotnet: command not found
```

Logo, nenhuma afirmação desta revisão deve ser interpretada como build ou testes reproduzidos nesta máquina. O documento [VALIDATION.md](../VALIDATION.md) registra uma validação anterior em Windows/.NET 10, mas está datado de 28 de outubro de 2026 e referencia commits anteriores ao snapshot analisado.

## Roteiro de validação recomendado

Em uma máquina Windows com o SDK indicado por `global.json`:

```powershell
dotnet restore DiscImageStudio.slnx
dotnet build DiscImageStudio.slnx --configuration Release --no-restore
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release --no-build
dotnet run --project src/DvdImageSolver --configuration Release --no-build -- selftest
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- selftest
```

Depois, validar a UI com `ui-snapshot`. Para a CLI portável, publicar pelo menos um RID e executar `tests/test_cli_package.py` sobre o pacote extraído, como o CI faz.

Testes com gravador real devem ser uma etapa separada, usando mídia descartável. Testes automatizados não provam refletância, estratégia de gravação, resposta do firmware ou visibilidade física.

## Desempenho e memória

As decisões de desempenho são em geral sólidas:

- geração de imagens de disco é sequencial;
- filesystems têm planejamento sem cache integral do conteúdo;
- gravação usa buffer limitado com backpressure;
- composites têm cache por revisão;
- preview obsoleto é descartado;
- BD tem testes de paralelismo/reuse;
- CD/DVD possuem regressões SIMD;
- imagens WPF são congeladas para uso cross-thread.

Os principais pontos de atenção são:

- composites 8.192 × 8.192 em BGRA podem ocupar cerca de 256 MiB antes de buffers/encoders;
- várias versões de composite e preview podem coexistir temporariamente;
- WPF `RenderTargetBitmap` depende de STA e pode pressionar memória nativa;
- geração em streaming precisa manter throughput mínimo até o dispositivo;
- previews de alta resolução precisam verificar cancelamento em loops internos.

Seria útil medir peak working set, tempo por etapa e throughput para presets representativos, especialmente BD-R.

## Segurança e integridade

O aplicativo é local, não instala driver/serviço e não possui telemetria ou tráfego de rede. Os riscos são principalmente de integridade de arquivos e operação de hardware.

Boas práticas observadas:

- abertura de arquivos sem montar comando shell;
- saída não pode ficar dentro da pasta de entrada;
- input/output são comparados em comandos relevantes;
- stores limitam e validam documentos;
- limpeza de cache usa allowlist;
- gravação rejeita mídia inadequada/não vazia;
- source folder é planejado antes da escrita;
- cancelamento e cleanup são testados.

Pontos para endurecimento:

- tratar links/reparse points de forma uniforme em scanners;
- definir política para arquivos que mudam conteúdo sem mudar tamanho;
- hashes opcionais no plano de filesystem;
- análise de dependências/vulnerabilidades no CI;
- limites documentados de quantidade, profundidade e nomes;
- testes de caminhos longos, Unicode e colisões case-insensitive.

## Observabilidade e diagnóstico

O `JobDock` fornece progresso, status, log e telemetria. Para um aplicativo desktop técnico, é uma boa base. Ainda faltam informações estruturadas para suporte.

Um pacote de diagnóstico opcional poderia conter versão/commit, OS/runtime, módulo e estágio, mídia/dispositivo, durações, throughput, stack trace e metadata do sidecar — sem incorporar imagens do usuário por padrão.

Isso é particularmente valioso para bugs que só aparecem em um gravador ou firmware.

## Documentação

A documentação técnica é rica. Os documentos de DVD, Blu-ray, calibração e modelos físicos preservam decisões que normalmente ficariam apenas no código.

Problemas atuais:

- documentos técnicos majoritariamente em chinês, enquanto READMEs têm várias traduções;
- ausência anterior de um índice analítico próprio dentro de `docs/`;
- imagens `tab-*` parecem descrever uma UI anterior;
- [VALIDATION.md](../VALIDATION.md) registra baseline anterior ao commit analisado;
- a ajuda de `UnifiedCommandRunner` chama o output BD de “ISO9660/Joliet”, mas `BluRayIsoGenerator` usa UDF 2.01 quando há filesystem;
- a propriedade `Description` do projeto WPF menciona CD/DVD, mas não Blu-ray.

Os dois últimos itens são inconsistências objetivas e fáceis de corrigir.

## Manutenibilidade do código

Padrões positivos:

- nomes expressivos e comentários de invariantes;
- records para DTOs e parâmetros;
- tipos separados para geometria e metadata;
- `internal` reduzindo superfície pública;
- fallbacks de IO delimitados;
- `CancellationToken` atravessa loops e writers;
- testes acompanham bugs específicos.

Dívidas principais:

- ViewModels multifuncionais grandes;
- API interna baseada em argumentos textuais;
- lógica de mídia duplicada entre Export e Burn;
- estado global com números textuais;
- solver DVD compilado por dois caminhos;
- testes portáveis presos ao Windows;
- code-behind da janela principal como segundo shell.

## Critério de pronto sugerido

Para motores/filesystems: build Release, regressões, artefato determinístico pequeno, cancelamento, sidecar/readback e documentação atualizada.

Para UI: temas claro/escuro, idiomas com strings longas, DPI 100/125/200%, layouts largo/estreito, teclado/Narrator e snapshot revisado.

Para gravação: todos os itens anteriores, fake/session sem hardware, teste físico controlado e registro de gravador, firmware, mídia, velocidade e resultado.

---

[← Código e fluxos](03-code-and-flows.md) · [Índice](README.md) · [Riscos e roadmap →](05-risks-and-roadmap.md)
