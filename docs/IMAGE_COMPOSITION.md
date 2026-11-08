# Composição de imagem portátil

`DiscImageStudio.Rendering` compõe as camadas de um projeto na imagem quadrada que thumbnail, preview e geração consomem. É uma biblioteca `net10.0` sem WPF, Avalonia ou dependência nativa: entrada e saída são buffers `BgraRaster` do `DiscImageStudio.ImageIO`, e só a GUI converte o resultado para um bitmap de tela.

Este documento registra a decisão técnica da Task 08.1 e o contrato que o motor da Task 08.2 implementa.

## Decisão (ADR)

**Contexto.** O compositor da WPF (`DesignComposer`) usa `DrawingVisual` e `RenderTargetBitmap`, exige thread STA e não roda no Linux. A Task 08 pedia um spike curto entre três caminhos: SkiaSharp explícito, composição gerenciada sobre buffers BGRA, e `ImageIO` com rasterizador próprio. Os dois últimos são o mesmo caminho na prática: o `ImageIO` já decodifica e grava os buffers, faltando só o rasterizador.

**Decisão.** Composição gerenciada por mapeamento inverso sobre buffers BGRA, com decode e PNG pelo `DiscImageStudio.ImageIO` (StbImageSharp/StbImageWriteSharp no Linux).

**Spike.** Mesmo cenário nos dois motores, cada execução em processo próprio, Release, máquina de 12 núcleos: uma camada de 4.096 × 3.072 sem rotação e uma de 2.048 × 2.048 com alpha, rotação de 17° e escala 0,42, compostas num canvas de raio 58 mm.

| Motor | 2.048 px | 8.192 px | Pico de working set (8K) | Repetição |
| --- | ---: | ---: | ---: | --- |
| Gerenciado (bilinear, premultiplicado, `Parallel.For`) | 103 ms | ~600 ms | 286 MiB | hash idêntico |
| SkiaSharp 3.119.4 (`SKFilterMode.Linear`) | 320 ms | ~2,5 s | 615 MiB | hash idêntico |

Entre os dois resultados, a diferença média foi de 0,023 por canal, a máxima de 3, e nenhum canal passou de 2.

**Motivos.**

- **Determinismo entre máquinas:** a aritmética gerenciada em `double` dá o mesmo resultado em x64 e arm64. O Skia escolhe caminhos SIMD por CPU e por versão nativa, então o hash só se repete na mesma máquina.
- **Memória:** o pico em 8K é praticamente o buffer de saída (256 MiB) mais as camadas. O Skia mantém a superfície nativa e a cópia gerenciada ao mesmo tempo, quase dobrando o pico.
- **Dependência:** nenhum binário nativo novo. O SkiaSharp chega hoje de forma transitiva pelo Avalonia, e o próprio backlog proíbe tratá-lo como contrato sem fixar a versão; adotá-lo amarraria o motor à versão do Skia da GUI.
- **Cancelamento:** o loop é nosso, então dá para cancelar por faixa de linhas. No Skia, um `DrawImage` de 8K não pode ser interrompido.
- **Qualidade:** a interpolação bilinear com pirâmide de redução (abaixo) cobre o que a WPF entrega com `RenderTargetBitmap`, que usa interpolação linear sem mipmaps.

**Consequências.** A qualidade de interpolação é responsabilidade do projeto. O motor precisa da pirâmide de redução para não serrilhar thumbnails, e de cobertura de borda própria para camadas rotacionadas. Uma interpolação bicúbica pode ser acrescentada depois sem mudar o contrato de geometria.

## Contrato de geometria

O contrato reproduz o `DesignComposer` da WPF:

- **Canvas.** Quadrado de `N × N` px, fundo transparente, centro do disco em `(N/2, N/2)` e lado físico `2 × canvasOuterRadiusMm`. A escala é `pixelsPerMm = N / (2 × canvasOuterRadiusMm)`. Para CD, `canvasOuterRadiusMm` é o raio fixo do esquema (`CdDiscParameters.StandardImageOuterRadiusMm`); para DVD e BD, é o raio externo gerado.
- **Coordenadas.** O pixel `(x, y)` cobre `[x, x+1) × [y, y+1)` e é amostrado no centro `(x + 0,5, y + 0,5)`. O eixo x cresce para a direita e o y para baixo.
- **Tamanho físico da camada.** Com escala 1, a maior dimensão da imagem ocupa `2 × contentOuterRadiusMm`, preservando a proporção. Em pixels, o tamanho é `baseMm × scale × pixelsPerMm`.
- **Transformação.** Rotação em graus no sentido horário da tela, em torno do centro da camada, seguida de translação em mm a partir do centro do disco (x à direita, y para baixo). O retângulo da camada mapeia a imagem inteira; o pixel de origem `i` tem centro em `i + 0,5`.
- **Ordem.** Camadas pintadas de baixo para cima, na ordem de `DesignState.Layers`. Camadas invisíveis, ou com tamanho em pixels menor ou igual a zero, não são pintadas.
- **Bordas.** A cobertura cai linearmente ao longo de 1 px de cada lado da borda da camada, o que suaviza bordas rotacionadas como na WPF.
- **Opacidade.** O modelo de cena aceita um multiplicador entre 0 e 1, com padrão 1. O `.discproj` ainda não persiste opacidade; persisti-la exige nova versão de schema.

## Contrato de pixel

- **Formato.** A entrada é `BgraRaster` com alpha straight, a ordem de bytes que o `ImageIO` devolve. Internamente cada amostra é premultiplicada e composta com *source-over* em `double`; o acumulador por pixel é BGRA premultiplicado de 8 bits. A saída é `BgraRaster` com alpha straight, que é o que o PNG grava.
- **Arredondamento.** Toda quantização para byte usa `MidpointRounding.AwayFromZero`, e a conversão de premultiplicado para straight usa `round(c × 255 / a)`.
- **Interpolação.** Bilinear nos quatro vizinhos, com coordenadas presas à borda da imagem.
- **Redução.** Quando a camada é desenhada em menos da metade da resolução de origem, a amostra vem de um nível da pirâmide gerado por média 2 × 2 em alpha premultiplicado. O nível escolhido é o menor que ainda tem pelo menos o tamanho de destino. Isso evita o serrilhado de uma imagem de 4K reduzida para a thumbnail de 256 px.

## Limites e memória

| Limite | Valor | Motivo |
| --- | ---: | --- |
| Lado do canvas | 1 a 8.192 px | 8.192 é `RingImageQuality.GenerationSize` |
| Lado decodificado da camada | 4.096 px | mesmo teto da WPF (`DesignComposer.MaxDecodeWidth`); acima disso a imagem é reduzida no carregamento |
| Pixels de origem antes da redução | 64 Mpx | limita o buffer transitório do decode a 256 MiB |
| Tamanho do arquivo de origem | 256 MiB | rejeitado antes de decodificar |

Medido na Task 08.5: composição de 8K com pico de 423 MiB; preparação da imagem de geração (composição de 8K mais PNG) com pico de 690 MiB, porque o StbImageWriteSharp filtra e comprime a imagem inteira em memória (≈270 MiB e ≈5,6 s). Hosts devem reservar 768 MiB para preparar uma imagem de geração.

O pico estimado de uma composição é `N² × 4` bytes (saída) mais, por camada carregada, `largura × altura × 4 × 4/3` (imagem e pirâmide). Em 8K, com duas camadas de 4.096 × 4.096, isso dá cerca de 256 MiB + 2 × 85 MiB ≈ 430 MiB. O motor nunca aloca um segundo buffer do tamanho da saída: a conversão para alpha straight é feita no próprio buffer.

## API

- `CompositionScene` e `CompositionLayer`: modelo imutável e validado da cena (raios, camadas, transformações, visibilidade e opacidade).
- `ImageComposer.Compose(scene, size, images, cancellationToken)`: devolve `CompositionResult` com o `BgraRaster` straight e a lista de `CompositionIssue`. Uma camada ausente, ilegível ou acima dos limites é ignorada e reportada, sem falhar a cena.
- `ILayerImageSource`: de onde vêm as imagens decodificadas. `FileLayerImageSource` lê do disco a cada chamada; o cache por revisão da Task 08.3 entra por essa interface.
- `LayerImage`: imagem premultiplicada, já reduzida ao limite de lado, com a pirâmide construída sob demanda e compartilhável entre composições concorrentes.
- `ImageComposer.SavePng` grava pelo `RasterCodec`. A gravação transacional para a geração é da Task 08.5.

**Medição do motor**, na mesma máquina do spike, com uma cena de cinco camadas (foto 1.600 × 1.200, camada rotacionada com alpha, uma invisível, uma com 50% de opacidade e uma ausente) lidas de PNG:

| Canvas | Tempo, incluindo decode | Pico de working set |
| ---: | ---: | ---: |
| 2.048 px | ~260 ms | 81 MiB |
| 8.192 px | ~840 ms | 321 MiB |

## Uso nas próximas tasks

**Exportação (Task 11) e Gravação (Task 13)** obtêm request e imagem prontos pelo preparador do host:

```csharp
ProjectGenerationPreparer preparer = hostServices.Generation;
using PreparedGeneration prepared = await preparer.PrepareAsync(
    projects.Current!,
    projects.ProjectFilePath,
    outputPath: "/home/user/disc.wav",
    cancellationToken: token);

if (prepared.Source.IsEmpty)
{
    // Nenhuma camada pôde ser pintada: avise antes de gerar um disco em branco.
}

GenerationPlan plan = await generation.PlanAsync(prepared.Request, token);
await generation.GenerateFileAsync(plan, outputPath, progress, token);
// Ao sair do using, a imagem temporária de 8K é apagada.
```

**Telas que recompõem com frequência** usam `ProjectCompositionService.ComposeLatestAsync` com um canal próprio: um pedido novo cancela o anterior, que devolve `null`. Para mostrar o resultado, use `CompositionBitmap.ToBitmap`, sempre fora da thread de interface. Para o tamanho físico de uma camada (por exemplo, para desenhar alças), use `ImageComposer.BaseSizeMm`.

## Cancelamento

O token é verificado antes e depois do decode de cada camada, entre os níveis da pirâmide e em cada faixa de linhas durante a pintura (`ParallelOptions.CancellationToken`). Um cancelamento lança `OperationCanceledException` e nunca devolve uma composição parcial.

## Cenas de referência

Nenhuma golden da WPF está versionada, e gerá-las exige Windows. Ficam definidas aqui as cenas que, quando houver acesso a Windows, devem ser congeladas como PNG a partir do `DesignComposer`, em 256 e 2.048 px:

1. Uma camada opaca em paisagem, escala 1, sem rotação: valida o tamanho físico e o centro.
2. Uma camada em retrato com translação (+10 mm, −6 mm): valida os eixos.
3. Uma camada rotacionada em 30° com escala 0,5: valida a rotação em torno do centro e as bordas suavizadas.
4. Duas camadas sobrepostas, a de cima com alpha parcial: valida a ordem e o *source-over*.
5. Uma camada invisível sobre uma visível: a invisível não aparece.
6. Uma imagem de 8.000 px composta em 256 px: valida a redução por pirâmide.

**Tolerância proposta**, a confirmar quando as goldens existirem:

- **Geometria:** centro do disco e retângulo de cada camada exatos dentro de ±1 px.
- **Imagem:** diferença absoluta média ≤ 1,0 por canal; máximo ≤ 8 apenas nas bordas das camadas, onde a suavização da WPF e a nossa diferem.
