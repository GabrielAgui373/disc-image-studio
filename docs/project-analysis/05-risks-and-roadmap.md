# Riscos e roadmap recomendado

[← Qualidade e operação](04-quality-and-operations.md) · [Índice](README.md)

## Matriz de riscos

| Prioridade | Risco | Evidência | Impacto provável |
| --- | --- | --- | --- |
| Alta | Export e Burn usam fronteiras diferentes | Export monta CLI textual; Burn chama tipos concretos. | Divergência entre arquivo validado e conteúdo gravado. |
| Alta | Concentração na camada App | ViewModels de 800–1.700 linhas e `MainWindow.xaml.cs` com ~905. | Regressões cruzadas e alto custo de mudança. |
| Alta | Estado numérico baseado em strings | `DiscParametersState` reúne três mídias e parsing tardio. | Validação duplicada e refactors frágeis. |
| Alta | Validação física limitada por natureza | Resultado depende de mídia, gravador, firmware e velocidade. | Sucesso digital pode não produzir imagem física esperada. |
| Resolvido | Solver DVD com hospedagem dupla | A Task 04 criou `DiscImageStudio.Dvd.Engine` e removeu a referência ao EXE e o glob de fontes. | Um único assembly portátil é usado em todos os targets. |
| Média | Projetos não portáveis | Camadas guardam caminhos absolutos. | Assets ausentes ao mover/compartilhar o projeto. |
| Média | Testes portáveis presos ao Windows | Harness inteiro targeta `net10.0-windows`. | Menor feedback em Linux/macOS e pouca descoberta por IDE. |
| Média | Cancelamento pode deixar saída parcial | A própria UI alerta sobre arquivos incompletos. | Artefato aparentemente válido, mas truncado. |
| Média | Diagnóstico pouco estruturado | Parte dos logs retém apenas `Exception.Message`. | Bugs de hardware difíceis de reproduzir. |
| Baixa | Documentação visual legada | `tab-*` não corresponde à UI `ui-*`. | Onboarding confuso. |
| Baixa | Metadata desatualizada | Help BD diz ISO9660/Joliet; csproj omite Blu-ray. | Informação incorreta em ajuda/pacote. |
| Baixa | Chaves de localização como texto-base | Parte do lookup usa frases chinesas. | Redação pode virar mudança de identidade. |

## Correções imediatas

### 1. Corrigir inconsistências factuais

Alterações pequenas e de baixo risco:

- em `UnifiedCommandRunner`, substituir a descrição ISO9660/Joliet do `bd-generate` por UDF 2.01 quando houver diretório e explicar drawing-only;
- atualizar `DiscImageStudio.App.csproj` para mencionar CD, DVD e single-layer BD-R;
- marcar `docs/images/tab-*` como legado ou mover para `docs/images/legacy/`;
- atualizar data/commit de `VALIDATION.md` após a próxima execução completa.

### 2. Criar validação reprodutível pós-análise

Executar em Windows o roteiro de [Qualidade e operação](04-quality-and-operations.md#roteiro-de-validação-recomendado), registrando SDK/runtime, commit, warnings, duração e artefatos. Isso fecha a única lacuna desta revisão: o ambiente atual não possui o SDK .NET.

### 3. Proteger outputs contra cancelamento

Padronizar geração em arquivo temporário no mesmo volume e rename/replace somente após sucesso. Sidecar e imagem devem ser publicados como uma unidade. Em cancelamento/falha, o temporário deve ter nome inequívoco e ser removido quando seguro.

## Fase 1 — unificar casos de uso de geração

Objetivo: garantir que Export e Burn executem a mesma configuração tipada.

Introduzir requests imutáveis:

```csharp
public abstract record GenerateDiscRequest;
public sealed record GenerateCdRequest(...) : GenerateDiscRequest;
public sealed record GenerateDvdRequest(...) : GenerateDiscRequest;
public sealed record GenerateBluRayRequest(...) : GenerateDiscRequest;
```

E um serviço com destinos diferentes:

```csharp
Task<GenerationPlan> PlanAsync(GenerateDiscRequest request, CancellationToken token);
Task GenerateFileAsync(GenerationPlan plan, string output, IProgress<...>? progress, CancellationToken token);
void GenerateStream(GenerationPlan plan, Stream output, CancellationToken token);
```

A CLI traduz argumentos para request. A GUI cria request diretamente. Export e Burn passam pelo mesmo `GenerationPlan`. O catálogo modular continua como API pública sem carregar sintaxe textual para dentro da aplicação.

Benefícios:

- paridade por construção;
- validação única;
- refatoração segura de opções;
- previews e resumos lendo o mesmo plano;
- testes comparando arquivo e stream byte a byte.

## Fase 2 — decompor o estado e a aplicação

### Estado

Separar o estado validado por família e manter adapters textuais apenas no formulário:

```text
ProjectWorkspaceState
├── ProjectIdentityState
├── DesignState
├── ToneState
├── DeliveryState
└── MediaState
    ├── CdState
    ├── DvdState
    └── BluRayState
```

`ProjectSession` passa a mapear documento ↔ estado tipado. A UI pode usar regras de validação ou view models de campos para preservar edição parcial sem contaminar o domínio.

### ViewModels

Extrair por motivo de mudança:

- `ShellViewModel` → `ProjectLifecycleService`, `JobCoordinator`, `NavigationState`;
- `LivePreviewViewModel` → serviços de preview por mídia;
- `BurnViewModel` → dispositivo/velocidade + builder do plano de conteúdo;
- `CalibrationViewModel` → sessão + solver coordinator;
- `MainWindow.xaml.cs` → comportamento de janela; navegação/settings em serviços.

Não é necessário dividir cada arquivo por tamanho. A meta é que cada classe tenha um único conjunto coerente de invariantes.

## Fase 3 — extrair o motor de DVD (concluída na Task 04)

Estrutura implementada:

```text
src/DiscImageStudio.Dvd.Engine/   net10.0, biblioteca
src/DvdImageSolver/              executável fino + selftest host
src/DiscImageStudio.Dvd/         módulo + streaming/hybrid adapter
```

Encoder, solver e tipos compartilhados foram movidos para a biblioteca e o glob `Compile Include="../DvdImageSolver/**/*.cs"` foi removido. O executável histórico encaminha ao módulo, e ambos usam exatamente o mesmo engine em todos os targets.

Critério de aceite:

- mesmos vetores e hashes de saída;
- self-test histórico preservado;
- CLI publicável em Linux/macOS;
- nenhuma dependência WPF no engine.

## Fase 4 — modernizar a estratégia de testes

Criar pelo menos:

- `DiscImageStudio.Core.Tests` (`net10.0`);
- `DiscImageStudio.Media.Tests` (`net10.0`) para ISO/CD/DVD/BD portáveis;
- `DiscImageStudio.Windows.Tests` (`net10.0-windows`) para WPF/IMAPI;
- manter tools/self-tests científicos onde a execução monolítica é útil.

Adicionar testes de contrato:

- geração para arquivo e stream são idênticas;
- descritor e parser/request não divergem;
- sidecar sempre corresponde ao artefato;
- cancelamento nunca publica o nome final;
- help/metadata refletem o filesystem real.

## Fase 5 — portabilidade de projetos e UX segura

Prioridades:

1. caminhos relativos ao `.discproj` quando o asset estiver abaixo da pasta do projeto;
2. comando “coletar projeto” para copiar assets;
3. tela de relink para assets ausentes;
4. hashes/tamanho para detectar troca silenciosa;
5. undo/redo no Design;
6. relatório exportável de calibração;
7. resumo final de gravação com mídia, capacidade, velocidade e conteúdo.

Um formato empacotado pode vir depois. Caminhos relativos e relink já resolvem boa parte do problema sem introduzir ZIP e migração complexa.

## Fase 6 — observabilidade e validação física

### Diagnóstico

Criar eventos estruturados com job, módulo, fase, parâmetros físicos, hashes, dispositivo, perfil de mídia, velocidade, throughput, buffer, HRESULT/sense data, exceção completa e resultado de cleanup.

O usuário deve escolher exportar o pacote; imagens e caminhos pessoais devem ser minimizados ou redigidos.

### Matriz física

Manter uma tabela versionada por:

- família e marca/lote da mídia;
- gravador e firmware;
- velocidade;
- modo arquivo versus streaming;
- normal/invertido;
- legibilidade do filesystem;
- visibilidade/contraste da imagem;
- fotografia e parâmetros calibrados.

Isso não transforma observação física em garantia universal, mas separa evidência experimental de correção digital.

## Decisões que devem ser preservadas

- módulos separados por mídia;
- Core sem WPF/IMAPI;
- writers sequenciais e planejamento prévio;
- mesma composição de alta qualidade para exportar e gravar;
- separação entre tom e polaridade BD;
- validação visual obrigatória da calibração;
- gravação sem imagem intermediária completa;
- schemas explícitos e escrita atômica;
- ferramentas científicas fora do produto principal;
- warnings como erros e regressões de baixo nível.

## Sequência sugerida

```mermaid
flowchart LR
    A[Correções factuais + build validado] --> B[Requests e planos tipados]
    B --> C[Export e Burn unificados]
    C --> D[Estado e ViewModels decompostos]
    B --> E[Engine DVD extraído]
    D --> F[Testes portáveis e descobríveis]
    E --> F
    F --> G[Projetos portáveis + undo/redo]
    G --> H[Diagnóstico e matriz física]
```

O melhor primeiro investimento estrutural é unificar o caso de uso de geração. Ele reduz risco funcional antes de uma reorganização ampla da UI. A extração do DVD deve ser entregue separadamente para facilitar comparação byte a byte.

## Resultado esperado

Após essas fases, o produto manteria a profundidade técnica atual, mas com um único caminho de configuração para arquivo e gravação, motores consumidos por APIs tipadas, UI menos acoplada à CLI, projetos mais compartilháveis, testes portáveis e diagnóstico melhor para hardware real.

O plano foi decomposto em entregas implementáveis no [backlog detalhado da GUI Linux/Avalonia](tasks/00.index.md), com dependências, critérios de aceite, testes e riscos por task.

---

[← Qualidade e operação](04-quality-and-operations.md) · [Índice](README.md)
