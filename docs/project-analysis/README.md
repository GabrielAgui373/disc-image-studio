# Análise geral do Disc Image Studio

> Revisão técnica e de produto baseada no repositório em 4 de novembro de 2026, no commit `fb0a598`.

Esta seção apresenta uma leitura transversal do projeto: arquitetura, telas, código, formatos, fluxos de geração e gravação, qualidade, riscos e próximos passos. Ela complementa a documentação técnica existente, que continua sendo a fonte detalhada para os algoritmos de cada mídia.

## Resumo executivo

O Disc Image Studio já é um produto tecnicamente maduro, não apenas um experimento visual. O repositório contém uma aplicação WPF completa, uma CLI multiplataforma, motores distintos para CD, DVD e BD-R, escritores ISO9660/Joliet e UDF 2.01, gravação óptica por IMAPI/MMC, calibração fotográfica e uma suíte ampla de regressão.

A principal qualidade arquitetural é a separação dos algoritmos por família de mídia. O projeto evita uma abstração física artificial entre CD, DVD e Blu-ray e compartilha apenas conceitos estáveis: catálogo de módulos, ciclo de jobs, perfil tonal, projeto, presets e progresso. A principal fragilidade está um nível acima: a aplicação WPF concentra muita coordenação em poucos ViewModels e atravessa o contrato modular por meio de listas de argumentos textuais, como se a própria GUI fosse uma CLI.

Em termos de produto, a jornada atual é clara:

```text
Início → Design → Prévia → Exportação → Gravação
                    ↘              ↗
                      Calibração
```

O sistema visual é coeso, possui temas claro/escuro, componentes reutilizáveis, layouts responsivos e sete idiomas. O fluxo de Design é o ponto mais forte da interface. Os maiores riscos de manutenção são o tamanho da camada `App`, o estado compartilhado baseado em strings, a hospedagem duplicada do solver de DVD e a diferença entre a qualidade dos testes algorítmicos e a validação física, que necessariamente depende de mídia, gravador e firmware reais.

## Retrato do repositório

| Indicador | Valor observado |
| --- | ---: |
| Arquivos versionados | 426 |
| Arquivos C# | 290 |
| Arquivos XAML | 31 |
| Linhas C# | 62.953 |
| Linhas XAML | 4.929 |
| Linhas C#/XAML somadas | 67.882 |
| Projetos na solução | 15 |
| Linhas em `DiscImageStudio.App` | 23.961 |
| Linhas no harness de testes | 17.886 |
| Catálogo de localização | 749 chaves em 7 idiomas |

Os números são uma fotografia do commit analisado e incluem ferramentas e testes onde indicado. A camada WPF representa cerca de um terço de todo o C#/XAML, o que ajuda a explicar por que a maior parte da dívida de manutenção está na orquestração da aplicação, não nos contratos do Core.

## Avaliação por dimensão

| Dimensão | Avaliação | Leitura curta |
| --- | --- | --- |
| Arquitetura de domínio | Forte | Limites por mídia são explícitos e tecnicamente corretos. |
| Arquitetura da aplicação | Moderada | MVVM funciona, mas há ViewModels e estado compartilhado grandes. |
| Pipeline de geração | Forte | Fluxos sequenciais, canceláveis e com planejamento prévio de layout. |
| Gravação óptica | Forte, mas sensível | Boa separação e buffering; o ambiente físico continua sendo variável. |
| UI e sistema visual | Forte | Hierarquia clara, componentes próprios, temas e boa jornada principal. |
| Persistência | Forte | JSON estrito, schemas, validação e escrita atômica. |
| Testes automatizados | Forte em regressão | Cobertura técnica ampla, mas harness próprio e alvo Windows reduzem descoberta/portabilidade. |
| Portabilidade | Mista | CLI e motores são amplamente portáveis; GUI e gravação são Windows. |
| Documentação | Rica, porém fragmentada | Muito conteúdo profundo, majoritariamente em chinês e com algumas capturas legadas. |

## Índice da análise

1. [Arquitetura e limites](01-architecture.md) — projetos, dependências, contratos, composição e decisões estruturais.
2. [Telas, UX e sistema visual](02-ui-ux.md) — jornada, avaliação tela a tela, responsividade, acessibilidade e localização.
3. [Código e fluxos de dados](03-code-and-flows.md) — projeto, composição, geração, prévia, gravação, calibração e persistência.
4. [Qualidade, testes e operação](04-quality-and-operations.md) — testes, CI, release, desempenho, segurança e observabilidade.
5. [Riscos e roadmap recomendado](05-risks-and-roadmap.md) — riscos priorizados e plano incremental de evolução.

## Escopo e método

A revisão combinou:

- inventário de todos os projetos, fontes C#, XAML, testes, ferramentas e documentação;
- leitura dos contratos centrais e dos fluxos de CD, DVD, BD-R, ISO/UDF e gravação;
- leitura dos ViewModels, serviços, controles, temas e persistência;
- inspeção das capturas versionadas de Início, Design, Exportação e telas legadas;
- inspeção dos workflows de CI, empacotamento e release;
- busca por concentração de código, exceções, cancelamento, concorrência e acoplamentos;
- tentativa de build em Release.

O build não pôde ser reproduzido neste ambiente porque o executável `dotnet` não está instalado. O repositório registra uma execução completa anterior em [VALIDATION.md](../VALIDATION.md), mas esta análise não a apresenta como validação própria. Todas as conclusões de compilabilidade nesta seção são, portanto, estruturais; os comandos recomendados para uma máquina preparada estão em [Qualidade e operação](04-quality-and-operations.md#roteiro-de-validação-recomendado).

## Documentos técnicos relacionados

- [Arquitetura oficial](../ARCHITECTURE.md)
- [Motor de DVD](../DVD_ENGINE.md)
- [Compatibilidade de áudio CD](../CD_AUDIO_COMPATIBILITY.md)
- [Adição e modelo de Blu-ray](../ADDING_BLURAY.md)
- [Calibração assistida](../CALIBRATION.md)
- [Auditoria dos modelos físicos](../PHYSICAL_MODEL_AUDIT.md)
- [Validação registrada](../VALIDATION.md)
- [Release e empacotamento](../RELEASING.md)

---

Próximo: [Arquitetura e limites →](01-architecture.md)
