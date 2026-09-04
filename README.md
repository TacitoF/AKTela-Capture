# AKTela Capture v0.2

Cliente Windows leve para capturar a tela e enviar quadros compactados para a AKTela Activity.

## Fluxo de teste

1. Abra a AKTela dentro do Discord.
2. Copie o código de 6 caracteres exibido na parte inferior da Activity.
3. Cole o código em **Código da Activity** no AKTela Capture.
4. Escolha a tela.
5. Clique em **Ligar compartilhamento**.
6. O próprio transmissor e os demais participantes na mesma Activity devem começar a ver a imagem.

## Modo leve desta versão

- Captura DX11 Desktop Duplication.
- Até 15 FPS no stream experimental.
- Downscale 2× em telas HD/Full HD para reduzir cópia, CPU e banda.
- JPEG qualidade 52 para validar o fluxo completo.
- Se ninguém estiver assistindo, a compactação dos frames é pausada automaticamente.
- A fila mantém somente o frame mais recente para não acumular atraso.

Depois de validar o fluxo, o encoder JPEG pode ser substituído por vídeo com aceleração de hardware.
