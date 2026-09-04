# AKTela Capture v0.3.2

Capture nativo para Windows focado em 1080p com baixo impacto.

## Destaques
- 1920×1080.
- Seletor de 30 ou 60 FPS.
- Captura via Desktop Duplication/D3D11 do FFmpeg (`ddagrab`).
- Escala em D3D11 e H.264 por hardware; tenta NVIDIA NVENC e usa Media Foundation como fallback.
- Perfil de baixa latência: CBR, buffer VBV próximo de 1 quadro, sem B-frames/lookahead.
- Áudio do sistema 48 kHz estéreo em Opus 128 kbps.
- Sem espectadores: captura/encoder são desligados automaticamente.
- O FFmpeg é baixado uma única vez para `%LOCALAPPDATA%\AKTelaCapture\tools` quando a primeira transmissão realmente começa.

## Build
O workflow `.github/workflows/build-windows.yml` continua gerando `AKTelaCapture.exe` no GitHub Actions.


## Atualização v0.3.2.2
Se estiver atualizando a partir da v0.2.x, este pacote sobrescreve o antigo `CaptureController.cs`, que não é mais usado.


## v0.3.2
- Corrige o fallback Media Foundation removendo `-profile:v high` e `-level:v 4.2`, que não são aceitos dessa forma por `h264_mf` em builds atuais do FFmpeg.
- Se NVENC e Media Foundation falharem, agora o erro mostra os dois motivos separadamente.
