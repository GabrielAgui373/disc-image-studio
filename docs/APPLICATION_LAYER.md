# Camada de aplicação portátil

`DiscImageStudio.Application` contém o estado validado e os casos de uso compartilháveis por WPF, Avalonia e CLI. O assembly tem target `net10.0`, depende somente de `DiscImageStudio.Core` e não conhece controles, dispatchers, bitmaps ou tipos de diálogo.

## Ownership

- `ProjectLifecycleService` é o dono da sessão aberta, caminho, dirty state e navegação conceitual. O host deve serializar suas chamadas.
- `ProjectWorkspace` é um snapshot imutável e tipado. Buffers de texto incompleto pertencem ao adapter da tela; somente valores convertidos chegam ao workspace.
- `CdState`, `DvdState` e `BluRayState` carregam apenas parâmetros relevantes para a respectiva mídia.
- `ProjectDocumentMapper` preserva o schema `.discproj` do Core e faz a fronteira documento ↔ estado.
- `JobCoordinator` protege sua coleção interna e permite jobs compartilhados, mas rejeita um novo job quando ele ou algum job ativo for exclusivo.

## Ports e threading

`IProjectDocumentStore`, `IFileSystem`, `IClock`, `IApplicationPaths`, `IUserInteraction`, `IApplicationEventPublisher` e os ports de plataforma (`IAppPaths`, file picker, clipboard, launcher, dispatcher, prompts, logging, cache e lifetime) são contratos sem dependência de Avalonia. O host Linux implementa os adapters Avalonia/XDG; a biblioteca fornece adapters básicos para relógio, filesystem e `ProjectJsonStore`. Settings e recentes usam stores JSON portáveis com escrita atômica.

Persistência JSON é síncrona e ocorre no thread chamador. Hosts de UI devem chamar lifecycle em um worker quando o arquivo puder ser grande e retornar ao dispatcher somente para atualizar bindings. Eventos são publicados sincronamente; o adapter decide se precisa fazer dispatch. Jobs executam no scheduler escolhido pelo chamador, respeitam `CancellationToken` e não capturam um framework de UI.

## Resultados

Falhas esperadas retornam `ApplicationResult` com `ApplicationErrorCode` estável. A UI traduz o código para texto localizado e pode registrar `Detail`/`Exception`; a camada não retorna toast, `MessageBoxResult` nem strings de botão.
