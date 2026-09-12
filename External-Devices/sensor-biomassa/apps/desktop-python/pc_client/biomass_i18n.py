"""Runtime English and Brazilian Portuguese text for the desktop app."""

from __future__ import annotations

import re


EN = "en"
PT_BR = "pt-BR"


EN_TEXT = {
    "connect_hint": (
        "USB serial is the more reliable choice for long unattended runs: no "
        "reconnects, no AP contention, and it powers the device. The AP is open "
        "at {host} but gives that adapter no route to the internet."
    ),
    "state_idle": "idle",
    "state_measuring": "measuring",
    "state_blanking": "blanking",
    "state_searching": "searching",
    "state_unknown": "unknown",
    "device_info": "fw v{fw}  ·  boot #{boot}  ·  {uptime}  ·  heap {heap} kB",
    "health_status": (
        "I2C errors {i2c}   ·   saturation {sat}   ·   sensor resets {resets}   ·   "
        "failed searches {failed}   ·   SoC {temp}"
    ),
    "boundary_misses_warn": (
        "{n} reading(s) could not see a conversion boundary and fell back to a "
        "blind wait. That is the timing v5.0 exists to stop relying on — the "
        "gear is probably too dim. Check the blank table and treat those "
        "readings with suspicion."
    ),
    "firmware_warning": (
        "Device runs firmware v{fw}; manual hardware control needs v{required}. "
        "Flash the newer firmware to enable these controls."
    ),
    "saved_samples": "saved {count} samples · {path}",
    "recording_path": "recording -> {path}",
    "recording_samples": "recording -> {path}   ({count} samples)",
    "set_pwm_warning": (
        "Set level {index} to {value:.1f} %?\n\nThis ERASES the stored blank "
        "calibration. You will have to re-blank before measuring again."
    ),
    "set_it_warning": (
        "Set slot {index} to {value}?\n\nThis ERASES the stored blank calibration."
    ),
    "blank_legend": (
        "{sat} of {total} gears saturated while blanking and {dim} measured "
        "too little light to be usable (under 500 counts). The device will not "
        "select either kind — a saturated cell reports -99, and a near-dark "
        "one carries no light information to divide by. The highlighted cell "
        "is the gear in use."
    ),
    "blank_too_dim_tip": (
        "Under 500 counts: this cell measured the dark, not light. The device "
        "refuses to select it as a gear."
    ),
    "probe_period_confirm": (
        "Measure the sensor's real conversion period in every integration "
        "slot?\n\nThe device drives the LED for about 40 s and reports one "
        "line per slot into the log below. It must be idle, and no sampling "
        "happens while it runs."
    ),
    "probe_period_http": (
        "This diagnostic reports its results over the USB serial connection "
        "only.\n\nOver WiFi the command runs on the device but the answers "
        "never reach this app. Connect over USB to read them."
    ),
    "probe_period_started": (
        "measuring conversion period (~40 s); one result line per integration "
        "slot will follow"
    ),
    "probe_period_hint": (
        "Checks the sensor's real conversion period against the guard the "
        "firmware's timing assumes. Worth running if readings come back "
        "unexpectedly low, or after a large change in room temperature."
    ),
    "set_pwm_preset_warning": (
        "Restore the recommended LED ladder (2, 3.5, 6, 10.5, 18, 32, 57, "
        "100 %)?\n\nThis ERASES the stored blank calibration. You will have to "
        "re-blank before measuring again."
    ),
    "interval_floor": (
        "Minimum {floor} s on this device: faster sampling keeps the LED on "
        "more than {duty}% of the time, and the heat drops its output enough "
        "to read as absorbance that is not there. Locking a short integration "
        "time in Optics lowers this floor."
    ),
    "new_experiment_summary": (
        "Clears the plot, closes the current file, and starts recording to a "
        "new folder under:\n{path}"
    ),
    "experiment_running": "● RECORDING — {name}   ·   {count} samples",
    "experiment_idle": "○ No experiment recording",
    "plot_span": "{lo} to {hi} AU  ·  span {span} mAU",
    "relocate_warning": (
        "This run is recording, and {count} sample(s) are already saved under "
        "its current name and folder.\n\nMove the data into the new file, or "
        "leave it where it is and start a new, empty file?"
    ),
    "seed_from_device": "Start from the {count} sample(s) in device memory",
    "seed_hint": (
        "The sensor keeps its own record of every sample since it booted. "
        "Including it writes those samples into this run with the times they "
        "were actually measured, so an experiment that was already running "
        "before the app was opened is not lost. Leave it off to begin now."
    ),
    "seed_hint_empty": "The device is holding no samples to start from.",
    "blank_mode_fast": "Fast sweep — about 25 s",
    "blank_mode_slow": "Slow sweep — about 3 min at {duty} % LED duty",
    "blank_hint_slow": (
        "The sweep drives the LED hard, and back to back it delivers as much "
        "heat in 25 s as five minutes of measuring. Each gear is then "
        "captured at a different temperature, and the whole table belongs to "
        "a thermal state the instrument leaves as soon as it starts "
        "measuring. Pacing the sweep holds it at the same LED duty a run "
        "uses, so every gear is zeroed under the conditions it will be used "
        "in. Clear media must be in the beam throughout."
    ),
    "blank_hint_fast": (
        "Quick, and enough when the instrument is already warm and you only "
        "need the range map. On a cold instrument it can leave several mAU of "
        "offset that decays over the following half hour. Clear media must be "
        "in the beam."
    ),
    "blank_hint_skipped": (
        "The stored calibration is kept. Absorbance stays relative to "
        "whatever blank is already on the device."
    ),
    "blank_hint_no_pacing": (
        "This firmware sweeps back to back only; paced sweeps need v5.2. "
        "Clear media must be in the beam. Takes about 25 s."
    ),
    "blank_confirm": (
        "Sweep all 32 gears and overwrite the stored I0 calibration?\n\n"
        "Clear media must be circulating.\n\n"
        "Slow paces the sweep at the LED duty a run uses, so every gear is "
        "zeroed at the temperature it will be measured at (about 3 min). "
        "Fast sweeps back to back (about 25 s) and is enough for a range map "
        "on an already-warm instrument."
    ),
    "blank_confirm_fast": (
        "Sweep all 32 gears and overwrite the stored I0 calibration?\n\n"
        "Clear media must be circulating. Takes about 25 s."
    ),
    "Slow sweep": "Slow sweep",
    "Fast sweep": "Fast sweep",
    "log_hidden": "{count} filtered out",
    "device_buffer": "{stored} of {size} samples held on the device.",
    "clear_buffer_warning": (
        "Discard the {count} sample(s) the device is holding in RAM?\n\nThey "
        "are the only copy of anything not already written to a CSV, and "
        "clearing them means a run started later cannot recover them. Files "
        "already saved are not affected."
    ),
}


PT = {
    # Connection dialog and shared labels.
    "Connect to sensor": "Conectar ao sensor",
    "Biomass Sensor": "Sensor de Biomassa",
    "USB serial (recommended)": "USB serial (recomendado)",
    "WiFi / device AP": "Wi-Fi / ponto de acesso do dispositivo",
    "Refresh": "Atualizar",
    "Port": "Porta",
    "Host": "Endereço",
    "Auto-detect": "Detectar automaticamente",
    "Connect": "Conectar",
    "Disconnect": "Desconectar",
    "Cancel": "Cancelar",
    "Yes": "Sim",
    "Light": "Claro",
    "Dark": "Escuro",
    "connecting": "conectando",
    "disconnected": "desconectado",
    "not recording": "sem gravação",
    "ERR": "ERRO",
    "connect_hint": (
        "A conexão USB serial é a opção mais confiável para ensaios longos sem "
        "supervisão: não há reconexões nem disputa pelo ponto de acesso, e ela "
        "também alimenta o dispositivo. O ponto de acesso é aberto em {host}, "
        "mas esse adaptador fica sem rota para a internet."
    ),
    "No serial ports found.": "Nenhuma porta serial encontrada.",
    "Connecting...": "Conectando...",
    "No port selected.": "Nenhuma porta selecionada.",
    "Looking for the sensor...": "Procurando o sensor...",
    "Probing {port} ...": "Verificando {port} ...",
    "Probing the device AP ...": "Verificando o ponto de acesso do dispositivo ...",
    "No sensor found on any port or at the AP.": (
        "Nenhum sensor encontrado nas portas seriais nem no ponto de acesso."
    ),
    "Failed: {error}": "Falha: {error}",
    "Connection failed": "Falha na conexão",

    # Header, live values, plots, and navigation.
    "AU": "UA",
    "RAW I": "I BRUTO",
    "BLANK I0": "I0 BRANCO",
    "TRANSM.": "TRANSM.",
    "INT. TIME": "TEMPO INT.",
    "LED DUTY": "CICLO LED",
    "SAMPLES": "AMOSTRAS",
    "INTERVAL": "INTERVALO",
    "Absorbance (AU)": "Absorbância (UA)",
    "counts": "contagens",
    "elapsed (h)": "tempo decorrido (h)",
    "absorbance": "absorbância",
    "single shot": "leitura única",
    "raw I": "I bruto",
    "blank I0": "I0 do branco",
    "All": "Tudo",
    "6 h": "6 h",
    "1 h": "1 h",
    "15 min": "15 min",
    "Pause plot": "Pausar gráfico",
    "Resume plot": "Retomar gráfico",
    "Autoscale": "Escala automática",
    "Export image": "Exportar imagem",
    "Run": "Ensaio",
    "Optics": "Óptica",
    "Ranging": "Faixa automática",
    "Advanced": "Avançado",

    # Run tab.
    "MEASUREMENT": "MEDIÇÃO",
    "Blank": "Branco",
    "Start": "Iniciar",
    "Stop": "Parar",
    "Single reading": "Leitura única",
    "A single reading is unfiltered (no median/EMA history for a one-off) and is flagged in the CSV so it can be excluded from fits.": (
        "Uma leitura única não é filtrada (não há histórico de mediana/EMA para "
        "uma leitura isolada) e recebe uma marca no CSV para que possa ser "
        "excluída dos ajustes."
    ),
    "SAMPLING INTERVAL": "INTERVALO DE AMOSTRAGEM",
    "How often a measurement is taken while running. The LED is only on during the read itself.": (
        "Frequência das medições durante a execução. O LED fica ligado somente "
        "durante a própria leitura."
    ),
    "RECORDING": "GRAVAÇÃO",
    "Browse": "Procurar",
    "Start recording": "Iniciar gravação",
    "Stop recording": "Parar gravação",
    "note (e.g. fed 5 mL glucose)": "nota (ex.: adicionados 5 mL de glicose)",
    "Add": "Adicionar",
    "Notes are timestamped into meta.json with the current sample number.": (
        "As notas são registradas no meta.json com data, hora e número da amostra atual."
    ),
    "CONNECTION": "CONEXÃO",
    "Push to TECNAL hub": "Enviar ao hub TECNAL",
    "Off means direct control only. Leave it off when no hub is present: the device scans for WiFi every 10 s while enabled, and each scan briefly disturbs clients on its own AP.": (
        "Desativado significa somente controle direto. Mantenha desativado quando "
        "não houver hub: enquanto ativado, o dispositivo procura redes Wi-Fi a "
        "cada 10 s, e cada busca interfere brevemente nos clientes do próprio "
        "ponto de acesso."
    ),

    # Optics tab.
    "GEAR CONTROL": "CONTROLE DA COMBINAÇÃO ÓPTICA",
    "Auto-ranging": "Ajuste automático de faixa",
    "Off locks the gear below. High Density Mode is also suppressed, so nothing overrides your choice.": (
        "Desativar fixa a combinação abaixo. O Modo de Alta Densidade também é "
        "suprimido, portanto nada substitui sua escolha."
    ),
    "INTEGRATION TIME": "TEMPO DE INTEGRAÇÃO",
    "Sensor integration time. Longer collects more light, so it suits dense cultures.": (
        "Tempo de integração do sensor. Tempos maiores coletam mais luz e são "
        "adequados para culturas densas."
    ),
    "LED LEVEL": "NÍVEL DO LED",
    "One of the 8 calibrated duty levels. Only these have a blank, so only these give valid absorbance.": (
        "Um dos 8 níveis calibrados de ciclo de trabalho. Somente esses níveis "
        "possuem branco e, portanto, fornecem absorbância válida."
    ),
    "DIRECT LED (IDLE ONLY)": "LED DIRETO (SOMENTE OCIOSO)",
    "LED DUTY": "CICLO LED",
    "Drives the LED at an arbitrary duty for optical alignment. Produces no valid absorbance.": (
        "Aciona o LED com ciclo de trabalho arbitrário para alinhamento óptico. "
        "Não produz absorbância válida."
    ),
    "LED off": "Desligar LED",
    "Sweep": "Varredura",
    "SWEEP STEP": "PASSO DA VARREDURA",
    "There is no blank for an uncalibrated duty, so absorbance cannot be computed here. Alignment and LED checks only — stop the run first.": (
        "Não há branco para um ciclo de trabalho não calibrado, portanto a "
        "absorbância não pode ser calculada aqui. Use apenas para alinhamento e "
        "verificação do LED; interrompa o ensaio primeiro."
    ),

    # Ranging tab.
    "AUTO-RANGE THRESHOLDS": "LIMITES DO AJUSTE AUTOMÁTICO",
    "LOW": "BAIXO",
    "Below this raw count for 10 readings in a row, the device searches for a more sensitive gear.": (
        "Abaixo desta contagem bruta por 10 leituras consecutivas, o dispositivo "
        "procura uma combinação mais sensível."
    ),
    "OPTIMAL TARGET": "ALVO ÓTIMO",
    "The raw count a gear search aims for. Best signal-to-noise is well below saturation.": (
        "Contagem bruta visada durante a busca de combinação. A melhor relação "
        "sinal-ruído ocorre bem abaixo da saturação."
    ),
    "HIGH": "ALTO",
    "Above this raw count for 10 readings in a row, the device searches for a less sensitive gear.": (
        "Acima desta contagem bruta por 10 leituras consecutivas, o dispositivo "
        "procura uma combinação menos sensível."
    ),
    "Save to device flash": "Salvar na memória flash",
    "Threshold changes apply immediately but live in RAM. Without saving, a reboot restores the stored values.": (
        "As alterações dos limites são aplicadas imediatamente, mas permanecem "
        "na RAM. Sem salvar, uma reinicialização restaura os valores armazenados."
    ),
    "FILTERING": "FILTRAGEM",
    "EMA COEFFICIENT": "COEFICIENTE EMA",
    "Low-pass on the median-filtered signal. 1.0 disables it; lower is smoother but slower to follow real change.": (
        "Filtro passa-baixa aplicado ao sinal filtrado pela mediana. O valor 1,0 "
        "o desativa; valores menores suavizam mais, mas respondem mais lentamente."
    ),
    "Applied after a 5-sample median. The median kills single-sample spikes (bubbles); the EMA smooths what is left.": (
        "Aplicado após uma mediana de 5 amostras. A mediana remove picos isolados "
        "(bolhas), e a EMA suaviza o sinal restante."
    ),
    "HEALTH": "DIAGNÓSTICO",
    "Reset counters": "Zerar contadores",

    # Advanced tab.
    "LED LEVEL TABLE": "TABELA DE NÍVEIS DO LED",
    "The 8 duty levels auto-ranging can choose from. Changing any of them ERASES the stored blank: the blank is indexed by gear, so a moved level makes its I0 wrong and every absorbance from it silently incorrect.": (
        "Os 8 níveis de ciclo de trabalho disponíveis ao ajuste automático. "
        "Alterar qualquer nível APAGA o branco armazenado: o branco é indexado "
        "pela combinação, portanto um nível alterado torna o I0 incorreto e "
        "invalida silenciosamente as absorbâncias correspondentes."
    ),
    "INTEGRATION TIME TABLE": "TABELA DE TEMPOS DE INTEGRAÇÃO",
    "The 4 integration-time slots, each chosen from the VEML7700's supported values. Also erases the blank.": (
        "Os 4 espaços de tempo de integração, escolhidos entre os valores "
        "compatíveis com o VEML7700. A alteração também apaga o branco."
    ),
    "slot {index}": "posição {index}",
    "BLANK CALIBRATION": "CALIBRAÇÃO DO BRANCO",
    "View blank table": "Ver tabela do branco",
    "DEVICE": "DISPOSITIVO",
    "Save config": "Salvar configuração",
    "Reload config": "Recarregar configuração",
    "Factory reset": "Restaurar padrão de fábrica",

    # Dynamic state and messages.
    "state_idle": "ocioso",
    "state_measuring": "medindo",
    "state_blanking": "calibrando branco",
    "state_searching": "procurando faixa",
    "state_unknown": "desconhecido",
    "high-density": "alta densidade",
    "manual": "manual",
    "SIMULATED": "SIMULADO",
    "device_info": "firmware v{fw}  ·  inicialização nº {boot}  ·  {uptime}  ·  memória {heap} kB",
    "{visible} of {total} points": "{visible} de {total} pontos",
    "Not applied: the device needs low < optimal < high.": (
        "Não aplicado: o dispositivo exige baixo < ótimo < alto."
    ),
    "Auto-ranging is on — the device will move off this gear on its own. Turn it off to hold.": (
        "O ajuste automático está ativo; o dispositivo mudará esta combinação "
        "automaticamente. Desative-o para manter a seleção."
    ),
    "Gear locked. Absorbance stays valid because this gear is in the calibrated table.": (
        "Combinação fixa. A absorbância permanece válida porque esta combinação "
        "está na tabela calibrada."
    ),
    "health_status": (
        "erros I2C {i2c}   ·   saturações {sat}   ·   reinicializações do sensor "
        "{resets}   ·   buscas sem êxito {failed}   ·   SoC {temp}"
    ),
    "boundary_misses_warn": (
        "{n} leitura(s) não conseguiram detectar o limite de uma conversão e "
        "recorreram à espera cega — justamente o comportamento que a v5.0 "
        "existe para evitar. A combinação provavelmente está escura demais. "
        "Verifique a tabela do branco e desconfie dessas leituras."
    ),
    "blank_too_dim_tip": (
        "Abaixo de 500 contagens: esta célula mediu o escuro, não a luz. O "
        "dispositivo se recusa a selecioná-la como combinação."
    ),
    "probe_period_confirm": (
        "Medir o período real de conversão do sensor em cada tempo de "
        "integração?\n\nO dispositivo aciona o LED por cerca de 40 s e informa "
        "uma linha por tempo de integração no registro abaixo. Ele precisa "
        "estar ocioso, e nenhuma amostragem ocorre durante a medição."
    ),
    "probe_period_http": (
        "Este diagnóstico informa os resultados apenas pela conexão USB "
        "serial.\n\nPor WiFi o comando é executado no dispositivo, mas as "
        "respostas não chegam a este aplicativo. Conecte-se por USB para "
        "lê-las."
    ),
    "probe_period_started": (
        "medindo o período de conversão (~40 s); seguirá uma linha de "
        "resultado por tempo de integração"
    ),
    "probe_period_hint": (
        "Compara o período real de conversão do sensor com a margem que a "
        "temporização do firmware assume. Vale executar se as leituras "
        "vierem inesperadamente baixas, ou após uma grande variação de "
        "temperatura ambiente."
    ),
    "Blank present.": "Branco disponível.",
    "No valid blank stored — run a blanking sweep before measuring.": (
        "Nenhum branco válido armazenado; execute uma varredura do branco antes de medir."
    ),
    "firmware_warning": (
        "O dispositivo executa o firmware v{fw}; o controle manual do hardware "
        "requer v{required}. Instale o firmware mais recente para ativar estes controles."
    ),
    "saved_samples": "{count} amostras salvas · {path}",
    "recording_path": "gravando → {path}",
    "recording_samples": "gravando → {path}   ({count} amostras)",

    # Dialogs and file pickers.
    "Run blanking sweep": "Executar varredura do branco",
    # Key must match the English source string exactly -- v5.0's sweep takes
    # ~40 s (the dark settle before every pulse), not the old 15 s.
    "Sweep all 32 gears and overwrite the stored I0 calibration?\n\nClear media must be circulating. Takes about 40 s.": (
        "Varrer todas as 32 combinações e substituir a calibração I0 armazenada?\n\n"
        "O meio límpido deve estar circulando. A operação leva cerca de 40 s."
    ),
    "Measure conversion period": "Medir o período de conversão",
    "Measure period": "Medir período",
    "experiment_running": "● GRAVANDO — {name}   ·   {count} amostras",
    "experiment_idle": "○ Nenhum experimento gravando",
    "Reset all device settings to defaults and ERASE the blank calibration?\n\nYou will have to re-blank before measuring.": (
        "Restaurar todas as configurações padrão do dispositivo e APAGAR a "
        "calibração do branco?\n\nSerá necessário calibrar o branco novamente antes de medir."
    ),
    "Change LED level table": "Alterar tabela de níveis do LED",
    "set_pwm_warning": (
        "Definir o nível {index} como {value:.1f} %?\n\nEsta ação APAGA a "
        "calibração do branco armazenada. Será necessário calibrar novamente antes de medir."
    ),
    "Change integration time table": "Alterar tabela de tempos de integração",
    "set_it_warning": (
        "Definir a posição {index} como {value}?\n\nEsta ação APAGA a calibração "
        "do branco armazenada."
    ),
    "Blank calibration (I0)": "Calibração do branco (I0)",
    "saturated during blanking": "saturado durante a calibração do branco",
    "Saturated cells are unusable: absorbance from them is reported as the -99 error sentinel.": (
        "Células saturadas não podem ser usadas: a absorbância correspondente é "
        "informada pelo valor sentinela de erro -99."
    ),
    "Output folder": "Pasta de saída",
    "Export plot": "Exportar gráfico",
    "PNG image (*.png)": "Imagem PNG (*.png)",

    # Blank calibration viewer.
    "Sensor counts with clear media at each gear. Absorbance is "
    "-log10(I/I0) using the cell for the gear in use.": (
        "Contagens do sensor com meio limpo em cada combinação. A absorbância é "
        "-log10(I/I0), usando a célula da combinação em uso."
    ),
    "sat.": "sat.",
    "gear currently in use": "combinação em uso no momento",
    "blank_legend": (
        "{sat} de {total} combinações saturaram durante a calibração e {dim} "
        "mediram luz insuficiente para serem utilizáveis (menos de 500 "
        "contagens). O dispositivo não seleciona nenhum dos dois casos — uma "
        "célula saturada informa -99, e uma célula quase escura não carrega "
        "informação de luz para servir de divisor. A célula destacada é a "
        "combinação em uso."
    ),
    "Close": "Fechar",

    # New experiment.
    "New experiment": "Novo experimento",
    "Start a new experiment": "Iniciar novo experimento",
    "Experiment name": "Nome do experimento",
    "Save to folder": "Salvar na pasta",
    "Run blanking sweep (zero) first": "Calibrar o branco (zero) antes",
    "Start measuring automatically": "Iniciar a medição automaticamente",
    "new_experiment_summary": (
        "Limpa o gráfico, fecha o arquivo atual e inicia a gravação em uma nova "
        "pasta dentro de:\n{path}"
    ),
    "Blanking requires clear media in the beam. It sweeps all gears and "
    "takes about 40 s.": (
        "A calibração do branco exige meio limpo no caminho óptico. Ela percorre "
        "todas as combinações e leva cerca de 40 s."
    ),
    "Clear plot": "Limpar gráfico",
    "Discard the plotted data? The recorded file is not affected.": (
        "Descartar os dados do gráfico? O arquivo gravado não é afetado."
    ),
    "Recommended levels": "Níveis recomendados",
    "Restore recommended LED levels": "Restaurar níveis de LED recomendados",
    "set_pwm_preset_warning": (
        "Restaurar a escala de LED recomendada (2; 3,5; 6; 10,5; 18; 32; 57; "
        "100 %)?\n\nEsta ação APAGA a calibração do branco armazenada. Será "
        "necessário calibrar novamente antes de medir."
    ),
    "Restores the geometric ladder (2 → 100 %, each step ~1.75x the light of "
    "the one below). Equal ratios mean equal absorbance steps, so "
    "auto-ranging moves evenly across the whole range.": (
        "Restaura a escala geométrica (2 → 100 %, cada passo com ~1,75x a luz "
        "do anterior). Razões iguais produzem passos iguais de absorbância, "
        "então o ajuste automático percorre toda a faixa de forma uniforme."
    ),
    "Clears the plot, closes the current data file, optionally re-zeros the "
    "instrument, and starts recording to a folder you choose.": (
        "Limpa o gráfico, fecha o arquivo de dados atual, opcionalmente refaz "
        "o zero do instrumento e inicia a gravação em uma pasta escolhida."
    ),
    "Discards what is drawn without touching the recorded file.": (
        "Descarta o que está desenhado sem alterar o arquivo gravado."
    ),
    "EXPERIMENT": "EXPERIMENTO",
    "MEASUREMENT": "MEDIÇÃO",
    "DEVICE MEMORY": "MEMÓRIA DO DISPOSITIVO",
    "Run": "Ensaio",
    "Add": "Adicionar",
    "note (e.g. fed 5 mL glucose)": "nota (ex.: alimentado 5 mL de glicose)",
    "plot_span": "{lo} a {hi} AU  ·  faixa {span} mAU",

    # Moving a run that is already recording.
    "Move the recording": "Mover a gravação",
    "Move the data": "Mover os dados",
    "Start a new file": "Iniciar um arquivo novo",
    "relocate_warning": (
        "Este ensaio está gravando, e {count} amostra(s) já foram salvas com "
        "o nome e a pasta atuais.\n\nMover os dados para o novo arquivo, ou "
        "deixá-los onde estão e iniciar um arquivo novo e vazio?"
    ),
    "Changing the name or the folder while a run is recording moves it: the "
    "app asks whether to carry the samples already saved into the new file.": (
        "Alterar o nome ou a pasta durante uma gravação move o ensaio: o "
        "aplicativo pergunta se as amostras já salvas devem ser levadas para "
        "o novo arquivo."
    ),
    "Stopping closes the CSV. The device keeps measuring and keeps its last "
    "1024 samples, so a run started again later can still recover them.": (
        "Parar fecha o arquivo CSV. O dispositivo continua medindo e mantém "
        "suas últimas 1024 amostras, então um ensaio iniciado depois ainda "
        "consegue recuperá-las."
    ),
    "Notes are timestamped into meta.json with the current sample number.": (
        "As notas são registradas em meta.json com data, hora e o número da "
        "amostra atual."
    ),

    # Log filter.
    "LOG": "REGISTRO",
    "Device output": "Saída do dispositivo",
    "Commands sent": "Comandos enviados",
    "log_hidden": "{count} ocultas pelo filtro",

    # Blanking sweep modes.
    "Slow sweep": "Varredura lenta",
    "Fast sweep": "Varredura rápida",
    "blank_mode_fast": "Varredura rápida — cerca de 25 s",
    "blank_mode_slow": (
        "Varredura lenta — cerca de 3 min com {duty} % de ciclo do LED"
    ),
    "blank_hint_slow": (
        "A varredura aciona o LED intensamente e, feita sem pausas, entrega "
        "em 25 s tanto calor quanto cinco minutos de medição. Cada combinação "
        "acaba registrada em uma temperatura diferente, e a tabela inteira "
        "passa a corresponder a um estado térmico que o instrumento abandona "
        "assim que começa a medir. Espaçar a varredura mantém o mesmo ciclo "
        "de LED de um ensaio, então cada combinação é zerada nas condições em "
        "que será usada. O meio límpido deve permanecer no caminho óptico "
        "durante todo o processo."
    ),
    "blank_hint_fast": (
        "Rápida, e suficiente quando o instrumento já está aquecido e só é "
        "necessário o mapa de faixas. Em um instrumento frio pode deixar "
        "alguns mAU de desvio que decaem ao longo da meia hora seguinte. O "
        "meio límpido deve estar no caminho óptico."
    ),
    "blank_hint_skipped": (
        "A calibração armazenada é mantida. A absorbância continua relativa "
        "ao branco que já está no dispositivo."
    ),
    "blank_hint_no_pacing": (
        "Este firmware só faz a varredura sem pausas; varreduras espaçadas "
        "exigem a v5.2. O meio límpido deve estar no caminho óptico. Leva "
        "cerca de 25 s."
    ),
    "blank_confirm": (
        "Varrer todas as 32 combinações e substituir a calibração I0 "
        "armazenada?\n\nO meio límpido deve estar circulando.\n\n"
        "A lenta espaça a varredura no mesmo ciclo de LED de um ensaio, "
        "então cada combinação é zerada na temperatura em que será medida "
        "(cerca de 3 min). A rápida varre sem pausas (cerca de 25 s) e basta "
        "para um mapa de faixas em um instrumento já aquecido."
    ),
    "blank_confirm_fast": (
        "Varrer todas as 32 combinações e substituir a calibração I0 "
        "armazenada?\n\nO meio límpido deve estar circulando. Leva cerca de "
        "25 s."
    ),

    # Device memory.
    # Short on purpose: the card it sits in is already titled MEMÓRIA DO
    # DISPOSITIVO, and the long form pushed the sidebar past its width.
    "Clear device memory": "Limpar memória",
    "device_buffer": "{stored} de {size} amostras armazenadas no dispositivo.",
    "clear_buffer_warning": (
        "Descartar as {count} amostra(s) que o dispositivo mantém na "
        "memória?\n\nElas são a única cópia de tudo o que ainda não foi "
        "gravado em um arquivo CSV, e limpá-las impede que um ensaio "
        "iniciado depois as recupere. Os arquivos já salvos não são afetados."
    ),
    "The sensor keeps its last samples in RAM so the app can recover anything "
    "it missed. Clearing it drops that history, which is the way to make sure "
    "a new experiment cannot pick up the previous one. Saved CSV files are "
    "not affected.": (
        "O sensor mantém suas últimas amostras na RAM para que o aplicativo "
        "possa recuperar o que tiver perdido. Limpar descarta esse histórico, "
        "que é a forma de garantir que um novo experimento não incorpore o "
        "anterior. Os arquivos CSV já salvos não são afetados."
    ),

    # Seeding a new experiment from the device buffer.
    "seed_from_device": (
        "Começar com as {count} amostra(s) da memória do dispositivo"
    ),
    "seed_hint": (
        "O sensor mantém o próprio registro de todas as amostras desde que "
        "foi ligado. Incluí-lo grava essas amostras neste ensaio com o "
        "horário em que foram realmente medidas, de modo que um experimento "
        "já em andamento antes da abertura do aplicativo não se perca. Deixe "
        "desmarcado para começar agora."
    ),
    "seed_hint_empty": (
        "O dispositivo não tem amostras armazenadas para servir de início."
    ),
    "Clears the plot, closes the current data file, optionally re-zeros the "
    "instrument, and starts recording to a folder you choose. It can also "
    "begin from the samples the sensor already holds in its own memory.": (
        "Limpa o gráfico, fecha o arquivo de dados atual, opcionalmente refaz "
        "o zero do instrumento e inicia a gravação em uma pasta escolhida. "
        "Também pode começar pelas amostras que o sensor já mantém na própria "
        "memória."
    ),
    "interval_floor": (
        "Mínimo de {floor} s neste dispositivo: uma amostragem mais rápida "
        "mantém o LED aceso por mais de {duty}% do tempo, e o aquecimento "
        "reduz a emissão o bastante para ser lido como uma absorbância que "
        "não existe. Travar um tempo de integração curto em Óptica reduz "
        "esse limite."
    ),
}


def normalize_language(language: str | None) -> str:
    return PT_BR if str(language).lower().replace("_", "-") in {"pt-br", "pt"} else EN


def has_translation(source: str) -> bool:
    return source in PT


def tr(language: str, source: str, **values) -> str:
    english = EN_TEXT.get(source, source)
    text = PT.get(source, PT.get(english, english)) \
        if normalize_language(language) == PT_BR else english
    try:
        return text.format(**values)
    except (KeyError, ValueError):
        return text


_LOG_PATTERNS = [
    (r"^connected over (.+)$", "conectado por {0}"),
    (r"^backfilled (\d+) sample\(s\) from the device buffer$",
     "recuperadas {0} amostras do buffer do dispositivo"),
    (r"^recording to (.+)$", "gravando em {0}"),
    (r"^stopped recording \((\d+) samples\) -> (.+)$",
     "gravação encerrada ({0} amostras) → {1}"),
    (r"^note ignored: not recording$", "nota ignorada: nenhuma gravação ativa"),
    (r"^note: (.+)$", "nota: {0}"),
    (r"^disconnected$", "desconectado"),
    (r"^reconnected$", "reconectado"),
    (r"^plot exported to (.+)$", "gráfico exportado para {0}"),
    (r"^plot export failed: (.+)$", "falha ao exportar o gráfico: {0}"),
    (r"^export failed: (.+)$", "falha ao exportar: {0}"),
    (r"^poll error: (.+)$", "erro de consulta: {0}"),
    (r"^initial sync failed: (.+)$", "falha na sincronização inicial: {0}"),
    (r"^reconnect failed: (.+)$", "falha ao reconectar: {0}"),
    (r"^could not read blank table: (.+)$", "não foi possível ler a tabela do branco: {0}"),
    (r"^command '(.+)' failed: (.+)$", "falha no comando '{0}': {1}"),
    (r"^setting failed: (.+)$", "falha ao aplicar a configuração: {0}"),
    (r"^firmware v(.+) predates manual control; those controls are disabled$",
     "o firmware v{0} é anterior ao controle manual; esses controles foram desativados"),
    (r"^device boot #(\d+) detected -- restarting sample stream$",
     "inicialização nº {0} detectada; reiniciando o fluxo de amostras"),
    (r"^gap of (\d+) sample\(s\) before seq (\d+); backfilling$",
     "lacuna de {0} amostras antes da sequência {1}; recuperando dados"),
    (r"^lost contact: (.+)$", "contato perdido: {0}"),
    (r"^new experiment '(.+)'$", "novo experimento '{0}'"),
    (r"^blanking sweep requested, paced at (.+)% LED duty \(about 3 min\)$",
     "varredura do branco solicitada, espaçada em {0}% de ciclo do LED "
     "(cerca de 3 min)"),
    (r"^blanking sweep requested \(fast, about 25 s\)$",
     "varredura do branco solicitada (rápida, cerca de 25 s)"),
    (r"^seeded from device memory: (\d+) sample\(s\)$",
     "iniciado com {0} amostra(s) da memória do dispositivo"),
    (r"^moved (\d+) sample\(s\) to (.+?)( \(left .+ in place\))?$",
     "{0} amostra(s) movidas para {1}"),
    (r"^previous file kept at (.+)$", "arquivo anterior mantido em {0}"),
    (r"^device memory cleared \((\d+) sample\(s\) discarded\)$",
     "memória do dispositivo limpa ({0} amostras descartadas)"),
    (r"^device memory not cleared -- the firmware may predate the "
     r"clear_history command$",
     "a memória do dispositivo não foi limpa; o firmware pode ser anterior ao "
     "comando clear_history"),
    (r"^run metadata incomplete: (.+)$",
     "metadados do ensaio incompletos: {0}"),
]


def translate_log(language: str, message: str) -> str:
    if normalize_language(language) != PT_BR or message.startswith("->"):
        return message
    for pattern, replacement in _LOG_PATTERNS:
        match = re.match(pattern, message)
        if match:
            return replacement.format(*match.groups())
    return message
