# AKTela Capture — Stability v2.2.1

Correções de inicialização e continuidade da captura H.264.

- Remove `scale_d3d11` do caminho padrão por instabilidade em alguns drivers.
- Desktop Duplication continua sendo usado para captura; NVENC continua sendo o encoder preferencial.
- `h264_mf` recebe perfil e nível numéricos; NVENC e `libx264` recebem nomes de perfil (`baseline`, `main`, `high`) e níveis como `3.1`.
- `libx264` habilita CABAC para Main/High e transformação 8x8 para High, evitando que o preset `ultrafast` produza Baseline quando outro perfil foi negociado.
- H.264 Main é o perfil preferido quando os espectadores confirmam suporte; Baseline continua como fallback.
- O SPS real continua sendo validado antes de qualquer quadro ser enviado.
- A aprovação do SPS não encerra mais a captura após o primeiro bloco de vídeo.

## Atualizar

Substitua os arquivos deste repositório e gere um novo executável pelo workflow **Gerar AKTela Capture Stability.exe**, em **Actions**, ou execute no Windows:

```powershell
dotnet publish AKTelaCaptureV1/AKTelaCapture.csproj -c Release -r win-x64 --self-contained true -o publish
```

Feche o Capture antigo e abra o executável 2.2.1. Esta correção não exige atualizar AKTela Activity ou AKTela Relay.

## Verificação no Windows

O workflow compila o aplicativo e executa testes H.264 antes de disponibilizar o executável, tanto em pull requests quanto em atualizações da branch `main`.

Para executar os testes no Windows com .NET 9:

```powershell
dotnet run --project tests/AKTelaCapture.SmokeTests/AKTelaCapture.SmokeTests.csproj -c Release
```

Os testes compilam os arquivos reais do capturador, usam o FFmpeg baixado pelo próprio aplicativo e uma fonte de vídeo sintético. Conferem Baseline/Main/High nas quatro qualidades, o SPS real, o envio contínuo até o segundo quadro-chave e a rejeição de perfil ou nível incompatível antes de enviar vídeo. Um executável FFmpeg existente pode ser fornecido após `--`.

Esta verificação não cobre captura do desktop, drivers de GPU, áudio ou uma sessão real do Discord.
