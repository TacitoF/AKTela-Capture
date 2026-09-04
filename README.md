# AKTela Capture v0.3.3

Correção do pipeline de vídeo.

## O que mudou

- Em telas 1920×1080, tenta primeiro `ddagrab -> h264_nvenc` diretamente, sem `scale_d3d11`.
- Se o caminho DXGI/D3D11 falhar, usa um fallback de captura diferente (`gdigrab`) em vez de repetir o mesmo pipeline quebrado.
- Tenta NVENC no fallback e, por último, Media Foundation.
- Mensagens de erro agora preservam mais linhas do FFmpeg para diagnóstico.
- Mantém 1080p, 30/60 FPS, CBR e parâmetros de baixa latência.

O fallback GDI é apenas de compatibilidade e pode consumir um pouco mais CPU que o caminho DXGI principal.
