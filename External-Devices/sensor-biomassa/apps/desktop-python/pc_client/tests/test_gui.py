"""
Drives the real MainWindow against a simulated v4.1 device, offscreen.

Catches the things that only show up when the widgets are actually built and
clicked: bad enum names, signal/slot mismatches, guard-flag bugs that make the
UI echo device state back at the device, and stylesheet format errors.
"""
import json
import math
import os
import sys
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import tempfile

import numpy as np

from biomass_core import Transport

fails = []


def check(cond, msg):
    print(("  PASS  " if cond else "  FAIL  ") + msg)
    if not cond:
        fails.append(msg)


class FakeDevice(Transport):
    """A v5.0 device: full status surface, records every command it gets."""
    name = "fake"

    def __init__(self, fw="5.0"):
        self.fw = fw
        self.seq = 0
        self.t_ms = 0
        self.ring = []
        self.commands = []
        self.state = "measuring"
        self.auto_range = True
        self.it_index = 2
        self.pwm_index = 4
        self.pwm_table = [2.0, 3.5, 6.0, 10.5, 18.0, 32.0, 57.0, 100.0]
        self.it_table = [100, 200, 400, 800]
        self.low, self.opt, self.high = 10000, 25000, 40000
        self.ema = 0.8
        self.refresh_ms = 25000
        # Thermal floor the firmware reports at IT 800 ms:
        # (10 + 2 * (1.20 * 800 + 8)) / 0.08.
        self.min_refresh_ms = 24325
        self.boundary_misses = 0
        self.soc_temp_c = 53.8
        self.blank_stamp = 1
        self.blank_scale = 1000
        self.blank_duty = -1.0      # -1: swept before this session

    def tick(self, n=1):
        for _ in range(n):
            self.seq += 1
            self.t_ms += self.refresh_ms
            a = 0.05 * math.exp(0.02 * self.seq)
            self.ring.append({
                "seq": self.seq, "t_ms": self.t_ms, "absorbance": round(a, 3),
                "raw": int(47000 * 10 ** -a), "i0": 47000,
                "it_ms": self.it_table[self.it_index],
                "pwm_pct": self.pwm_table[self.pwm_index],
                "hd_mode": False, "sat": False,
                "single": self.seq % 17 == 0, "manual": not self.auto_range,
            })

    def open(self): pass
    def close(self): pass

    def get_status(self):
        return {
            "fw": self.fw, "name": "fake", "boot_id": 2, "uptime_ms": self.t_ms,
            "state": self.state, "seq": self.seq, "blank_done": True,
            "hd_mode": False, "refresh_ms": self.refresh_ms,
            "low": self.low, "high": self.high, "opt": self.opt,
            "hub_enabled": False, "hub_connected": False, "hub_ssid": "",
            "ap_ip": "192.168.7.1", "ap_clients": 1, "sta_ip": "",
            "i2c_errors": 0, "saturation_events": 3, "sensor_resets": 0,
            "failed_searches": 0, "hist_size": 1024, "hist_stored": len(self.ring),
            "free_heap": 210000,
            "auto_range": self.auto_range, "it_index": self.it_index,
            "pwm_index": self.pwm_index,
            "it_ms": self.it_table[self.it_index],
            "pwm_pct": self.pwm_table[self.pwm_index],
            "led_duty": 0.0, "manual_led": False, "led_test": False,
            "test_period": 50, "ema": self.ema,
            "min_refresh_ms": self.min_refresh_ms,
            "led_duty_limit": 0.08,
            "it_table": list(self.it_table), "pwm_table": list(self.pwm_table),
            "boundary_misses": self.boundary_misses,
            "soc_temp_c": self.soc_temp_c,
        }

    def get_blank_table(self):
        return {"blank_done": True, "timestamp": self.blank_stamp,
                "sweep_duty_pct": self.blank_duty, "sweep_ms": 24000,
                "it_ms": self.it_table, "pwm_pct": self.pwm_table,
                "i0": [[self.blank_scale * (i + 1) * (j + 1) for j in range(8)]
                       for i in range(4)]}

    def reblank(self, duty=0.0):
        """A sweep lands a different table, as a real re-zero would."""
        self.blank_stamp += 1000
        self.blank_scale += 7
        self.blank_duty = duty

    def get_history(self, since):
        newer = [s for s in self.ring if s["seq"] > since]
        batch = newer[:60]
        return {"boot_id": 2, "seq": self.seq,
                "first_seq": self.ring[0]["seq"] if self.ring else 1,
                "samples": batch, "count": len(batch), "more": len(newer) > 60}

    def send_command(self, payload):
        self.commands.append(payload)
        # emulate the device acting on it
        if payload.get("command") == "manual":
            self.auto_range = False
        elif payload.get("command") == "auto":
            self.auto_range = True
        elif payload.get("command") == "set_gear":
            self.it_index, self.pwm_index = payload["it"], payload["pwm"]
        elif payload.get("command") == "blank":
            # A completed sweep always writes a new table and stamps it. Tests
            # that mean to simulate an aborted one simply do not send this.
            self.reblank(float(payload.get("duty_pct", 0.0)))
        if "refresh_ms" in payload:
            self.refresh_ms = payload["refresh_ms"]
        if "ema" in payload:
            self.ema = payload["ema"]
        if "low" in payload:
            self.low, self.opt, self.high = (payload["low"], payload["opt"],
                                             payload["high"])

    def sent(self, name):
        return [c for c in self.commands if c.get("command") == name]


def main():
    from PySide6 import QtCore, QtWidgets
    import biomass_gui

    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])
    tmp = Path(tempfile.mkdtemp())
    outdir = tmp / ("very_long_output_folder_name_" * 3)

    print("[1] window builds and primes")
    dev = FakeDevice()
    dev.tick(120)
    win = biomass_gui.MainWindow(dev, outdir, "guitest", interval=10.0,
                                 autosave=True)
    win.show()
    app.processEvents()
    check(biomass_gui.pg.getConfigOption("antialias") is True,
          "plot antialiasing is enabled")
    check(win.curveA.opts.get("antialias") is True,
          "absorbance curve requests antialiasing")
    check(win.curveRaw.opts.get("antialias") is True,
          "raw-intensity curve requests antialiasing")
    check(not win.statusBar().isSizeGripEnabled(),
          "the native status-bar pixel grip is disabled")
    app_style = QtWidgets.QApplication.instance().styleSheet()
    check("QScrollBar::add-page:vertical" in app_style and
          "QScrollBar::up-arrow:vertical" in app_style,
          "scrollbar pages and arrows are fully themed")
    check(win.xs.size == 120, f"plotted {win.xs.size} samples, want 120")
    check(win.client.recorder is not None, "auto-started recording")
    check(win.client.recorder.count == 120, "recorder wrote the backfill")

    print("\n[1b] frontend structure fits the minimum supported window")
    win.resize(win.minimumSize())
    app.processEvents()
    check(win.sidebarTabs.count() == 4, "four task-oriented sidebar tabs")
    check([win.sidebarTabs.tabText(i) for i in range(4)] ==
          ["Run", "Optics", "Ranging", "Advanced"],
          "sidebar tab order follows the operator workflow")
    check(win.sidebarTabs.width() >= 460,
          f"sidebar keeps its 460 px control width ({win.sidebarTabs.width()} px)")
    overflow = []
    controls_inside = True
    for i in range(win.sidebarTabs.count()):
        win.sidebarTabs.setCurrentIndex(i)
        app.processEvents()
        area = win.sidebarTabs.widget(i)
        inner = area.widget()
        if area.horizontalScrollBar().maximum() != 0:
            overflow.append((win.sidebarTabs.tabText(i),
                             area.horizontalScrollBar().maximum(),
                             inner.minimumSizeHint().width(),
                             area.viewport().width()))
        for button in inner.findChildren(QtWidgets.QPushButton):
            if not button.isVisibleTo(inner):
                continue
            right = button.mapTo(inner, button.rect().bottomRight()).x()
            controls_inside &= right <= inner.width() + 1
    check(not overflow, f"sidebar tabs need no horizontal scrolling: {overflow}")
    check(controls_inside, "visible action buttons stay inside their tab")
    left = win.mainSplitter.widget(0)
    left_buttons = win._span_buttons + [win.pauseBtn, win.autoscaleBtn,
                                        win.exportImageBtn]
    left_inside = all(
        button.mapTo(left, button.rect().bottomRight()).x() <= left.width() + 1
        for button in left_buttons
    )
    check(left_inside and left.width() >= 780,
          f"plot action bar fits the minimum supported window ({left.width()} px)")
    central = win.centralWidget()
    header_inside = all(
        button.mapTo(central, button.rect().bottomRight()).x() <= central.width() + 1
        for button in (win.themeBtn, win.languageBtn, win.connBtn)
    )
    check(header_inside,
          f"theme, language, and connection controls fit the header: "
          f"language {win.languageBtn.width()}, central {central.width()}")
    check(win._span_buttons[-1].text() == "15 min" and
          win._span_buttons[-1].width() >= 74,
          "15-minute plot span label has enough width")
    check(win.dirLabel.toolTip() == str(outdir),
          "full output directory is retained in a tooltip")
    check(len(win.dirLabel.text()) <= 64,
          "long output directory is shortened in the panel")
    check(win.recInfo.toolTip().endswith("data.csv"),
          "full active CSV path is retained in a tooltip")
    # Lower bound is the device-reported thermal floor, not a constant.
    check(win.sInterval.box.minimum() >= 10.0 and
          win.sInterval.box.maximum() == 3600.0,
          f"interval range follows the device floor "
          f"({win.sInterval.box.minimum()}-{win.sInterval.box.maximum()} s)")
    check(not win.bStart.isEnabled() and win.bStop.isEnabled(),
          "measurement buttons reflect the measuring state")
    check(not win.sLED.slider.isEnabled() and not win.testChk.isEnabled(),
          "idle-only optical controls are locked while measuring")
    win.resize(1420, 900)
    win.sidebarTabs.setCurrentIndex(0)
    app.processEvents()
    screenshot = os.environ.get("BIOMASS_GUI_SCREENSHOT")
    if screenshot:
        check(win.grab().save(screenshot), f"saved UI screenshot to {screenshot}")

    print("\n[2] status bound to widgets (no echo back to device)")
    before = len(dev.commands)
    win._on_status(dev.get_status())
    app.processEvents()
    check(len(dev.commands) == before,
          f"syncing status sent {len(dev.commands) - before} commands, want 0")
    check(win.sLow.value() == 10000, f"low slider = {win.sLow.value()}")
    check(win.sOpt.value() == 25000, "optimal slider synced")
    check(win.sHigh.value() == 40000, "high slider synced")
    check(abs(win.sEma.value() - 0.8) < 1e-6, "EMA slider synced")
    check(abs(win.sInterval.value() - 25.0) < 0.05,
          f"interval slider = {win.sInterval.value()} s")
    check(win.autoChk.isChecked(), "auto-range checkbox synced")
    check(win.sIT.value() == 2, "IT slider index synced")
    check(win.sPWM.value() == 4, "PWM slider index synced")
    check(win.sIT.value_label.text() == "400 ms",
          f"IT label shows device table: {win.sIT.value_label.text()}")
    check(win.sPWM.value_label.text() == "18 %",
          f"PWM label shows device table: {win.sPWM.value_label.text()}")
    check(win.pwmBoxes[7].value() == 100.0, "advanced PWM table populated")
    check(win.itCombos[3].currentText() == "800 ms", "advanced IT table populated")

    print("\n[3] manual gear control")
    win.autoChk.setChecked(False)
    app.processEvents()
    check(dev.sent("manual"), "unchecking auto-range sent 'manual'")
    win.sIT.slider.setValue(3)
    win.sPWM.slider.setValue(6)
    for t in win._debounce.values():
        t.stop()
        t.timeout.emit()
    app.processEvents()
    gears = dev.sent("set_gear")
    check(gears, "gear change sent set_gear")
    check(gears[-1] == {"command": "set_gear", "it": 3, "pwm": 6},
          f"last set_gear = {gears[-1] if gears else None}")

    print("\n[4] thresholds validate before sending")
    n = len(dev.commands)
    win.sLow.box.setValue(50000)      # low > high: invalid
    win.sLow._on_box()
    app.processEvents()
    check(len(dev.commands) == n, "invalid ordering not sent to device")
    check("low < optimal < high" in win.threshHint.text(),
          "user told why it was rejected")
    win.sLow.box.setValue(8000)
    win.sLow._on_box()
    for t in win._debounce.values():
        t.stop()
        t.timeout.emit()
    app.processEvents()
    thr = [c for c in dev.commands if "low" in c]
    check(thr and thr[-1]["low"] == 8000, f"valid thresholds sent: {thr[-1:]}")

    print("\n[5] sliders that map to settings")
    win.sInterval.box.setValue(30)
    win.sInterval._on_box()
    app.processEvents()
    check(any(c.get("refresh_ms") == 30000 for c in dev.commands),
          "interval slider sent refresh_ms in ms")
    win.sEma.box.setValue(0.35)
    win.sEma._on_box()
    app.processEvents()
    check(any(abs(c.get("ema", 0) - 0.35) < 1e-6 for c in dev.commands),
          "EMA slider sent ema")
    dev.state = "idle"
    win._on_status(dev.get_status())
    app.processEvents()
    check(win.sLED.slider.isEnabled() and win.testChk.isEnabled(),
          "direct optical controls unlock in idle")
    win.sLED.box.setValue(42.5)
    win.sLED._on_box()
    app.processEvents()
    leds = dev.sent("led")
    check(leds and abs(leds[-1]["duty"] - 42.5) < 1e-6,
          f"LED slider sent duty: {leds[-1:]}")

    print("\n[6] buttons")
    win._cmd("stop")
    check(dev.sent("stop"), "stop sent")
    # read_once is IDLE-only in firmware, so the button must be disabled
    # while measuring and enabled once the device reports idle.
    dev.state = "measuring"
    win._on_status(dev.get_status())
    app.processEvents()
    check(not win.bOnce.isEnabled(), "single reading disabled while measuring")
    dev.state = "idle"
    win._on_status(dev.get_status())
    app.processEvents()
    check(win.bOnce.isEnabled(), "single reading enabled once idle")
    win.bOnce.click()
    app.processEvents()
    check(dev.sent("read_once"), "single reading sent")
    win.testChk.setChecked(True)
    app.processEvents()
    check(dev.sent("test_on"), "sweep toggle sent test_on")
    win._led_off()
    app.processEvents()
    check(dev.sent("led_off"), "LED off sent")
    check(not win.testChk.isChecked(), "LED off also released the sweep toggle")

    print("\n[6b] every latest-firmware action is wired")
    confirm = win._confirm
    ask_blank = win._ask_blank_mode
    win._confirm = lambda *a, **k: True
    win._ask_blank_mode = lambda paced: 0.0
    try:
        win.bBlank.click()
        win.bStart.click()
        win.hubChk.setChecked(True)
        win.hubChk.setChecked(False)
        win.resetHealthBtn.click()
        win.saveThresholdsBtn.click()
        win.reloadConfigBtn.click()
        win._set_pwm_level(0)
        win._set_it_slot(0)
        win.factoryResetBtn.click()
    finally:
        win._confirm = confirm
        win._ask_blank_mode = ask_blank
        win._await_blank = False
    app.processEvents()
    for command in ("blank", "start", "hub_on", "hub_off", "reset_health",
                    "save_config", "load_config", "set_pwm", "set_it", "factory"):
        check(dev.sent(command), f"{command} action sent")

    print("\n[7] plot spans and pause")
    win._set_span(900, win._span_buttons[3])
    app.processEvents()
    check(win._span_s == 900, "span applied")
    check("of 120 points" in win.npts.text(), f"point counter: {win.npts.text()}")
    win._set_span(0, win._span_buttons[0])
    win.pauseBtn.setChecked(True)
    n_before = win.xs.size
    dev.tick(5)
    win.client._last_status_poll = 0
    win._tick()
    app.processEvents()
    check(win.xs.size == n_before + 5, "data still ingested while paused")
    win.pauseBtn.setChecked(False)
    app.processEvents()

    print("\n[8] single-shot samples are marked on the plot")
    check(len(win.singles) > 0, f"{len(win.singles)} single-shot markers")

    print("\n[9] notes land in metadata")
    win.noteEdit.setText("fed 5 mL glucose")
    win._add_note()
    app.processEvents()

    meta = json.loads(win.client.recorder.meta_path.read_text(encoding="utf-8"))
    check("operator_notes" in meta, "note written to meta.json")
    check(meta["operator_notes"][0]["text"] == "fed 5 mL glucose",
          "note text preserved")
    check("seq" in meta["operator_notes"][0], "note carries the sample number")
    check(win.noteEdit.text() == "", "note field cleared")

    print("\n[10] recording toggle")
    path = win.client.recorder.csv_path
    win.recBtn.setChecked(False)
    app.processEvents()
    check(win.client.recorder is None, "recording stopped")
    check(path.exists(), "CSV still on disk")
    rows = path.read_text(encoding="utf-8").strip().splitlines()
    check(rows[0].endswith("single,manual,valid"),
          f"CSV header has the new columns: {rows[0][-30:]}")
    ncol = len(rows[0].split(","))
    check(all(len(r.split(",")) == ncol for r in rows[1:]), "CSV rows aligned")
    win.nameEdit.setText("second_run")
    win.recBtn.setChecked(True)
    app.processEvents()
    check(win.client.recorder is not None, "second run started")
    check("second_run" in str(win.client.recorder.csv_path),
          f"new run folder used the new name: {win.client.recorder.csv_path}")

    print("\n[11] theme toggle")
    win._toggle_theme()
    app.processEvents()
    check(win.theme_name == "light", "switched to light")
    win._toggle_theme()
    app.processEvents()
    check(win.theme_name == "dark", "switched back to dark")

    print("\n[11b] complete Brazilian Portuguese runtime translation")
    commands_before_language = len(dev.commands)
    win.languageBtn.click()
    app.processEvents()
    check(win.language == "pt-BR" and win.languageBtn.text() == "English",
          "language button switches the live window to Português (Brasil)")
    check([win.sidebarTabs.tabText(i) for i in range(4)] ==
          ["Ensaio", "Óptica", "Faixa automática", "Avançado"],
          "all navigation tabs translated")
    check(win.bBlank.text() == "Branco" and win.bStart.text() == "Iniciar" and
          win.recBtn.text() == "Parar gravação",
          "measurement and recording actions translated")
    check(win.noteEdit.placeholderText().startswith("nota (ex.:") and
          win.autoChk.text() == "Ajuste automático de faixa",
          "field guidance and optical controls translated")
    group_titles = [group.title() for group in
                    win.findChildren(QtWidgets.QGroupBox)]
    check("MEDIÇÃO" in group_titles and "CALIBRAÇÃO DO BRANCO" in group_titles,
          "group titles translated across Run and Advanced tabs")
    check(win.p1.getAxis("left").labelText == "Absorbância (UA)" and
          win.p2.getAxis("bottom").labelText == "tempo decorrido (h)",
          "plot axes translated")
    check(" de " in win.npts.text() and " pontos" in win.npts.text() and
          "erros I2C" in win.healthLabel.text(),
          f"live counters and device health translated: "
          f"{win.npts.text()!r}, {win.healthLabel.text()!r}")
    check("conectado por" in win.logBox.toPlainText() and
          "amostras" in win.statusBar().currentMessage(),
          "existing logs and recording status re-rendered in Portuguese")
    captured_confirmations = []
    original_confirm = win._confirm
    win._confirm = lambda title, message, warning=False: (
        captured_confirmations.append((title, message, warning)) or False)
    win._factory_reset()
    win._confirm = original_confirm
    check(captured_confirmations[0][0] == "Restaurar padrão de fábrica",
          "confirmation titles and safety warnings translated")
    # The sweep prompt is its own dialog rather than a yes/no confirm, since
    # it offers two ways to run. Its text still has to be translated.
    check(win._t("Run blanking sweep") == "Executar varredura do branco" and
          "meio límpido" in win._t("blank_confirm") and
          "3 min" in win._t("blank_confirm") and
          win._t("Slow sweep") == "Varredura lenta",
          "sweep prompt translated, both modes named")
    check(win.sEma.box.locale().decimalPoint() == ",",
          "numeric controls use the Brazilian decimal separator")
    check(len(dev.commands) == commands_before_language,
          "changing language sends no command to the device")

    win.resize(win.minimumSize())
    app.processEvents()
    pt_overflow = []
    for i in range(win.sidebarTabs.count()):
        win.sidebarTabs.setCurrentIndex(i)
        app.processEvents()
        area = win.sidebarTabs.widget(i)
        if area.horizontalScrollBar().maximum() != 0:
            pt_overflow.append((win.sidebarTabs.tabText(i),
                                area.horizontalScrollBar().maximum(),
                                area.widget().minimumSizeHint().width(),
                                area.viewport().width()))
    left = win.mainSplitter.widget(0)
    left_inside = all(
        button.mapTo(left, button.rect().bottomRight()).x() <= left.width() + 1
        for button in win._span_buttons +
        [win.pauseBtn, win.autoscaleBtn, win.exportImageBtn]
    )
    check(not pt_overflow and left_inside and left.width() >= 780,
          f"Portuguese layout fits the minimum window: {pt_overflow}")
    win.resize(1420, 900)
    win.languageBtn.click()
    app.processEvents()
    check(win.language == "en" and win.bStart.text() == "Start",
          "language button restores the complete English interface")
    dlg_language = biomass_gui.ConnectDialog(language="en")
    dlg_language.languageBtn.click()
    app.processEvents()
    check(dlg_language.windowTitle() == "Conectar ao sensor" and
          dlg_language.mode.itemText(0) == "USB serial (recomendado)" and
          dlg_language.ok_btn.text() == "Conectar",
          "connection dialog also switches completely to Portuguese")
    dlg_language.close()

    print("\n[12] blank table viewer builds")
    QtCore.QTimer.singleShot(80, lambda: [
        w.close() for w in app.topLevelWidgets()
        if isinstance(w, QtWidgets.QDialog) and w.isVisible()])
    win._show_blank()
    app.processEvents()
    check(True, "blank table dialog opened and closed without error")

    print("\n[13] old firmware degrades gracefully")
    old = FakeDevice(fw="4.0")
    old.tick(3)
    win2 = biomass_gui.MainWindow(old, tmp, "oldfw", interval=10.0,
                                  autosave=False)
    win2.show()
    app.processEvents()
    old.state = "idle"
    st = old.get_status()
    for k in ("auto_range", "it_index", "pwm_index", "it_table", "pwm_table",
              "ema", "led_duty"):
        st.pop(k, None)          # v4.0 does not report these
    win2._on_status(st)
    app.processEvents()
    check(not win2.sIT.slider.isEnabled(), "manual gear disabled on v4.0")
    check(not win2.autoChk.isEnabled(), "auto-range toggle disabled on v4.0")
    check(not win2.bOnce.isEnabled(), "single reading disabled on v4.0")
    check(not win2.sLED.slider.isEnabled(), "direct LED disabled on v4.0")
    check("4.1" in win2.fwWarn.text(), "explains which firmware is needed")
    # isVisible() is False for anything on a non-active tab, so isHidden()
    # is what actually tells us the widget was not explicitly hidden.
    check(not win2.fwWarn.isHidden(), "firmware warning is not hidden")
    # The gear-lock note must not clobber the capability warning: both are
    # written during the same _on_status pass.
    full = old.get_status()
    win2._on_status(full)
    app.processEvents()
    check("4.1" in win2.fwWarn.text(),
          "capability warning survives a status carrying auto_range")
    check(not win2.bOnce.isEnabled(),
          "repeated idle status cannot re-enable unsupported controls")
    win2.close()

    print("\n[13b] persistent demo mode is clearly identified")
    from biomass_demo import DemoTransport
    demo = DemoTransport()
    demo.open()
    win3 = biomass_gui.MainWindow(demo, tmp, "demo", interval=10.0,
                                  autosave=False)
    win3.show()
    app.processEvents()
    check("[DEMO]" in win3.windowTitle(), "window title marks simulated data")
    check("SIMULATED" in win3.devinfo.text(), "header marks simulated firmware")
    check(win3.xs.size == 120, "demo window opens with a populated plot")
    check(win3.bStart.isEnabled(), "demo is ready for the user to press Start")
    win3.bStart.click()
    win3.client._last_status_poll = 0
    win3._tick()
    app.processEvents()
    check(demo.state == "measuring" and "DEMO / measuring" in win3.chip.text(),
          "demo Start action enters live measurement mode")
    win3.close()

    print("\n[15] clear plot keeps the recorded file intact")
    dev2 = FakeDevice()
    dev2.tick(40)
    win4 = biomass_gui.MainWindow(dev2, tmp, "cleartest", interval=999,
                                  autosave=True)
    win4.show()
    app.processEvents()
    check(win4.xs.size == 40, f"plotted {win4.xs.size}, want 40")
    rec_path = win4.client.recorder.csv_path
    rows_before = len(rec_path.read_text(encoding="utf-8").strip().splitlines())
    win4._reset_plot_data()          # bypass the confirm dialog
    app.processEvents()
    check(win4.xs.size == 0, "plot arrays emptied")
    check(win4.t0 is None, "elapsed clock restarted")
    check(win4.client.samples == [], "client sample list released")
    check(win4.bigAbs.text() == "--", "readout reset")
    check(win4.client.recorder is not None, "still recording after clear")
    rows_after = len(rec_path.read_text(encoding="utf-8").strip().splitlines())
    check(rows_after == rows_before, "CSV untouched by a plot clear")
    # New data after a clear must start at elapsed 0, not hours in.
    dev2.tick(3)
    win4.client._last_status_poll = 0
    win4._tick()
    app.processEvents()
    check(win4.xs.size == 3, f"{win4.xs.size} new points after clear, want 3")
    check(win4.xs[0] == 0.0, f"new run starts at elapsed 0, got {win4.xs[0]}")

    print("\n[16] new experiment: fresh file, cleared plot, blank then start")
    old_csv = win4.client.recorder.csv_path
    dev2.commands.clear()
    win4.nameEdit.setText("exp_two")
    # Drive the dialog result directly; the dialog itself is covered in [17].
    win4.outdir = tmp
    win4._cmd("stop")
    win4._stop_recording()
    win4._reset_plot_data()
    win4.run_name = "exp_two"
    win4._start_recording()
    win4._begin_blank_wait(start_after=True)
    win4._cmd("blank")
    app.processEvents()
    new_csv = win4.client.recorder.csv_path
    check(new_csv != old_csv, "new experiment writes to a different file")
    check("exp_two" in str(new_csv), f"new folder named for the run: {new_csv}")
    check(old_csv.exists(), "previous experiment's file still on disk")
    check(dev2.sent("blank"), "blanking sweep requested")

    # The device may still report "idle" between receiving the command and
    # acting on it (over HTTP the blank is deferred to the main loop). That
    # window must not be mistaken for a finished sweep -- blank_done is
    # already True from the previous calibration.
    dev2.state = "idle"
    win4._service_pending_experiment(dev2.get_status())
    app.processEvents()
    check(not dev2.sent("start"),
          "idle-before-sweep-starts is not mistaken for completion")
    check(win4._await_blank, "still waiting through the command latency")

    # While the device is sweeping, we must not fire start early.
    dev2.state = "blanking"
    win4._service_pending_experiment(dev2.get_status())
    app.processEvents()
    check(not dev2.sent("start"), "start withheld while blanking")
    check(win4._await_blank, "still waiting for the sweep")
    # Sweep finishes -> start fires.
    dev2.state = "idle"
    win4._service_pending_experiment(dev2.get_status())
    app.processEvents()
    check(not win4._await_blank, "wait cleared once idle")
    QtCore.QTimer.singleShot(0, lambda: None)
    for _ in range(3):
        app.processEvents()
        QtCore.QThread.msleep(150)
        app.processEvents()
    check(dev2.sent("start"), "measurement started after the sweep completed")

    print("\n[17] new experiment dialog")
    dlg = biomass_gui.NewExperimentDialog(win4, tmp, "dialog_run")
    outdir, name, do_blank, do_start, seed, duty = dlg.result_values()
    check(name == "dialog_run", f"name defaulted from the run: {name}")
    check(Path(outdir) == tmp, "folder defaulted to the current output dir")
    check(do_blank and do_start, "zero + auto-start checked by default")
    check(not seed, "a new experiment starts now, not from the device buffer")
    check(not dlg.seedChk.isEnabled(),
          "seeding is offered only when the device holds something")
    check(duty == biomass_gui.SLOW_BLANK_DUTY_PCT,
          f"the paced sweep is the default where it is supported, got {duty}")
    dlg.blankMode.setCurrentIndex(0)
    check(dlg.result_values()[5] == 0.0,
          "the fast back-to-back sweep can still be chosen")
    dlg.nameEdit.setText("  ")
    _, name2, _, _, _, _ = dlg.result_values()
    check(name2 == "run", f"blank name falls back to 'run', got {name2!r}")
    dlg.blankChk.setChecked(False)
    _, _, b3, _, _, _ = dlg.result_values()
    check(not b3, "zero can be declined")
    check(not dlg.blankMode.isEnabled(),
          "the sweep mode is dead while no sweep is asked for")
    dlg.close()

    dlg2 = biomass_gui.NewExperimentDialog(win4, tmp, "seeded", stored=37)
    check(dlg2.seedChk.isEnabled() and "37" in dlg2.seedChk.text(),
          f"seed option names the count: {dlg2.seedChk.text()!r}")
    dlg2.seedChk.setChecked(True)
    check(dlg2.result_values()[4], "seeding can be requested")
    dlg2.close()

    # Firmware older than 5.2 cannot pace a sweep; the option must be dead
    # rather than sending a duty the device will ignore.
    dlg3 = biomass_gui.NewExperimentDialog(win4, tmp, "old_fw",
                                           paced_blank=False)
    check(not dlg3.blankMode.isEnabled(),
          "pacing is offered only on firmware that has it")
    check(dlg3.result_values()[5] == 0.0,
          "and an unpaced sweep is what gets asked for")
    dlg3.close()

    print("\n[18] blank viewer is themed and flags saturation")
    sat_dev = FakeDevice()
    sat_dev.tick(5)
    # Saturate the brightest gears, as a real blanking sweep does.
    def sat_blank():
        rows = [[1000 * (i + 1) * (j + 1) for j in range(8)] for i in range(4)]
        rows[3][6] = rows[3][7] = rows[2][7] = 65535
        return {"blank_done": True, "timestamp": 1,
                "it_ms": sat_dev.it_table, "pwm_pct": sat_dev.pwm_table,
                "i0": rows}
    sat_dev.get_blank_table = sat_blank
    win5 = biomass_gui.MainWindow(sat_dev, tmp, "sat", interval=999,
                                  autosave=False)
    win5.show()
    app.processEvents()
    opened = {}
    def grab_dialog():
        for w in app.topLevelWidgets():
            if isinstance(w, QtWidgets.QDialog) and w.isVisible() \
                    and w is not win5:
                tbl = w.findChild(QtWidgets.QTableWidget)
                if tbl is not None:
                    # Snapshot values now: the widget dies with the dialog.
                    opened["rows"] = tbl.rowCount()
                    opened["cols"] = tbl.columnCount()
                    opened["read_only"] = (
                        tbl.editTriggers() ==
                        QtWidgets.QAbstractItemView.EditTrigger.NoEditTriggers)
                    opened["texts"] = [tbl.item(i, j).text()
                                       for i in range(tbl.rowCount())
                                       for j in range(tbl.columnCount())]
                w.close()
    QtCore.QTimer.singleShot(60, grab_dialog)
    win5._show_blank()
    app.processEvents()
    check("texts" in opened, "blank dialog contained a table")
    if "texts" in opened:
        check(opened["rows"] == 4 and opened["cols"] == 8, "4x8 gear grid")
        n_sat = sum(1 for t in opened["texts"] if t == "sat.")
        check(n_sat == 3, f"3 saturated cells labelled, got {n_sat}")
        check(opened["read_only"], "table is read-only")
        check("65535" not in opened["texts"],
              "saturated cells show 'sat.', never the raw 65535 marker")
        check("24 000" in opened["texts"],
              f"thousands grouped for readability; sample: "
              f"{opened['texts'][:3]}")
    win5.close()

    print("\n[19b] sampling interval respects the LED thermal floor")
    dev3 = FakeDevice()
    dev3.tick(5)
    win6 = biomass_gui.MainWindow(dev3, tmp, "floor", interval=999,
                                  autosave=False)
    win6.show()
    app.processEvents()
    win6._on_status(dev3.get_status())
    app.processEvents()
    # Whole seconds, rounded UP: a raw floor of 24.325 s must present as 25,
    # never 24 -- the control must not offer a value the device will clamp.
    check(win6.sInterval.resolution == 1.0,
          f"interval is a whole-second control, "
          f"resolution {win6.sInterval.resolution}")
    check(win6.sInterval.box.minimum() == 25,
          f"floor rounded up to whole seconds, got "
          f"{win6.sInterval.box.minimum()}")
    check("25" in win6.intervalHint.text(),
          f"floor explained to the user: {win6.intervalHint.text()[:60]}")
    check("8" in win6.intervalHint.text(), "duty limit quoted in the hint")
    # The control must not offer a value the firmware would clamp.
    win6.sInterval.box.setValue(1.0)
    check(win6.sInterval.box.value() >= 25,
          f"cannot select below the floor, got {win6.sInterval.box.value()}")
    # Locking a short IT lowers the floor; the control must follow.
    n_before = len(dev3.commands)
    st2 = dev3.get_status()
    st2["auto_range"] = False
    st2["min_refresh_ms"] = 3325     # (10 + 2 * (1.20 * 100 + 8)) / 0.08
    win6._on_status(st2)
    app.processEvents()
    check(win6.sInterval.box.minimum() == 4,
          f"floor drops with a locked short IT, got "
          f"{win6.sInterval.box.minimum()}")
    check(len(dev3.commands) == n_before,
          "changing the floor sent nothing back to the device")
    win6.close()

    print("\n[19] recommended LED ladder button")
    dev2.commands.clear()
    win4._restore_pwm_preset  # exists
    QtCore.QTimer.singleShot(60, lambda: [
        w.close() for w in app.topLevelWidgets()
        if isinstance(w, QtWidgets.QMessageBox) and w.isVisible()])
    win4.bPwmPreset.click()
    app.processEvents()
    check(True, "preset button is wired and cancels cleanly")
    win4.close()

    print("\n[20] v5.0 status surface")
    dev5 = FakeDevice()
    dev5.tick(3)
    win7 = biomass_gui.MainWindow(dev5, tmp, "v5", interval=10.0,
                                  autosave=False)
    win7._on_status(dev5.get_status())
    app.processEvents()
    check("53.8" in win7.healthLabel.text() or "54" in win7.healthLabel.text(),
          f"SoC temperature shown in health: {win7.healthLabel.text()[-24:]!r}")
    # isHidden(), not isVisible(): the window is never shown in this test, so
    # isVisible() is False for every child regardless. isHidden() reports the
    # explicit setVisible() state, which is what the code under test sets.
    # Zero misses is the normal case and must not nag.
    check(win7.boundaryWarn.isHidden(),
          "no anchoring warning when every read anchored")
    check(not win7.probeBtn.isHidden(),
          "conversion-period diagnostic offered on v5.0")

    # A read that fell back to a blind wait is a correctness signal: it must
    # surface, not hide inside the counters line.
    st5 = dev5.get_status()
    st5["boundary_misses"] = 4
    win7._on_status(st5)
    app.processEvents()
    check(not win7.boundaryWarn.isHidden() and "4" in win7.boundaryWarn.text(),
          f"anchoring failures warned about: {win7.boundaryWarn.text()[:50]!r}")

    # The diagnostic needs an idle device.
    st5["boundary_misses"] = 0
    st5["state"] = "measuring"
    win7._on_status(st5)
    app.processEvents()
    check(not win7.probeBtn.isEnabled(),
          "diagnostic disabled while the device is measuring")

    # Older firmware has none of this; the widgets must stay out of the way
    # rather than report a reassuring zero the device never sent.
    old5 = FakeDevice(fw="4.6")
    st_old = old5.get_status()
    for k in ("boundary_misses", "soc_temp_c"):
        st_old.pop(k, None)
    win7._on_status(st_old)
    app.processEvents()
    check(win7.probeBtn.isHidden(),
          "diagnostic hidden on firmware that lacks probe_period")
    check(win7.boundaryWarn.isHidden(),
          "no anchoring warning when the device does not report the counter")
    check("--" in win7.healthLabel.text(),
          "missing SoC temperature shown as unknown, not as a number")
    win7.close()

    print("\n[21] blank viewer flags cells the firmware will not use")
    # A pre-v4.6 blank left rows of 9..109 counts, and "non-zero" used to be
    # the whole validity test. The viewer must mark them, or it shows a gear
    # as usable that the device refuses to select.
    dev6 = FakeDevice()
    dev6.get_blank_table = lambda: {
        "blank_done": True, "timestamp": 1,
        "it_ms": dev6.it_table, "pwm_pct": dev6.pwm_table,
        "i0": [[1000 * (j + 1) for j in range(8)],
               [9, 8, 11, 15, 0, 29, 46, 21],      # measured the dark
               [5000] * 8,
               [65535] * 8]}                        # saturated
    win8 = biomass_gui.MainWindow(dev6, tmp, "blank5", interval=10.0,
                                  autosave=False)
    win8._on_status(dev6.get_status())
    QtCore.QTimer.singleShot(120, lambda: [
        w.close() for w in app.topLevelWidgets()
        if isinstance(w, QtWidgets.QDialog) and w.isVisible()])
    dlg_tables = []
    orig_exec = QtWidgets.QDialog.exec

    def _capture(self):
        dlg_tables.extend(self.findChildren(QtWidgets.QTableWidget))
        return 0
    QtWidgets.QDialog.exec = _capture
    try:
        win8._show_blank()
    finally:
        QtWidgets.QDialog.exec = orig_exec
    app.processEvents()
    if dlg_tables:
        tbl = dlg_tables[0]
        dim_cells = [tbl.item(1, j).text() for j in range(8)]
        check(all(t not in ("", None) for t in dim_cells),
              "near-dark cells still show their value")
        tips = [tbl.item(1, j).toolTip() for j in range(8)]
        check(all(t for t in tips),
              "every near-dark cell explains why it is unusable")
        check(all(tbl.item(3, j).text() == win8._t("sat.") for j in range(8)),
              "saturated row still labelled sat.")
    else:
        check(False, "blank dialog exposed a table")
    win8.close()

    print("\n[22] experiment banner reports the run")
    dev7 = FakeDevice()
    dev7.tick(2)
    win9 = biomass_gui.MainWindow(dev7, tmp, "banner", interval=999,
                                  autosave=False)
    check(win9.expStatus.text() and "banner" not in win9.expStatus.text(),
          f"idle banner says nothing is recording: {win9.expStatus.text()!r}")
    win9.nameEdit.setText("DTR_01")
    win9._start_recording()
    app.processEvents()
    check("DTR_01" in win9.expStatus.text(),
          f"running banner names the experiment: {win9.expStatus.text()!r}")
    # Derived from the recorder, so it cannot disagree with what is written.
    check(str(win9.client.recorder.csv_path) == win9.expStatus.toolTip(),
          "banner points at the file actually being written")
    win9._stop_recording()
    app.processEvents()
    check("DTR_01" not in win9.expStatus.text(),
          f"banner clears when recording stops: {win9.expStatus.text()!r}")
    # The two cards that define a run must sit together, ahead of MEASUREMENT.
    # Read the LAYOUT order, not findChildren() order: MEASUREMENT is
    # constructed before RECORDING and inserted after it, so creation order
    # would report a layout that is not what the user sees.
    tabw = win9.findChild(QtWidgets.QTabWidget)
    page = tabw.widget(0)
    inner = page.widget() if isinstance(page, QtWidgets.QScrollArea) else page
    lay9 = inner.layout()
    order = []
    for i in range(lay9.count()):
        item = lay9.itemAt(i).widget()
        if isinstance(item, QtWidgets.QGroupBox):
            order.append(item.title())
    check(order[:2] == ["EXPERIMENT", "MEASUREMENT"],
          f"one card owns the run, ahead of MEASUREMENT, got {order}")
    # Stop recording belongs under New experiment and its explanation, not
    # in a card of its own where it reads as an unrelated action.
    expLay = None
    for child in inner.findChildren(QtWidgets.QGroupBox):
        if child.title() == "EXPERIMENT":
            expLay = child.layout()
    widgets = [expLay.itemAt(i).widget() for i in range(expLay.count())]
    order9 = [w for w in widgets if w is not None]
    check(win9.bNewExp in order9 and win9.recBtn in order9,
          "both run actions live in the EXPERIMENT card")
    check(order9.index(win9.recBtn) - order9.index(win9.bNewExp) == 2,
          "record toggle sits below New experiment and its explanation")
    owner = win9.nameEdit.parent()
    check(isinstance(owner, QtWidgets.QGroupBox) and owner.title() == "EXPERIMENT",
          f"the run name lives in the same card, not its own: "
          f"{getattr(owner, 'title', lambda: owner)()}")
    win9.close()

    print("\n[23] opening the app records nothing until asked")
    dev8 = FakeDevice()
    dev8.tick(12)
    quiet = tmp / "quiet"
    win10 = biomass_gui.MainWindow(dev8, quiet, "quiet", interval=999)
    win10.show()
    app.processEvents()
    check(win10.client.recorder is None,
          "no recording started merely by opening the window")
    made = sorted(p.name for p in quiet.iterdir()) if quiet.exists() else []
    check(not made, f"no run folder created on launch, found {made}")
    check(win10.xs.size == 12,
          f"device buffer is still plotted ({win10.xs.size} points)")
    check("not recording" in win10.recInfo.text().lower(),
          f"and says so: {win10.recInfo.text()!r}")

    print("\n[24] renaming a live run moves the file")
    win10.nameEdit.setText("first_name")
    win10._start_recording()
    dev8.tick(3)
    win10.client._last_status_poll = 0
    win10._tick()
    app.processEvents()
    first_csv = win10.client.recorder.csv_path
    written = win10.client.recorder.count
    # Starting a recording by hand begins now. The 12 samples already on the
    # plot came out of the device buffer before this file existed; New
    # experiment is what offers to include them.
    check(written == 3, f"recorded {written} samples since the file opened, want 3")

    win10._ask_relocate = lambda count: "move"       # answer the warning
    win10.nameEdit.setText("renamed_run")
    win10._on_run_name_edited()
    app.processEvents()
    moved_csv = win10.client.recorder.csv_path
    check("renamed_run" in str(moved_csv),
          f"the run followed its new name: {moved_csv}")
    check(win10.client.recorder.count == written,
          f"carried {win10.client.recorder.count} rows across, want {written}")
    rows = moved_csv.read_text(encoding="utf-8").strip().splitlines()
    check(len(rows) == written + 1, f"{len(rows)} lines in the moved file")
    check(not first_csv.exists(),
          "the folder it was renamed away from is gone, not left as a stub")
    meta = json.loads((moved_csv.parent / "meta.json").read_text(encoding="utf-8"))
    check(meta.get("moved_samples") == written, "the move is recorded in meta")
    check("time_anchor_epoch" in meta, "the moved run kept its time base")

    win10._ask_relocate = lambda count: "new"
    kept = win10.client.recorder.csv_path
    win10.nameEdit.setText("third_name")
    win10._on_run_name_edited()
    app.processEvents()
    check(win10.client.recorder.count == 0, "starting a new file leaves it empty")
    check(kept.exists(), "and leaves the previous file where it was")

    win10._ask_relocate = lambda count: "cancel"
    win10.nameEdit.setText("typo")
    win10._on_run_name_edited()
    app.processEvents()
    check(win10.run_name == "third_name" and win10.nameEdit.text() == "third_name",
          f"cancelling puts the name back: {win10.nameEdit.text()!r}")

    print("\n[24b] the log filter hides the chatter, never the failures")
    win10._log_entries.clear()
    win10.log("[dev] Setting IT: 400ms")
    win10.log("-> {'command': 'status'}")
    win10.log("recording to somewhere")
    win10.log("poll error: device fell off the bus")
    app.processEvents()
    shown = win10.logBox.toPlainText()
    check("Setting IT" not in shown, "device narration is filtered out")
    check("'command': 'status'" not in shown, "commands sent are filtered out")
    check("recording to somewhere" in shown, "app events are kept")
    check("poll error" in shown, "failures are never hidden")
    check("2" in win10.logCount.text(),
          f"and the count says how many were hidden: {win10.logCount.text()!r}")
    win10.showDeviceChk.setChecked(True)
    app.processEvents()
    check("Setting IT" in win10.logBox.toPlainText(),
          "device output comes back when asked for")
    win10.showDeviceChk.setChecked(False)
    # The old renderer redrew every line on every new line. Cap the history
    # so a multi-day run cannot make that quadratic again.
    for i in range(biomass_gui.LOG_LIMIT + 50):
        win10.log(f"filler {i}")
    check(len(win10._log_entries) <= biomass_gui.LOG_LIMIT,
          f"log history capped at {biomass_gui.LOG_LIMIT}, "
          f"got {len(win10._log_entries)}")
    win10._log_entries.clear()
    win10._render_log()

    print("\n[25] a new experiment can begin from device memory")
    real_dialog = biomass_gui.NewExperimentDialog

    def stub_dialog(values):
        class Stub:
            def __init__(self, *a, **kw):
                Stub.kwargs = kw

            def exec(self):
                return QtWidgets.QDialog.DialogCode.Accepted

            def result_values(self):
                return values
        return Stub

    try:
        biomass_gui.NewExperimentDialog = stub_dialog(
            (tmp / "seeded", "from_buffer", False, False, True, 0.0))
        win10._new_experiment()
        app.processEvents()
        held = len(dev8.ring)
        check(biomass_gui.NewExperimentDialog.kwargs.get("stored") == held,
              "the dialog is told how much the device is holding")
        check(win10.client.recorder.count == held,
              f"seeded run wrote {win10.client.recorder.count} rows, want {held}")
        check(win10.xs.size == held, "and the plot shows the same history")

        biomass_gui.NewExperimentDialog = stub_dialog(
            (tmp / "unseeded", "clean_start", False, False, False, 0.0))
        win10._new_experiment()
        app.processEvents()
        check(win10.client.recorder.count == 0,
              "an ordinary new experiment starts empty")
        check(win10.xs.size == 0, "with an empty plot")

        # A paced sweep has to reach the device as a duty, not as a hope.
        dev8.commands.clear()
        biomass_gui.NewExperimentDialog = stub_dialog(
            (tmp / "paced", "slow_zero", True, False, False,
             biomass_gui.SLOW_BLANK_DUTY_PCT))
        win10._new_experiment()
        app.processEvents()
        blanks = dev8.sent("blank")
        check(blanks and blanks[-1].get("duty_pct")
              == biomass_gui.SLOW_BLANK_DUTY_PCT,
              f"slow sweep asked for by duty: {blanks[-1:]}")
        dev8.commands.clear()
        biomass_gui.NewExperimentDialog = stub_dialog(
            (tmp / "quick", "fast_zero", True, False, False, 0.0))
        win10._new_experiment()
        app.processEvents()
        blanks = dev8.sent("blank")
        check(blanks and "duty_pct" not in blanks[-1],
              f"fast sweep sends the bare command, as old firmware expects: "
              f"{blanks[-1:]}")
    finally:
        biomass_gui.NewExperimentDialog = real_dialog
        win10._await_blank = False

    print("\n[25b] a re-zero updates the blank recorded in meta.json")
    # The recorder captures the blank when it opens, which for a new
    # experiment is before the sweep it asked for. Left alone, meta.json
    # documents the calibration the run replaced, not the one its rows were
    # computed against -- seen on real hardware, where meta said 34905 while
    # every row divided by 34814.
    win10.nameEdit.setText("rezero")
    win10._start_recording()
    stale = json.loads(
        win10.client.recorder.meta_path.read_text(encoding="utf-8"))
    win10._begin_blank_wait(start_after=False)
    dev8.reblank(duty=biomass_gui.SLOW_BLANK_DUTY_PCT)   # sweep on the device
    dev8.state = "blanking"
    win10._service_pending_experiment(dev8.get_status())
    dev8.state = "idle"
    win10._service_pending_experiment(dev8.get_status())
    app.processEvents()
    fresh = json.loads(
        win10.client.recorder.meta_path.read_text(encoding="utf-8"))
    check(fresh["blank_table"] != stale["blank_table"],
          "meta.json carries the blank the sweep produced, not the old one")
    check(fresh["blank_table"]["i0"] == dev8.get_blank_table()["i0"],
          "and it matches what the device now holds")
    check(fresh["blank_table"].get("sweep_duty_pct")
          == biomass_gui.SLOW_BLANK_DUTY_PCT,
          "the run records that its I0 came from a paced sweep")

    # A sweep that was stopped restores the old table, and the device still
    # reports blank_done -- so idle+blank_done cannot be read as success.
    # Three minutes of paced sweep is a long time to change your mind in.
    win10._begin_blank_wait(start_after=True)
    before = json.loads(
        win10.client.recorder.meta_path.read_text(encoding="utf-8"))
    dev8.commands.clear()
    dev8.state = "blanking"
    win10._service_pending_experiment(dev8.get_status())
    dev8.state = "idle"                   # aborted: no reblank() call
    win10._service_pending_experiment(dev8.get_status())
    app.processEvents()
    after = json.loads(
        win10.client.recorder.meta_path.read_text(encoding="utf-8"))
    check(after["blank_table"] == before["blank_table"],
          "an aborted sweep does not restamp the metadata")
    check(not dev8.sent("start"),
          "and does not start a run against a calibration nobody confirmed")
    check("aborted" in win10.logBox.toPlainText(),
          f"the abort is reported, not reported as success")

    print("\n[26] the plot reports absorbance at its real size")
    left = win10.p1.getAxis("left")
    check(left.tickStrings([0.002, 0.003, 0.004], 1.0, 0.001) ==
          ["0.002", "0.003", "0.004"],
          f"ticks are absolute, not scaled: "
          f"{left.tickStrings([0.002, 0.003, 0.004], 1.0, 0.001)}")
    check(not left.autoSIPrefix and not win10.p2.getAxis('left').autoSIPrefix,
          "neither y axis factors a power out of its labels")
    # A flat trace must not be magnified to fill the plot.
    win10.xs = np.linspace(0, 1, 40)
    win10.ys = np.full(40, 0.003)
    win10.raws = np.full(40, 34900.0)
    win10.i0s = np.full(40, 35148.0)
    win10._redraw()
    win10._autoscale()
    app.processEvents()
    lo, hi = win10.p1.getViewBox().viewRange()[1]
    check(hi - lo >= biomass_gui.MIN_ABS_SPAN - 1e-9,
          f"a 0 mAU trace still spans {(hi - lo) * 1000:.1f} mAU of axis, "
          f"want at least {biomass_gui.MIN_ABS_SPAN * 1000:.0f}")
    check("0.0030" in win10.spanInfo.text(),
          f"the absolute window is stated: {win10.spanInfo.text()!r}")
    win10.close()

    print("\n[14] clean shutdown")
    win.close()
    app.processEvents()
    check(not win.timer.isActive(), "poll timer stopped on close")

    print("\n" + ("ALL PASS" if not fails else f"{len(fails)} FAILURES:"))
    for f in fails:
        print("  - " + f)
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(main())
