# AKTela Capture — Stability v2.1

Aplicativo Windows auxiliar da AKTela.

## Correções v2.1

- Corrige ambiguidade de overload em `H264AccessUnitReader` que impedia o build no GitHub Actions.
- Remove `stackalloc` dentro do loop do leitor IVF.
- Remove aviso nullable do ícone da janela.
- Atualiza Concentus para `OpusCodecFactory` + API `Span`, usando a interface moderna e podendo aproveitar o backend nativo quando disponível.
- Torna o envio WebSocket mais justo: controle, vídeo e áudio avançam no mesmo ciclo, evitando starvation do vídeo pelo áudio/cursor.
- Mantém as melhorias Stability v2: codec negociado, validação do SPS real, keyframe sob demanda, qualidade adaptativa, diagnóstico, reconexão e apenas um transmissor por sala.

## Estrutura

- Projeto: `AKTelaCaptureV1/AKTelaCapture.csproj`
- Workflow: `.github/workflows/build-windows.yml`
- Relay: `wss://aktela-relay.tacito1-filho.workers.dev/ws`
