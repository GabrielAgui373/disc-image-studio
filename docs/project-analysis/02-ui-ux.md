# Telas, UX e sistema visual

[← Arquitetura](01-architecture.md) · [Índice](README.md) · [Código e fluxos →](03-code-and-flows.md)

## Estrutura da experiência

A aplicação atual organiza o trabalho por projeto, não por ferramenta isolada. A Home é o único espaço sem projeto; depois de abrir ou criar um `.discproj`, a barra lateral e a barra de etapas apresentam cinco visões do mesmo estado:

| Espaço | Pergunta que responde |
| --- | --- |
| Design | Qual imagem e quais arquivos irão para o disco? |
| Prévia | Como o desenho será projetado ou lido do artefato gerado? |
| Exportação | Para qual arquivo gravável o projeto será materializado? |
| Gravação | Em qual dispositivo e velocidade o conteúdo será escrito? |
| Calibração | Como ajustar o modelo geométrico ao disco físico observado? |

A distinção é boa. O usuário não precisa entender a estrutura dos módulos para concluir o fluxo, e parâmetros aparecem no local em que têm significado. Geometria pertence ao projeto; tom pertence ao Design; caminho e formato pertencem à Exportação; dispositivo e confirmação pertencem à Gravação; medição pertence à Prévia/Calibração.

## Avaliação tela a tela

### Início

A Home oferece novo projeto, abertura, templates por mídia e projetos recentes. A hierarquia é simples e a densidade é adequada. Os templates tornam parâmetros físicos complexos acessíveis sem começar por um formulário técnico.

Pontos fortes:

- ação primária “Novo projeto” clara;
- templates expõem CD/DVD/BD sem criar três produtos separados;
- recentes combinam thumbnail, família e localização;
- a seção de recentes desaparece quando vazia;
- o projeto já aberto pode ser retomado sem criar outra sessão.

Pontos a melhorar:

- em telas largas, a faixa de ações ocupa bastante espaço vazio;
- o valor do template poderia comunicar melhor “padrão”, “medido” e “contribuído” sem depender apenas de tooltip/ícone;
- a documentação deve usar somente as capturas `ui-*`; as imagens `tab-*` retratam uma navegação antiga e hoje parecem outro produto.

![Tela inicial atual](../images/ui-home.png)

### Novo projeto e configurações do projeto

O diálogo separa escolha de template, nome/local e parâmetros da mídia. Presets reduzem o risco de valores inválidos; salvar a geometria atual como preset fecha bem o ciclo de calibração.

O cuidado necessário é não sobrecarregar o primeiro contato com detalhes físicos. Campos como coeficientes cúbicos de pitch precisam continuar atrás de divulgação progressiva, explicação contextual e defaults confiáveis. A família do disco ser imutável após a criação é uma boa restrição: trocar CD por BD no mesmo documento mudaria quase todas as invariantes.

### Design

É a tela mais forte do produto. O layout de três regiões — camadas, canvas e propriedades/tom — se aproxima de editores gráficos conhecidos sem tentar se tornar um editor genérico.

Recursos bem resolvidos:

- canvas em coordenadas físicas, com disco e anel útil visíveis;
- seleção sincronizada entre lista e canvas;
- mover, escalar, rotacionar, ordenar e ocultar camadas;
- propriedades numéricas em milímetros/graus;
- diretório de arquivos do disco separado das camadas visuais;
- preview tonal no mesmo contexto da composição;
- zoom, pan e ajuste à janela;
- layout estreito que empilha painéis sem esmagar o canvas.

Riscos de usabilidade:

- existem dois tipos de “conteúdo”: imagem desenhada e arquivos montáveis. A separação é correta, mas deve permanecer explicitamente nomeada;
- tom e geometria física são conceitos avançados; tooltips ajudam, porém uma comparação antes/depois ou presets de tom seriam mais descobríveis;
- não há indicação de undo/redo no modelo analisado. Em um editor direto, ausência de histórico torna exclusão, reorder e transformações mais ansiosos;
- caminhos absolutos de imagem significam que uma camada válida visualmente hoje pode ficar ausente ao mover o projeto.

![Tela de design atual](../images/ui-design.png)

### Prévia

A tela distingue projeção rápida do Design e leitura do artefato final. Essa diferença é central: a primeira responde rapidamente; a segunda verifica o que realmente foi codificado. A região amarela que marca filesystem ajuda a explicar por que nem todo o disco é desenho.

Pontos fortes:

- um único inspetor projeta parâmetros de CD/DVD/BD;
- geometria de geração é resumida e editada no lugar proprietário;
- parâmetros avançados de medição/qualidade ficam recolhidos;
- sidecars restauram o contexto do artefato gerado;
- exportação da prévia preserva a resolução escolhida.

Pontos a melhorar:

- “projeção” e “readback” precisam de microcopy muito clara, pois visualmente ambos produzem uma imagem de disco;
- o custo de resoluções altas deveria ser antecipado com estimativa ou classificação de velocidade;
- falhas por sidecar ausente deveriam distinguir “prévia aproximada” de “erro”.

### Exportação

A tela é deliberadamente enxuta: fonte, caminho, opções do formato e ação de geração. Essa contenção é correta. O usuário já tomou decisões visuais no Design e físicas nas configurações.

Pontos fortes:

- a fonte é uma composição, não um caminho de imagem que possa divergir do Design;
- CD alterna coerentemente entre áudio e dados conforme a presença de arquivos;
- DVD/BD expõem volume e posição dos arquivos sem revelar layout de setores;
- a mensagem avisa que exportar não grava o disco;
- o próximo passo leva à Gravação.

Melhorias possíveis:

- mostrar tamanho estimado e espaço livre antes de iniciar;
- exibir no resumo se o volume será ISO9660/Joliet, UDF 2.01 ou trilha CD-DA;
- tornar sidecars visíveis no resultado, não apenas como detalhe interno;
- oferecer “validar artefato após gerar” como ação explícita para jobs longos.

![Tela de exportação atual](../images/ui-export.png)

### Gravação

A tela faz uma escolha importante: grava diretamente o Design ou o padrão de calibração, em vez de pedir ao usuário que selecione novamente um arquivo arbitrário. Isso reduz divergência entre o que foi previsto e o que será gravado.

Elementos adequados:

- origem do conteúdo explícita;
- dispositivo e velocidade separados;
- refresh de dispositivos/mídia;
- confirmação obrigatória de mídia correta;
- aviso de risco de interrupção;
- cancelamento e telemetria no dock global.

A gravação é a operação mais irreversível do produto. Antes da confirmação final, a interface deve sempre expor família, dispositivo, mídia detectada, capacidade, velocidade, tamanho e origem em um resumo único. Para BD-R, o aviso de ausência de remapeamento e impossibilidade provável de retomar deve permanecer destacado.

### Calibração

É o fluxo mais complexo e, corretamente, usa três etapas:

1. gerar o alvo;
2. importar e retificar a fotografia;
3. marcar a curva, resolver e aplicar parâmetros.

O uso de cards numerados e estado de etapa reduz a complexidade. A sobreposição entre pontos observados e curva prevista é essencial para evitar que um resultado numérico seja aceito sem inspeção visual.

O risco de UX aqui não é densidade, mas falsa confiança. O produto deve continuar distinguindo:

- convergência matemática;
- residual aceitável;
- coerência visual de toda a curva;
- validade física em uma gravação real.

Os limites de residual e a confirmação “verifiquei a curva inteira” são boas barreiras. Uma evolução útil seria exportar um relatório de calibração legível, com foto, pontos, curva, resíduos, mídia e versão do algoritmo.

### Configurações e Sobre

As configurações separam aparência/idioma, armazenamento/cache e informações legais. A troca de idioma e tema em runtime preserva trabalho não salvo, um comportamento de produto muito bom. A área Sobre incorpora licenças offline, importante para distribuição GPL e componentes de terceiros.

## Sistema visual

O design system está centralizado em:

- `Tokens.xaml`: cores, brushes, alturas, espaços, raios, tipografia e largura de painéis;
- `Palette.Light.xaml`: overrides do tema claro;
- `Controls.xaml`: estilos e templates;
- controles compostos como `PageHeader`, `StageBar`, `SectionCard`, `SourceChip`, `PathField`, `TonePanel`, `JobDock` e `ToastHost`;
- `DiscTheme`/`DiscRenderCore`: desenho consistente dos discos em controles customizados.

Essa base é mais madura que o comum em aplicações WPF pequenas. O uso de `DynamicResource` permite troca de tema sem reconstruir janelas. O código também contém regressões específicas para evitar que estilos derivados sejam declarados antes do estilo implícito e caiam silenciosamente no visual nativo do Windows.

## Responsividade e navegação

Design e Calibração alteram layout por largura, não apenas reduzem colunas. Essa é a abordagem correta para superfícies densas. A barra lateral colapsa para ícones e as páginas possuem scroll independente onde necessário.

A navegação tem duas representações do mesmo workflow: sidebar e `StageBar`. Isso aumenta a orientação, mas exige uma única fonte de verdade. Hoje o `MainWindow.xaml.cs` sincroniza páginas, barras e estado; funciona, porém essa lógica poderia migrar para um coordenador de navegação testável.

## MVVM e code-behind

O projeto usa CommunityToolkit.Mvvm, propriedades observáveis e comandos. O code-behind é justificável nos controles gráficos e em layout responsivo. Entretanto, `MainWindow.xaml.cs` possui cerca de 905 linhas e trata também de sessão, snapshots, idioma, animações, settings, barra lateral e navegação.

A fronteira recomendada é:

- code-behind: foco, eventos de janela, hit testing, visual states e adaptação de layout;
- ViewModel/serviço: navegação, sessão, decisão de negócio, comandos e lifecycle de jobs;
- controle customizado: interação gráfica localizada.

## Localização e acessibilidade

O catálogo possui 749 chaves completas em chinês simplificado/tradicional, inglês, japonês, turco, espanhol e tcheco. A troca em runtime e os testes de relocalização são pontos fortes.

A escolha de frases chinesas como algumas chaves de lookup torna o catálogo legível para o autor original, mas aumenta o custo de manutenção internacional: mudar a redação-base pode parecer uma alteração de chave. IDs semânticos estáveis seriam melhores a longo prazo, embora uma migração total não seja urgente.

Aspectos positivos de acessibilidade observados:

- labels e tooltips frequentes;
- `AutomationProperties.Name` em controles relevantes;
- contraste por tokens e tema claro/escuro;
- navegação por teclado e atalhos em vários fluxos;
- informação de estado acompanhando cor em cards e progresso.

Pontos que merecem uma auditoria dedicada:

- ordem de tabulação nas telas densas;
- leitura por Narrator dos canvases customizados;
- foco após abrir/fechar modais e após navegação;
- tamanho do alvo de ícones compactos;
- contraste de texto muted e overlays sobre imagens;
- não depender apenas de vermelho/laranja na curva de calibração.

## Recomendações de UX prioritárias

1. Adicionar undo/redo ao Design antes de ampliar ferramentas de edição.
2. Tornar o resumo pré-gravação completo e impossível de ignorar.
3. Expor o tipo de filesystem/artefato na Exportação e no resultado.
4. Marcar ou remover capturas `tab-*` legadas da documentação.
5. Criar um relatório exportável de calibração.
6. Fazer uma auditoria de Narrator/teclado nos canvases customizados.
7. Melhorar a portabilidade de assets do projeto e a recuperação de imagens ausentes.

---

[← Arquitetura](01-architecture.md) · [Índice](README.md) · [Código e fluxos →](03-code-and-flows.md)
