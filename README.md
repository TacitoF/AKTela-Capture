# AKTela Capture v1.3

## Correções

- O campo do código da Activity voltou a ser exibido corretamente.
- O código permanece legível durante a transmissão.
- Botão Colar mantém o estilo visual em vez de ficar cinza.
- Restaura informações essenciais que existiam nas versões anteriores:
  - saída;
  - FPS real da captura;
  - encoder em uso;
  - quantidade de espectadores.
- Status mostra resolução, FPS, encoder e latência sem poluir a interface.
- Clique esquerdo ou duplo clique no ícone da bandeja abre a janela.
- Menu da bandeja mostra o estado atual da transmissão.

A camada de vídeo continua enviando binário diretamente para o Cloudflare Relay. O Relay v2 converte somente o trecho necessário para a Discord Activity.
