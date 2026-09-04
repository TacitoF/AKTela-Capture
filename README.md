# AKTela Capture v0.3.4

Correções desta versão:

- Corrige o cursor piscando no Windows quando o fallback `gdigrab` é usado. O cursor do sistema não é desenhado pelo FFmpeg nesta versão (`draw_mouse=0`).
- Força a saída do encoder exatamente em 30 ou 60 FPS com `-r:v` + `-fps_mode cfr`.
- Corrige o indicador de FPS da interface, que podia mostrar 60 em uma transmissão configurada para 30 por causa da forma como alguns fluxos H.264 do Media Foundation são divididos em access units.
- Mantém 1080p e os fallbacks NVENC / Media Foundation da v0.3.3.

> Observação: nesta versão o cursor do Windows não aparece para os espectadores quando o caminho de compatibilidade GDI é usado. Isso elimina o flicker local. Uma sobreposição de cursor independente pode ser adicionada depois sem mover/piscar o cursor real do usuário.
