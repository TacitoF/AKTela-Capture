# AKTela Capture — Clean v1.1

Esta versão usa uma pasta de projeto nova (`AKTelaCaptureV1`) e uma lista explícita de arquivos C# no `.csproj`.
Assim, arquivos antigos que ainda estejam no repositório não são compilados e não podem causar classes duplicadas.

Relay: `wss://aktela-relay.tacito1-filho.workers.dev/ws`

## Estrutura que deve ser enviada ao GitHub

- `.github/workflows/build-windows.yml`
- `AKTelaCaptureV1/`
- `README.md`

A pasta antiga `AKTelaCapture/` pode permanecer temporariamente no repositório; o novo workflow não a usa.
