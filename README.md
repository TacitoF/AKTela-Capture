# AKTela Capture v0.5.2

Atualização focada em interface e baixa latência.

- Removido da tela principal o texto explicativo sobre cursor automático.
- Janela ficou um pouco mais baixa, mantendo apenas as informações essenciais.
- Fila de envio do relay foi reduzida para limitar memória e evitar acúmulo excessivo de frames quando a rede oscila.
- Mantém perfis Jogo, Filme, Leve e Personalizado, captura de áudio sem retorno do Discord e cursor remoto separado.

## Desempenho

O caminho preferencial continua sendo captura DXGI/D3D11 + encoder por hardware. Se o caminho direto não estiver disponível, o Capture faz fallback para os modos de compatibilidade.

O consumo real de CPU/GPU depende da GPU, driver, resolução/FPS e fonte capturada e deve ser medido no Windows durante uma transmissão real.
