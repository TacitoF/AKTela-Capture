# AKTela Capture v1.4

Correções desta versão:

- H.264 passa a usar Baseline Profile para maior compatibilidade com WebCodecs/Discord;
- nível H.264 é escolhido conforme resolução/FPS;
- fallback final via `libx264` baseline/zerolatency se os encoders por hardware falharem;
- saída preserva a proporção da tela/janela em vez de cortar ou deformar conteúdo;
- atalho global `Ctrl + Shift + S` restaurado para iniciar/encerrar a transmissão;
- menu da bandeja exibe o mesmo atalho;
- controles de mídia e controle usam filas separadas para evitar que cursor/ping/status sejam descartados durante vídeo pesado;
- fila de vídeo menor para reduzir acúmulo de latência;
- mantém informações de Saída, Captura, Encoder e Assistindo na interface.

Estrutura:

- `.github/workflows/build-windows.yml`
- `AKTelaCaptureV1/`
- `README.md`

Substitua `AKTelaCaptureV1/` e `.github/workflows/build-windows.yml` no repositório `AKTela-Capture`.
