# AKTela Capture 2.3.5

Interface redesenhada e correções de estabilidade para transmissão em tempo real.

- A janela agora se limita automaticamente à área útil do monitor, respeita DPI por monitor e pode ser redimensionada; em telas menores, a configuração continua acessível por rolagem.
- A exclusão do áudio da chamada prioriza o processo do Discord com sessão de áudio ativa, inclusive quando existem árvores antigas ou múltiplas instâncias.

- Novo layout responsivo, com fluxo de configuração mais claro, estados legíveis e diagnóstico na tela principal.
- A fila de vídeo agora preserva a dependência entre quadros: após congestionamento, aguarda um novo quadro-chave em vez de enviar deltas inválidos.
- O áudio passa a iniciar corretamente depois da negociação de compatibilidade.
- O cursor remoto desaparece quando fica inativo, acompanhando o comportamento de players em tela cheia.
- Medição de latência a cada 6 segundos e redução de qualidade mais rápida em rede congestionada.
- A latência usada na adaptação percorre Capture → espectador → Capture.
- Proteções contra concorrência ao iniciar, encerrar, reconectar e fechar o aplicativo.

- Remove `scale_d3d11` do caminho padrão por instabilidade em alguns drivers.
- Desktop Duplication continua sendo usado para captura; NVENC continua sendo o encoder preferencial.
- `h264_mf` recebe perfil e nível numéricos; NVENC e `libx264` recebem nomes de perfil (`baseline`, `main`, `high`) e níveis como `3.1`.
- `libx264` habilita CABAC para Main/High e transformação 8x8 para High, evitando que o preset `ultrafast` produza Baseline quando outro perfil foi negociado.
- H.264 Main é o perfil preferido quando os espectadores confirmam suporte; Baseline continua como fallback.
- O SPS real continua sendo validado antes de qualquer quadro ser enviado.
- A aprovação do SPS não encerra mais a captura após o primeiro bloco de vídeo.

## Atualizar

Baixe a versão publicada em **Releases**, execute-a no Windows x64 e cole o código de seis caracteres exibido pela Activity. Para compilar localmente:

```powershell
dotnet publish AKTelaCaptureV1/AKTelaCapture.csproj -c Release -r win-x64 --self-contained true -o publish
```

As correções de sincronização de espectadores dependem também das versões atuais de AKTela Activity e AKTela Relay.

## Verificação no Windows

O workflow compila o aplicativo e executa testes H.264 antes de disponibilizar o executável, tanto em pull requests quanto em atualizações da branch `main`.

Para executar os testes no Windows com .NET 9:

```powershell
dotnet run --project tests/AKTelaCapture.SmokeTests/AKTelaCapture.SmokeTests.csproj -c Release
```

Os testes compilam os arquivos reais do capturador, usam o FFmpeg baixado pelo próprio aplicativo e uma fonte de vídeo sintético. Conferem Baseline/Main/High nas quatro qualidades, o SPS real, o envio contínuo até o segundo quadro-chave e a rejeição de perfil ou nível incompatível antes de enviar vídeo. Um executável FFmpeg existente pode ser fornecido após `--`.

Esta verificação não cobre captura do desktop, drivers de GPU, áudio ou uma sessão real do Discord.

- O código da Activity é validado enquanto você digita e aceita Ctrl+V normalmente.
- O estado AO VIVO e mensagens de erro ganharam maior destaque visual.
- A release passa a incluir o executável também dentro de um arquivo ZIP.

- Corrige o byte de versão dos pacotes para AKV5; o Relay deixa de descartar os quadros.
- Mantém o formato vertical preferencial de 560 × 860 sem ultrapassar a resolução disponível.
