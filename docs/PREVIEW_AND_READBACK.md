# Prévia e readback

Contrato e decisões da prévia portátil (Task 10). A matriz dos seis caminhos herdados da WPF está em [10.preview-and-readback.md](project-analysis/tasks/10.preview-and-readback.md#contrato-herdado-da-wpf).

## Onde cada peça mora

| Camada | Projeto | Conteúdo |
| --- | --- | --- |
| Contratos | `DiscImageStudio.Application/Preview` | `PreviewRequest`, `PreviewResult`, códigos de warning e falha, `IPreviewService`, `PreviewCoordinator`, `PreviewSourceCache`, `PreviewCostEstimator` e `PreviewOverlayPainter`. Não conhece os motores. |
| Serviços por mídia | `DiscImageStudio.Preview` (novo, `net10.0`) | Projeção de CD, DVD e Blu-ray, mais o renderizador de Blu-ray portado da WPF. Referencia Application e os motores, nunca Avalonia. |
| Host | `DiscImageStudio.App.Avalonia` | Registra `PreviewSources` e `Previews` em `HostServices` e libera o cache ao encerrar. A página Prévia chega na 10.4. |

## Decisões (ADRs curtos)

### ADR 1 — Buffer em memória, não PNG temporário

- **Contexto:** os motores gravavam o preview em PNG, e a WPF recarregava o arquivo para exibir, sobrepor o overlay e exportar.
- **Decisão:** o resultado é um buffer BGRA opaco de `Size × Size` (`PreviewResult.Pixels`). Os motores ganharam pontos de entrada que devolvem o buffer: `CdTrackGenerator.RenderWarpPixels`, `CdDataImageGenerator.RenderWarpPixels` e `CalibrationPreviewRenderer.RenderPixels`. Os métodos antigos que gravam PNG passaram a chamá-los.
- **Consequência:** exibir e exportar usam os mesmos bytes, e o PNG exportado é exatamente o exibido. A verificação da 10.2 confirmou que os buffers são idênticos, byte a byte, aos PNGs gravados pelos caminhos antigos (CD áudio, CD dados e DVD).

### ADR 2 — A projeção lê a fonte da geração, com cache por conteúdo

- **Contexto:** na WPF, a projeção lia um arquivo de imagem escolhido pelo usuário. Na Avalonia, a entrada é o próprio projeto.
- **Decisão:** a projeção lê a composição de 8192 px que a geração consome (`ProjectCompositionService.PrepareSourceImage`). O `PreviewSourceCache` guarda um arquivo por conteúdo de design, identificado por `ProjectCompositionService.ContentKey` (transformações e tamanho/data de cada imagem) e pelo tamanho. Cada serviço segura um *lease* enquanto o motor lê o arquivo, e o arquivo substituído só é apagado quando ninguém mais o usa.
- **Rascunho:** `PreviewRequest.SourceSize` aceita de 1024 a 8192 px. Abaixo de 8192 o resultado traz o warning `DraftSource`, porque deixa de bater pixel a pixel com a geração; no Blu-ray, por exemplo, o dither do tom é aplicado na resolução da fonte.
- **Consequência:** mudar resolução, modo ou geometria nunca recompõe; só uma mudança de design recompõe, uma vez. Em 8192 px, a fonte custa ~256 MiB para compor e mais ~256 MiB para o motor decodificar. Em máquinas com pouca memória, a 10.4 deve oferecer o rascunho em 4096 px (~64 MiB).

### ADR 3 — Dither portátil do antes/depois do tom

- **Contexto:** a pendência da Task 09 supunha extrair `GrayscaleDither`/`FourLevelDither` do `DiscImageStudio.Imaging`, que é `net10.0-windows`.
- **Decisão:** não extrair. A matemática do tom que a geração usa já é portátil: `ToneProfile.AdjustedGray`, `ToneProfile.UseWhite` e `ToneDither` no Core, e `CdToneMapping` (4 níveis) no CD. Os três motores de projeção aplicam o `ToneProfile` por esse caminho, e o antes/depois da 10.4 deve usar as mesmas funções sobre a composição de 2048 px.
- **Consequência:** o "depois" mostra o tom que a geração aplica de fato, não uma aproximação do dither da WPF.

### ADR 4 — Blu-ray portado, overlay como dado

- **Blu-ray:** `BluRayPreviewRenderer.RenderCalibration` foi copiado para `BluRayProjectionRenderer` sem `BitmapSource`. A cópia da WPF continua intacta, porque o projeto WPF não compila no Linux. Fazer a WPF delegar ao renderizador portátil fica como pendência a conferir no Windows. O readback do Blu-ray entra na 10.3.
- **Overlay:** o resultado descreve os overlays como anéis em milímetros (`PreviewRingOverlay`: anel útil e sistema de arquivos) e o mapeamento pixel → mm (`PreviewFrame`), porque cada motor centraliza o raster de um jeito. O host desenha os anéis ou chama `PreviewOverlayPainter.Paint`, que aplica sobre uma cópia a cor da legenda da WPF (#FACC15) no sistema de arquivos e um contorno no anel útil.

## Uso

```csharp
PreviewRequest request = new(
    workspace,               // snapshot imutável; edições posteriores não vazam
    projectFilePath,
    PreviewMode.Projection,
    OutputSize: PreviewDefaults.OutputSize(workspace.Media.Family),
    MeasuredGeometry: null,  // null: lê com a geometria gerada (warning MeasuredGeometryAssumed)
    Revision: revision);

PreviewResult? result = await services.Previews.RenderLatestAsync("preview-page", request);
if (result is null)
{
    return; // superado por um pedido mais novo no mesmo canal
}
```

- Falhas viram `PreviewFailedException` com `PreviewFailureCode`: `Unsupported`, `NothingToProject`, `InvalidParameters`, `DataLayoutFailed`, `ArtifactMissing` ou `ArtifactUnreadable`.
- Um pedido novo no mesmo canal cancela o anterior, que devolve `null`. `Cancel(channel)` e `CancelAll()` existem para troca de página e fechamento de projeto.
- `result.Parameters` lista o que o motor usou de fato (geometria gerada e lida, tom, amostras por setor, faixa de desenho, tamanho da fonte). É a base dos "parâmetros efetivos" da 10.4 e do diagnóstico da 10.5.
- Padrões herdados da WPF: saída de 1600 px (CD), 1200 px (DVD) e 2048 px (BD); 16 amostras por setor (CD) e 4 (DVD). Limites de 256 a 8192 px.

## Custo estimado

`PreviewCostEstimator.Estimate(família, modo, tamanho, setores, amostras, fonte)` devolve amostras, memória de trabalho, memória da fonte e duração. As vazões foram medidas na 10.2, com fonte de 4096 px, tamanhos padrão e a máquina sob pressão de memória:

| Mídia | Amostras | Medido | Estimado | Pico estimado |
| --- | --- | --- | --- | --- |
| CD 80 min | 5,8 M | 1,3–1,6 s | 1,6 s | 103 MiB |
| DVD-5 | 9,2 M | 0,8 s | 0,9 s | 80 MiB |
| BD 25 GB | 4,2 M pixels | 1,6 s | 1,6 s | 80 MiB |

A primeira prévia de um design soma a composição da fonte, que levou ~13 s em 8192 px nessa máquina.
