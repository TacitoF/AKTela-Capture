# AKTela Capture v0.3

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
