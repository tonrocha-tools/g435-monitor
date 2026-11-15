# Changelog

## [1.2.0] — 2026-11-15

### Added

- **Início minimizado**: ao iniciar com o Windows, o app abre apenas com o
  ícone na bandeja, sem exibir a janela. Abrir o app manualmente continua
  mostrando a janela normalmente.

## [1.1.3] — 2026-11-14

### Changed

- O tempo estimado agora usa o consumo dos **últimos 30 min** (antes 60 min).
- Adicionado tooltip no tempo estimado explicando que volume, jogos e pausas
  mudam a estimativa.

## [1.1.2] — 2026-11-14

### Changed

- O rótulo “Restante estimado” passou a ser **“Tempo estimado”**.
- Quando ainda não há leituras suficientes, o app mostra
  **“aguardando leituras”** em vez de “—”.

## [1.1.1] — 2026-11-14

### Changed

- Renomeado “Restante estimado” para “Tempo estimado”.

## [1.1.0] — 2026-11-14

### Added

- **Notificações de bateria baixa** em 20%, 10% e 5%, uma vez por ciclo de
  descarga, com opção nas Configurações.
- **Tempo estimado** até precisar recarregar, calculado pela descida real da
  tensão.
- **Estado desconectado claro:** ícone cinza/riscado na bandeja e dica na
  janela com instruções.
- **Mensagens amigáveis** para dongle ausente, fone desligado/fora de alcance
  e leitura inválida.
- **Atualização automática** ao voltar do standby e ao reconectar o dongle USB.

### Changed

- Ajustes de altura/layout da janela principal e das Configurações.
- README e screenshot atualizados.

## [1.0.0] — 2026-11-14

### Added

- Primeira versão pública.
- Leitura da bateria do G435 LIGHTSPEED pelo dongle HID, sem G HUB.
- Percentual com histerese de 2% e tensão em mV.
- Estado Conectado/Desconectado.
- Ícone de bandeja dinâmico conforme a porcentagem.
- Bandeja, instância única, iniciar com o Windows e intervalo configurável.
