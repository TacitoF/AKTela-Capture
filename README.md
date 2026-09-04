# AKTela Capture

Aplicativo Windows auxiliar da AKTela.

## Objetivo desta versão

- Interface pequena e vertical.
- Botão único para ligar/desligar a captura.
- Seleção de monitor.
- Captura por DirectX 11 / Desktop Duplication.
- Limite padrão de 30 FPS para reduzir uso de recursos.
- Pode ser minimizado para a bandeja do Windows.
- Não precisa de administrador.

> Nesta primeira versão o app já captura a tela localmente, mas ainda não envia o vídeo para a Activity. A próxima etapa do projeto conecta os quadros ao relay/WebSocket da AKTela e ao player dentro do Discord.

## Gerar o EXE sem instalar ferramentas no PC

1. Crie um repositório no GitHub e envie todo o conteúdo desta pasta.
2. Abra a aba **Actions**.
3. Abra **Gerar AKTela Capture.exe**.
4. Clique em **Run workflow**.
5. Quando terminar, baixe o artefato `AKTela-Capture-Windows-x64`.
6. Extraia e execute `AKTelaCapture.exe`.

O workflow usa um computador Windows do GitHub para compilar o executável.

## Observação sobre o Windows SmartScreen

Como o executável ainda não possui certificado de assinatura de código, o Windows pode exibir um aviso do SmartScreen. Isso é esperado durante o desenvolvimento.
