# AKTela Capture — Stability v2.2

Correção de compatibilidade do encoder no Windows.

- Remove `scale_d3d11` do caminho padrão por instabilidade em alguns drivers.
- Desktop Duplication continua sendo usado para captura; NVENC continua sendo o encoder preferencial.
- `h264_mf` e `libx264` recebem IDs numéricos de profile/level (66/77/100), evitando `Undefined constant ... baseline`.
- H.264 Main é o perfil preferido quando os espectadores confirmam suporte; Baseline continua como fallback.
- O SPS real continua sendo validado antes de qualquer quadro ser enviado.
