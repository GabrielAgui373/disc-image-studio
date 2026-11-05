# Pipeline de geração tipado

`DiscImageStudio.Generation` é a fronteira portátil usada para planejar e gerar artefatos de CD, DVD e Blu-ray. A CLI converte texto nessa API; as interfaces WPF e Avalonia constroem requests tipados diretamente.

## Dependências

```text
CLI ───────────────┐
WPF Export/Burn ───┼──> DiscImageStudio.Generation
Avalonia ──────────┘          │
                              ├──> Core / ImageIO
                              └──> Cd / Dvd / BluRay
```

O projeto não referencia Avalonia, WPF, IMAPI ou tipos de controles. `DiscImageStudio.Application` também permanece independente dos motores: cada host compõe os casos de uso e a geração sem transformar a camada de aplicação em um agregador de infraestrutura.

## Fluxo

1. O adapter cria `GenerateCdRequest`, `GenerateDvdRequest` ou `GenerateBluRayRequest`.
2. `PlanAsync` valida imagem, geometria, tom, diretório, capacidade e combinação de opções.
3. O resultado `GenerationPlan` congela filesystem, faixa de desenho, tamanho, arquivos, sidecars, capabilities e detalhes físicos da mídia.
4. `GenerateFileAsync` e `GenerateStreamAsync` consomem exatamente o mesmo plano.
5. Progresso e cancelamento atravessam os loops dos motores.

O destino em arquivo escreve primeiro um arquivo parcial no diretório final e somente publica o nome solicitado após a geração e o flush terminarem. A política completa de integridade e recuperação continua na Task 11.

## Contratos públicos

- `GenerateDiscRequest`: base imutável, sem argumentos de linha de comando ou tipos de UI.
- Requests específicos: preservam diferenças de CD-DA/Mode 1, DVD e UDF 2.01.
- `GenerationPlan`: visão comum e detalhes tipados (`CdGenerationPlanDetails`, `DvdGenerationPlanDetails` e `BluRayGenerationPlanDetails`).
- `GenerationValidationException`: carrega `GenerationValidationError` com `Code`, `Field` e parâmetros localizáveis.
- `IDiscGenerationService`: planejamento e geração para arquivo ou stream.
- `TypedGenerationCommandLine`: único adapter de texto para os comandos de geração da CLI.

`IOpticalDiscModule` continua sendo uma fronteira pública para comandos existentes e operações de prévia/readback. A migração da renderização de prévia para contratos próprios pertence à Task 10; ela não deve reintroduzir argumentos textuais no pipeline de geração.

## Filesystems e formatos

| Request | Modo | Filesystem do plano | Extensão |
| --- | --- | --- | --- |
| CD | áudio raw | CD-DA | `.raw` |
| CD | áudio WAV | CD-DA | `.wav` |
| CD | dados | ISO9660/Joliet Mode 1 | `.iso` |
| DVD | somente desenho | nenhum | `.iso` |
| DVD | híbrido | ISO9660/Joliet | `.iso` |
| Blu-ray | somente desenho | nenhum | `.iso` |
| Blu-ray | dados | UDF 2.01 | `.iso` |

Byte order de áudio CD, interleave, CUE, posição de arquivos e inversão 3T/6T são opções explícitas do request. A gravação WPF usa o mesmo `GenerationPlan` da exportação e apenas adapta o stream ao `OpticalBurnRequest`.

## Threading e progresso

Planejamento e escrita são executados fora da thread chamadora. Callbacks de `IProgress<GenerationProgress>` podem chegar da thread de trabalho; adapters de UI devem fazer marshaling quando necessário. Os valores possuem total em bytes, nunca devem regredir e terminam com o estágio `complete`.

O token é observado antes do planejamento, durante leitura/conversão da imagem e nos loops sequenciais de cada motor. Cancelamento de arquivo remove o parcial; cancelamento de stream deixa o lifecycle do stream com o chamador.

## Testes de compatibilidade

`TypedGenerationPipelineTests` fixa hashes SHA-256 para cinco vetores pequenos:

- CD áudio;
- CD Mode 1 com ISO9660/Joliet;
- DVD somente desenho;
- Blu-ray somente desenho;
- Blu-ray com UDF 2.01.

Cada vetor compara saída em arquivo e `MemoryStream`. A suíte também cobre parsing CLI versus construção direta, planos/filesystems, códigos de validação, paths inválidos, progresso monotônico e cancelamento de planejamento/escrita.

