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
