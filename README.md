# AKTela Capture v0.6.1

Atualização focada em identidade visual, simplicidade e uso pela bandeja do Windows.

## Interface

- Novo ícone oficial do AKTela aplicado ao executável, janela e bandeja.
- Visual redesenhado com paleta azul-marinho/ciano inspirada na identidade AKTrovão.
- Interface mais limpa: código, modo, qualidade, fonte, áudio e ação principal.
- Perfis Jogo, Filme e Leve agora aparecem como botões segmentados.
- Botão Colar no código da Activity.
- Combos com desenho próprio para combinar melhor com o tema escuro.
- Tipografia Segoe UI Variable quando disponível e espaçamento baseado em uma grade consistente.

## Bandeja

- Clique esquerdo no ícone abre/restaura imediatamente o AKTela Capture.
- Clique direito abre menu com estado atual, abrir, iniciar/encerrar e sair.
- Tooltip da bandeja mostra se está pronto, aguardando espectador ou ao vivo.
- Ao minimizar pela primeira vez, o Windows informa como reabrir o aplicativo.
- Fechar a janela continua enviando o aplicativo para a bandeja; sair fica disponível nos menus.

## Desempenho

A atualização visual não altera o pipeline de captura/encode. Foram usados apenas controles WinForms e desenho 2D simples, sem WebView, Electron ou framework adicional de UI. Isso preserva o comportamento leve da versão anterior.

O caminho preferencial continua sendo captura DXGI/D3D11 + encoder por hardware, com fallback de compatibilidade quando necessário.


## Ícone do aplicativo

A v0.6.1 embute o novo ícone AKTela diretamente no executável e também o utiliza na janela e no ícone da bandeja. O arquivo `Assets/AKTela.ico` contém múltiplas resoluções para Windows (16 a 256 px).
