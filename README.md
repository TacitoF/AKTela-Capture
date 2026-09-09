# AKTela Capture 2.7.0

Interface redesenhada e correções de estabilidade para transmissão em tempo real.

- O perfil `Jogo` agora usa 720p e 60 FPS por padrão, processando 56% menos pixels que 1080p60 sem perder fluidez.
- NVENC tenta primeiro manter captura, conversão e redimensionamento em superfícies D3D11, sem copiar cada frame para a RAM.
- Drivers incompatíveis voltam automaticamente para os caminhos estáveis com cópia, Media Foundation ou software.
- O Capture reduz `1080p60 → 720p60 → 720p30` quando a máquina não sustenta o FPS solicitado.
- Encoder H.264/VP8 por software ativa 720p30 automaticamente para não disputar CPU com o jogo.
- O FFmpeg executa abaixo da prioridade normal e o áudio deixa de consultar o buffer a cada 3 ms.

- O relógio do áudio agora avança pelas amostras codificadas, evitando sobreposição quando os pacotes são processados em rajadas.
- A fila de envio preserva somente o áudio recente durante congestionamentos, mantendo o vídeo como prioridade e evitando som atrasado.
- Os lotes de mídia foram reduzidos de 80 ms para 40 ms, alimentando o player com áudio mais uniforme.
- A fila de áudio tolera oscilações breves de até 160 ms sem eliminar imediatamente um bloco Opus.

- Até três Captures podem transmitir na mesma Activity, cada um ocupando uma tela independente.
- Quando houver duas ou três telas, o Capture limita automaticamente cada transmissão a 720p e 30 FPS para reduzir banda, CPU e custo no Relay.
- O status identifica a posição da transmissão (`Tela 1/3`, `Tela 2/3` ou `Tela 3/3`).
- O nome editável do transmissor acompanha cada tela na Activity.
- Vídeo e áudio são agrupados em lotes de 40 ms, reduzindo em cerca de 3 vezes as mensagens contabilizadas pela Cloudflare.
- A captura de janela usa `gfxcapture`/Windows.Graphics.Capture com o `HWND` selecionado, evitando quadros pretos em jogos e aplicativos acelerados por GPU.
- Janelas mantêm a proporção original e ficam centralizadas no vídeo, sem corte ou deformação; bordas visíveis são calculadas pelo DWM.
- O FFmpeg antigo em cache é atualizado automaticamente para uma compilação com `gfxcapture` e `scale_d3d11`.
- Desktop Duplication e GDI permanecem como fallbacks, nesta ordem, para máquinas ou aplicativos incompatíveis.

- A janela agora se limita automaticamente à área útil do monitor, respeita DPI por monitor e pode ser redimensionada; em telas menores, a configuração continua acessível por rolagem.
- A exclusão do áudio da chamada prioriza o processo do Discord com sessão de áudio ativa, inclusive quando existem árvores antigas ou múltiplas instâncias.

- Novo layout responsivo, com fluxo de configuração mais claro, estados legíveis e diagnóstico na tela principal.
- A fila de vídeo agora preserva a dependência entre quadros: após congestionamento, aguarda um novo quadro-chave em vez de enviar deltas inválidos.
- O áudio passa a iniciar corretamente depois da negociação de compatibilidade.
- O cursor remoto desaparece quando fica inativo, acompanhando o comportamento de players em tela cheia.
- Medição de latência a cada 6 segundos e redução de qualidade mais rápida em rede congestionada.
- A latência usada na adaptação percorre Capture → espectador → Capture.
- Proteções contra concorrência ao iniciar, encerrar, reconectar e fechar o aplicativo.

- `scale_d3d11` retorna como primeira tentativa de alto desempenho; falhas de driver acionam automaticamente o caminho estável anterior.
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
