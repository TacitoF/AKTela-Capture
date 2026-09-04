# AKTela Capture v0.6.2

Correções desta versão:

- captura de janela/jogo prioriza Desktop Duplication (DXGI) em vez de GDI, evitando tela preta em superfícies D3D11/D3D12;
- captura de janela usa a região da janela dentro do monitor selecionado;
- relay pode ser trocado sem recompilar o executável através de `https://ak-tela-three.vercel.app/relay.json`;
- executável passa a ser gerado como `AKTela Capture.exe`, forçando o Windows a usar o novo ícone e evitando o cache do nome antigo;
- GDI permanece apenas como fallback para aplicativos comuns.

## Observação sobre captura de jogos

A captura por região DXGI exige que a janela esteja visível. Se o jogo estiver minimizado ou totalmente coberto por outra janela, o conteúdo capturado pode não corresponder ao jogo.
