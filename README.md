# AKTela Capture v0.4

Versão focada em usabilidade, baixa latência e áudio correto para uso junto ao Discord.

## Novidades

- Perfis: Jogo, Filme, Leve e Personalizado.
- Qualidades: 720p30, 720p60, 1080p30 e 1080p60.
- Fonte: tela inteira ou janela/jogo específico.
- Cursor separado do vídeo: Automático, Mostrar ou Ocultar. No perfil Jogo, Automático oculta o cursor.
- Áudio por aplicativo usando WASAPI process loopback.
  - Janela: captura somente o áudio do aplicativo selecionado.
  - Tela: pode capturar todo o sistema excluindo a árvore de processos do Discord.
  - Isso evita retransmitir para os espectadores as vozes da call do Discord.
- Indicador de latência do relay.
- Preserva configurações entre execuções.
- Atalho global Ctrl + Shift + S para iniciar/encerrar.
- Minimização automática para a bandeja.
- Encoder por hardware continua priorizado.

## Build

O workflow `.github/workflows/build-windows.yml` usa .NET 9 e gera um executável self-contained para Windows x64.

## Observação

O áudio por processo usa APIs modernas do Windows. A versão é destinada a Windows 10/11 recentes; Windows 11 é o alvo recomendado.
