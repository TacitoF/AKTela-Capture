# AKTela Capture v0.5

Versão focada em simplificar a interface e corrigir a escala do cursor remoto.

## Interface

- Janela compacta e mais curta.
- A tela principal mostra apenas: código, modo, qualidade, fonte, áudio e iniciar/encerrar.
- Opções menos usadas (cursor, minimizar e atalho) ficam no menu de três pontos.
- Áudio é uma única opção; internamente o AKTela escolhe a captura recomendada:
  - janela: apenas o áudio do aplicativo;
  - tela: sistema sem a árvore de processos do Discord.
- Status ao vivo consolidado em uma única faixa.

## Cursor

- Automático continua ocultando o cursor no modo Jogo.
- Em outros modos o cursor é enviado separadamente.
- O Capture agora lê tamanho e hotspot do cursor atual do Windows e envia proporções normalizadas.
- A Activity v0.5 usa essas proporções para desenhar o cursor no tamanho relativo correto ao vídeo.

## Build

O workflow `.github/workflows/build-windows.yml` usa .NET 9 e gera `AKTela-Capture-v0.5-Windows-x64`.
