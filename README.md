# AKTela Capture — Stability v2

Aplicativo Windows responsável pela captura, encoder e envio ao relay.

## Estrutura do repositório

```text
.github/workflows/build-windows.yml
AKTelaCaptureV1/
README.md
```

## Estabilidade

- Um único publisher por Activity; um segundo Capture é recusado sem derrubar o primeiro.
- Reconexão do mesmo Capture usa um ID persistente durante a sessão e substitui apenas o socket antigo.
- Preflight antes de transmitir: relay, FFmpeg, encoder/perfil real e áudio.
- O vídeo só começa depois que os espectadores anunciam os codecs que realmente suportam.
- Validação do SPS H.264 real antes de enviar qualquer quadro.
- H.264 Baseline/Main/High + fallback VP8.
- FFmpeg 9.0 estável, não build master.
- Keyframe sob demanda após entrada/reconexão/erro do decoder.
- Fila de vídeo curta: frames antigos são descartados para priorizar baixa latência.
- Qualidade adaptativa conforme RTT e descartes.
- Diagnóstico pelo menu da bandeja.
- Atalho global `Ctrl + Shift + S` para iniciar/encerrar.

## Build

O GitHub Actions gera o artefato `AKTela-Capture-Stability-v2-Windows-x64`.
