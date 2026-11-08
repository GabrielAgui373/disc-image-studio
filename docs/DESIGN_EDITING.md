# Edição de design e histórico

A camada `DiscImageStudio.Application.Editing` concentra toda alteração persistente de um projeto aberto: camadas, tom, pasta de dados e configurações. Ela torna essas alterações desfazíveis e mantém o estado "modificado" fiel ao conteúdo. Este documento registra as decisões da Task 09.1 e o contrato que canvas, lista de camadas e painéis (Tasks 09.2 a 09.5) devem seguir.

## Peças

| Tipo | Papel |
| --- | --- |
| `DesignEdits` | Funções puras: recebem um `ProjectWorkspace` e devolvem outro. Não guardam estado e lançam `ArgumentException` para entrada inválida. |
| `ProjectHistory` | Pilha linear de passos (antes/depois), com limite de entradas e de memória. |
| `ProjectEditor` | Único caminho para edições desfazíveis: aplica, registra, desfaz, refaz e conduz arrastes. |
| `ProjectLifecycleService.ApplyEdit` | Grava o novo estado e recalcula o dirty pelo conteúdo. |
| `ProjectLifecycleService.PreviewEdit` | Mostra um estado transitório (arraste em andamento) sem tocar no dirty nem no histórico. |
| `ProjectWorkspaceChanged` | Evento publicado a cada mudança de conteúdo; `IsPreview` indica estados transitórios. |

## ADR 1 — Histórico por snapshots imutáveis

**Contexto.** A task pede um histórico com transações de arraste, limite de memória e checkpoint no save. O `ProjectWorkspace` já é imutável, e cada camada é um record imutável.

**Decisão.** Cada passo guarda o workspace de antes e o de depois. Como as edições são funções puras que reaproveitam as camadas não alteradas, dois snapshots seguidos compartilham quase tudo: o custo por passo é a lista de referências e os textos das camadas alteradas. As camadas guardam paths, não pixels; as imagens decodificadas ficam no cache de composição da Task 08.

**Regras.**

- Um passo novo descarta o ramo de refazer.
- O histórico guarda no máximo 200 passos e cerca de 8 MiB estimados; os mais antigos saem primeiro.
- Um arraste (`BeginDrag`) mostra prévias calculadas sempre a partir do estado inicial, sem acumular erro, e registra um único passo no `Commit`. Cancelar ou descartar sem commit restaura o estado inicial.
- Soltar um arraste no mesmo lugar não cria passo.
- Abrir, criar ou fechar um projeto zera o histórico (`SessionId`). Uma mudança feita fora do editor, detectada por `ContentRevision`, também zera, para o desfazer nunca ressuscitar um estado que o usuário não viu.
- Zoom, pan, seleção e hover nunca passam pelo editor.

**Dirty pelo conteúdo.** O lifecycle guarda uma impressão digital do conteúdo no último save ou na abertura. Ela é o JSON do documento, ignorando data de modificação e thumbnail. Depois de cada edição, desfazer ou refazer, o projeto fica dirty se, e somente se, o conteúdo difere dessa impressão. Por isso, desfazer até o estado salvo limpa o projeto, e desfazer depois de salvar suja de novo. A data do save e a thumbnail nunca são revertidas por um desfazer.

**Alternativa descartada.** Comandos com operação inversa (Do/Undo por tipo). Cada edição precisaria de código inverso próprio e de testes de simetria, e os erros de inversão são sutis. Com estado imutável, o snapshot dá o mesmo resultado com menos código e sem risco de divergir.

## ADR 2 — Opacidade por camada no schema 3

**Contexto.** A task pede opacidade por camada. O `.discproj` não tinha o campo, e o JSON rejeita campos desconhecidos (`UnmappedMemberHandling.Disallow`), então qualquer campo novo faz builds anteriores recusarem o arquivo. A Task 07 exige que um projeto salvo no Linux reabra na WPF quando usa só campos conhecidos.

**Decisão.** `ProjectLayer` ganha `opacity` opcional, gravado apenas quando a camada não é totalmente opaca. Nesse caso o arquivo é gravado como schema 3 (`ProjectJsonStore.RequiredSchemaVersion`); sem opacidade, continua schema 2 e idêntico ao de antes. Leitores aceitam os schemas 1 a 3 e validam a opacidade entre 0 e 1.

**Consequências.**

- Projetos que não usam opacidade continuam abrindo em qualquer build.
- Um projeto que usa opacidade só abre em builds com este Core.
- A WPF compila sem mudanças (parâmetro opcional no fim), mas não conhece a opacidade: ela compõe as camadas opacas e, ao salvar, descarta o campo.
- O compositor portátil da Task 08 já pinta a opacidade, então preview, thumbnail e geração no Linux a respeitam.

## ADR 3 — Campos numéricos com buffer e commit

**Contexto.** O backlog alerta que binding bidirecional direto em campos numéricos sobrescreve estado válido durante uma edição parcial ("1." ou "-"), e cada tecla viraria um passo do histórico.

**Decisão.** `NumericFieldViewModel` separa o texto digitado do valor do projeto:

- Digitar só altera o texto.
- Enter ou perda de foco faz o parse, aceitando o separador decimal da cultura atual ou o invariante, valida o intervalo e chama o commit, que vira um único passo pelo editor.
- Um valor inválido mostra a mensagem de intervalo e mantém o texto, sem tocar no projeto.
- Esc restaura o último valor confirmado.
- Um valor igual ao atual (dentro da precisão do campo) não cria passo.
- Enquanto o campo tem foco, atualizações externas (arraste no canvas, desfazer) são guardadas e só aparecem quando o foco sai.

## Como usar

**Edição simples**, por exemplo renomear a partir da lista de camadas:

```csharp
ApplicationResult<EditOutcome> result = editor.Apply(
    HistoryLabels.RenameLayer,
    workspace => DesignEdits.RenameLayer(workspace, layerId, newName),
    [layerId]);
```

**Várias camadas como um passo**, por exemplo um import:

```csharp
IReadOnlyList<string> added = [];
editor.Apply(
    HistoryLabels.AddLayers,
    workspace => DesignEdits.AddLayers(workspace, files.Select(f => new NewLayer(Path.GetFileNameWithoutExtension(f), f)).ToArray(), out added));
// Selecione `added` depois do import.
```

**Arraste** no canvas:

```csharp
using ProjectEditor.DragEdit drag = editor.BeginDrag(HistoryLabels.TransformLayer, [layerId]);
LayerTransform start = LayerTransform.Of(layer);
// A cada movimento do ponteiro, sempre a partir do estado inicial:
drag.Update(workspace => DesignEdits.SetTransform(workspace, layerId, start with { TranslateXMm = start.TranslateXMm + dx }));
// Ao soltar:
drag.Commit();
// Esc ou perda de captura: drag.Cancel() (ou apenas sair do using).
```

**Seleção depois de desfazer/refazer:** `EditOutcome.AffectedLayerIds` diz quais camadas selecionar.

**Ordem das camadas:** índices de `DesignEdits` são do documento, de baixo para cima (índice 0 é pintado primeiro). A lista da interface mostra o topo primeiro e precisa converter: `índiceDocumento = total − 1 − índiceLista`.

**Rótulos:** cada código de `HistoryLabels` tem a entrada `history.label.<código>` no catálogo. Um código novo precisa da entrada correspondente.

## Canvas de design (Task 09.2)

O canvas da página Design é a superfície de manipulação direta. Ele é dividido em peças com uma responsabilidade cada:

| Peça | Papel |
| --- | --- |
| `CanvasViewport` | Mapeamento explícito modelo ↔ tela: milímetros a partir do centro do disco → DIP. O fit mostra o disco de 60 mm com 3 mm de margem; o zoom vai de 0,25× a 12× sobre o fit, e o pan é limitado ao raio do disco. Como a tela trabalha em DIP, a escala de renderização da plataforma cuida do DPI físico. |
| `LayerGeometry` | Cantos da camada girada, hit test por polígono, posição da alça de rotação e diferença de ângulo sem salto na costura de ±180°. |
| `LayerBitmapCache` | Bitmaps de interação de até 1.024 px, gerados a partir do mesmo cache de imagens decodificadas do compositor (Task 08), carregados fora da thread de interface. |
| `DesignCanvasViewModel` | Estado de visualização (zoom, pan, seleção), que nunca suja o projeto, e as edições pelo `ProjectEditor`. |
| `DiscDesignCanvas` | O controle: desenha e traduz ponteiro e teclado em chamadas ao view model. |

**Desenho**, de baixo para cima: fundo, corpo do disco e hub, camadas na ordem do documento (com opacidade), máscara escurecendo tudo fora do anel útil (uma única geometria even-odd), guias tracejadas do anel, cruz no centro e, por fim, a moldura da seleção com quatro alças de escala nos cantos e a alça de rotação. Camadas ocultas aparecem como contorno tracejado discreto; camadas com imagem ausente ou ainda carregando aparecem como contorno tracejado de aviso, com a área estimada.

**Interações:**

| Gesto | Efeito | Histórico |
| --- | --- | --- |
| Clique | Seleciona a camada sob o ponteiro: primeiro as visíveis com imagem, depois placeholders, depois ocultas. Clique no fundo limpa a seleção. | — |
| Arrastar a camada | Move | um passo ao soltar |
| Arrastar um canto | Escala uniforme pela distância ao centro | um passo ao soltar |
| Arrastar a alça superior | Gira; Shift encaixa em múltiplos de 90° | um passo ao soltar |
| Setas / Shift+setas | Nudge de 0,1 mm / 1 mm | toques seguidos viram um passo, confirmado após 700 ms ou antes de desfazer, salvar, fechar ou abrir configurações |
| Delete / Backspace | Remove a camada selecionada | um passo |
| Ctrl+D | Duplica acima da original e seleciona a cópia | um passo |
| Esc | Cancela o arraste em andamento (ou limpa a seleção) | nada |
| Ctrl ou Alt + roda | Zoom ancorado no ponteiro | — |
| Botão do meio | Pan (com zoom acima do fit) | — |
| Clique duplo no fundo | Volta ao fit | — |

Perder o ponteiro no meio de um arraste (outra janela, um diálogo) restaura o estado inicial. Depois de desfazer ou refazer, a camada afetada é selecionada de novo.

## Contexto de projeto na interface

A navegação separa o que é do aplicativo do que é do projeto aberto:

- **Sem projeto:** a barra lateral mostra só Home e Settings. As etapas do projeto não aparecem, e um atalho para elas é recusado com um aviso.
- **Com projeto:** a barra lateral ganha o grupo "PROJECT" (miniatura do disco, nome e ponto de alterações não salvas) com as etapas Design, Preview, Export e Burn. A barra do projeto (desfazer/refazer, configurações, salvar, fechar) aparece só nessas etapas, acima do título da etapa, e o badge do título mostra a família (CD, DVD, BD).
- **Home:** o cartão "Current project" com "Continue in Design" quando há projeto aberto, o hero com Novo/Abrir e os recentes. A escolha de família fica só no diálogo de novo projeto.
- **Settings:** concentra informações do aplicativo e diagnóstico.
