# AKTela Capture — reconstrução limpa v1

Aplicativo Windows auxiliar da AKTela.

- Relay direto: `wss://aktela-relay.tacito1-filho.workers.dev/ws`
- Não depende do relay antigo da Vercel.
- Captura de tela/janela prioriza Desktop Duplication + NVENC.
- `scale_d3d11` é tentado primeiro para redimensionamento na GPU; há fallbacks.
- Áudio de janela usa Process Loopback; tela inteira exclui a árvore de processos do Discord.
