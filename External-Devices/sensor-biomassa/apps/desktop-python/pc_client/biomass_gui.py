"""
biomass_gui.py -- desktop control window for the biomass sensor.

Full hardware control: connection, run recording, blank/start/stop, single
shots, auto-ranging or a manually locked gear, direct LED duty, sampling
interval, filter coefficient, thresholds, and the gear tables themselves.

Requires PySide6 + pyqtgraph. Built for firmware v5.0; older firmware degrades
gracefully rather than silently sending commands the device will ignore:

  * below v4.1 the manual-control widgets are disabled and say why
  * below v5.0 the conversion-period diagnostic is hidden, and the anchoring
    and SoC-temperature readouts show nothing rather than a reassuring zero
    the device never sent
"""

from __future__ import annotations

import math
import os
from datetime import datetime, timedelta
from pathlib import Path
from typing import Callable, Optional

import numpy as np

# This application is built on PySide6. pyqtgraph otherwise auto-selects the
# first installed Qt binding, which can be PyQt6 on a developer machine and
# produces PlotWidget objects that QSplitter cannot accept as PySide6 widgets.
os.environ["PYQTGRAPH_QT_LIB"] = "PySide6"
from PySide6 import QtCore, QtGui, QtWidgets
import pyqtgraph as pg

# Display-only smoothing. This changes rasterization of plot lines, never the
# samples or CSV values supplied to the curves.
pg.setConfigOptions(antialias=True)

from biomass_core import (
    DEFAULT_HTTP_HOST,
    HttpTransport,
    Recorder,
    Sample,
    SensorClient,
    SerialTransport,
    Transport,
    TransportError,
    blank_cell_state,
    list_candidate_ports,
)
from biomass_i18n import EN, PT_BR, has_translation, normalize_language, tr, translate_log

# The VEML7700's selectable integration times. The firmware stores an index
# into this list, not the millisecond value.
IT_CHOICES = [(0, "25 ms"), (1, "50 ms"), (2, "100 ms"), (3, "200 ms"),
              (4, "400 ms"), (5, "800 ms")]
IT_MS = [25, 50, 100, 200, 400, 800]

MIN_FW_MANUAL = (4, 1)   # manual control commands land in 4.1
#: probe_period, boundary_misses and soc_temp_c land in 5.0. The window works
#: fine against older firmware -- those readouts simply have nothing to show,
#: and the diagnostic button is hidden rather than sending a command the
#: device would answer with "Error: Command must be in JSON format".
MIN_FW_PROBE = (5, 0)
#: paced blanking sweeps ({"command":"blank","duty_pct":D}) land in 5.2
MIN_FW_PACED_BLANK = (5, 2)

#: LED duty a paced ("slow") blanking sweep is held at, in percent.
#:
#: This is the firmware's own LED_DUTY_LIMIT -- the load it already considers
#: safe to run at indefinitely while measuring -- so the sweep captures the
#: table under the same thermal conditions the measurements will live in.
#: It puts the sweep at about three minutes, against roughly 25 s and ~60 %
#: duty back to back. See characterization/cold_start_warmup.md.
SLOW_BLANK_DUTY_PCT = 8.0

#: Log lines kept in memory. A multi-day run produces plenty, and an
#: unbounded list is both a leak and, once it has to be re-rendered, slow.
LOG_LIMIT = 2000
#: What the history is cut back to when it hits the limit. Trimming in one
#: block matters: dropping a single entry per line would put a full redraw of
#: the box on every line from the cap onwards, which is worse than the
#: unbounded list it replaced.
LOG_TRIM_TO = 1800

#: Smallest absorbance window the plot will ever show, in AU. Autoscale on a
#: flat trace would otherwise magnify sensor noise until it fills the plot,
#: and a 0.3 mAU wobble drawn edge to edge reads as an experiment going wrong.
#: 0.01 AU is roughly 100x the instrument's point-to-point noise.
MIN_ABS_SPAN = 0.01
#: Same idea for the raw/I0 plot, in sensor counts.
MIN_COUNT_SPAN = 500.0


# ==========================================================================
# Theme
# ==========================================================================

THEMES = {
    "dark": {
        "bg": "#14161a", "panel": "#1c1f26", "panel2": "#232733",
        "fg": "#e8eaee", "muted": "#98a0b3", "line": "#2c313b",
        "accent": "#5b93f0", "ok": "#3ec27a", "warn": "#e0a12b",
        "err": "#e5675a", "plot_bg": "#1c1f26", "grid": "#3a4050",
        "trace": "#5b93f0", "trace2": "#3ec27a", "trace3": "#e0a12b",
    },
    "light": {
        "bg": "#f2f4f7", "panel": "#ffffff", "panel2": "#f7f8fa",
        "fg": "#16181d", "muted": "#697086", "line": "#dde1e8",
        "accent": "#2d6cdf", "ok": "#1f9d55", "warn": "#b8770a",
        "err": "#c0392b", "plot_bg": "#ffffff", "grid": "#c9cfda",
        "trace": "#2d6cdf", "trace2": "#1f9d55", "trace3": "#b8770a",
    },
}

QSS = """
QWidget {{ color: {fg}; font-family: "Segoe UI", system-ui, sans-serif;
           font-size: 13px; }}
QMainWindow, QDialog, QWidget#appRoot {{ background: {bg}; }}
QWidget#scrollViewport, QWidget#scrollPage {{ background: {panel}; }}
QFrame#card, QGroupBox {{ background: {panel}; border: 1px solid {line};
                          border-radius: 10px; }}
QGroupBox {{ margin-top: 16px; padding: 14px 12px 12px 12px;
             font-weight: 600; }}
QGroupBox::title {{ subcontrol-origin: margin; left: 12px; padding: 0 5px;
                    color: {muted}; font-size: 11px; font-weight: 700; }}
QLabel#h1 {{ font-size: 15px; font-weight: 700; }}
QLabel#big {{ font-size: 40px; font-weight: 700; }}
QLabel#unit, QLabel#caption {{ color: {muted}; font-size: 10px;
                               font-weight: 700; letter-spacing: .5px; }}
QLabel#statval {{ font-size: 15px; font-weight: 600; }}
QLabel#hint {{ color: {muted}; font-size: 11px; }}
QLabel#chip {{ padding: 4px 12px; border-radius: 10px; font-weight: 700;
               font-size: 11px; border: 1px solid {line}; color: {muted}; }}

QPushButton {{ background: {panel2}; border: 1px solid {line};
               border-radius: 7px; padding: 7px 14px; font-weight: 600; }}
QPushButton:hover {{ border-color: {accent}; }}
QPushButton:pressed {{ background: {line}; }}
QPushButton:disabled {{ color: {muted}; border-color: {line}; }}
QPushButton#primary {{ background: {accent}; border-color: {accent};
                       color: #ffffff; }}
QPushButton#primary:disabled {{ background: {panel2}; color: {muted};
                                border-color: {line}; }}
QPushButton#danger {{ border-color: {err}; color: {err}; }}
QPushButton#plotAction {{ padding-left: 6px; padding-right: 6px; }}
QPushButton:checked {{ background: {accent}; border-color: {accent};
                       color: #ffffff; }}

QTabWidget::pane {{ border: 1px solid {line}; border-radius: 10px;
                    background: {panel}; top: -1px; }}
QTabBar::tab {{ background: transparent; color: {muted}; padding: 8px 14px;
                font-weight: 600; border: none; }}
QTabBar::tab:selected {{ color: {fg}; border-bottom: 2px solid {accent}; }}

QSlider::groove:horizontal {{ height: 4px; background: {line};
                              border-radius: 2px; }}
QSlider::sub-page:horizontal {{ background: {accent}; border-radius: 2px; }}
QSlider::handle:horizontal {{ background: {fg}; width: 14px; height: 14px;
                              margin: -6px 0; border-radius: 7px; }}
QSlider::handle:horizontal:disabled {{ background: {muted}; }}

QSpinBox, QDoubleSpinBox, QLineEdit, QComboBox {{
    background: {panel2}; border: 1px solid {line}; border-radius: 6px;
    padding: 5px 7px; }}
QSpinBox:focus, QDoubleSpinBox:focus, QLineEdit:focus, QComboBox:focus {{
    border-color: {accent}; }}
QComboBox::drop-down {{ border: none; width: 18px; }}
QComboBox QAbstractItemView {{ background: {panel2}; border: 1px solid {line};
                               selection-background-color: {accent}; }}

QCheckBox {{ spacing: 8px; }}
QCheckBox::indicator {{ width: 16px; height: 16px; border-radius: 4px;
                        border: 1px solid {line}; background: {panel2}; }}
QCheckBox::indicator:checked {{ background: {accent}; border-color: {accent}; }}

QPlainTextEdit {{ background: {panel}; border: 1px solid {line};
                  border-radius: 8px; color: {muted};
                  font-family: Consolas, monospace; font-size: 11px; }}

QTableWidget, QTableView {{ background: {panel}; color: {fg};
    gridline-color: {line}; border: 1px solid {line}; border-radius: 8px;
    selection-background-color: {accent}; selection-color: #ffffff;
    outline: none; }}
QTableWidget::item, QTableView::item {{ padding: 5px 8px;
    border: none; }}
QTableWidget::item:selected {{ background: {accent}; color: #ffffff; }}
QHeaderView {{ background: transparent; border: none; }}
QHeaderView::section {{ background: {panel2}; color: {muted}; border: none;
    border-right: 1px solid {line}; border-bottom: 1px solid {line};
    padding: 6px 10px; font-size: 11px; font-weight: 700; }}
QHeaderView::section:last {{ border-right: none; }}
QTableCornerButton::section {{ background: {panel2}; border: none;
    border-right: 1px solid {line}; border-bottom: 1px solid {line}; }}
QStatusBar {{ color: {muted}; }}
QSplitter::handle {{ background: transparent; }}
QScrollArea {{ border: none; background: transparent; }}
QScrollBar:vertical {{ background: {panel}; width: 10px; margin: 0;
                       border: none; }}
QScrollBar::handle:vertical {{ background: {line}; border: none;
                               border-radius: 4px; min-height: 30px;
                               margin: 2px; }}
QScrollBar::handle:vertical:hover {{ background: {muted}; }}
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical {{
    background: transparent; border: none; }}
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical {{
    background: transparent; border: none; height: 0; }}
QScrollBar::up-arrow:vertical, QScrollBar::down-arrow:vertical {{
    image: none; width: 0; height: 0; }}
QScrollBar:horizontal {{ background: {panel}; height: 10px; margin: 0;
                         border: none; }}
QScrollBar::handle:horizontal {{ background: {line}; border: none;
                                 border-radius: 4px; min-width: 30px;
                                 margin: 2px; }}
QScrollBar::add-page:horizontal, QScrollBar::sub-page:horizontal {{
    background: transparent; border: none; }}
QScrollBar::add-line:horizontal, QScrollBar::sub-line:horizontal {{
    background: transparent; border: none; width: 0; }}
QScrollBar::left-arrow:horizontal, QScrollBar::right-arrow:horizontal {{
    image: none; width: 0; height: 0; }}
QToolTip {{ background: {panel2}; color: {fg}; border: 1px solid {line};
            padding: 5px; }}
"""


# ==========================================================================
# Reusable widgets
# ==========================================================================

class Slider(QtWidgets.QWidget):
    """
    Label + slider + numeric box, kept in sync.

    `committed` fires only when the user finishes (slider released, or the
    box edited) so the device is not flooded with intermediate values while
    a slider is being dragged. `moved` fires continuously for live labels.
    """

    committed = QtCore.Signal(float)
    moved = QtCore.Signal(float)

    def __init__(self, title: str, lo: float, hi: float, decimals: int = 0,
                 step: float = 1.0, suffix: str = "", tip: str = ""):
        super().__init__()
        self._scale = 10 ** decimals
        self._decimals = decimals
        self._guard = False

        lay = QtWidgets.QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(4)

        top = QtWidgets.QHBoxLayout()
        top.setContentsMargins(0, 0, 0, 0)
        self.caption = QtWidgets.QLabel(title)
        self.caption.setObjectName("caption")
        top.addWidget(self.caption)
        top.addStretch(1)
        if decimals:
            self.box = QtWidgets.QDoubleSpinBox()
            self.box.setDecimals(decimals)
        else:
            self.box = QtWidgets.QSpinBox()
        self.box.setRange(lo, hi)
        self.box.setSingleStep(step)
        self.box.setSuffix(suffix)
        self.box.setFixedWidth(96)
        self.box.setAlignment(QtCore.Qt.AlignmentFlag.AlignRight)
        top.addWidget(self.box)
        lay.addLayout(top)

        self.slider = QtWidgets.QSlider(QtCore.Qt.Orientation.Horizontal)
        self.slider.setRange(int(lo * self._scale), int(hi * self._scale))
        lay.addWidget(self.slider)

        if tip:
            self.setToolTip(tip)

        self.slider.valueChanged.connect(self._on_slide)
        self.slider.sliderReleased.connect(
            lambda: self.committed.emit(self.value()))
        self.box.editingFinished.connect(self._on_box)

    def _on_slide(self, raw: int):
        if self._guard:
            return
        v = raw / self._scale
        self._guard = True
        self.box.setValue(v)
        self._guard = False
        self.moved.emit(v)
        # Keyboard/wheel changes never emit sliderReleased, so commit those.
        if not self.slider.isSliderDown():
            self.committed.emit(v)

    def _on_box(self):
        if self._guard:
            return
        v = self.box.value()
        self._guard = True
        self.slider.setValue(int(v * self._scale))
        self._guard = False
        self.moved.emit(v)
        self.committed.emit(v)

    def value(self) -> float:
        return self.box.value()

    def set_value_silently(self, v: float):
        """Reflect device state without emitting anything back at it."""
        self._guard = True
        self.box.setValue(v)
        self.slider.setValue(int(v * self._scale))
        self._guard = False

    @property
    def resolution(self) -> float:
        """Smallest value change this control can represent.

        Exposed because callers need it to round a hardware limit UP to
        something the control can actually hold, and the underlying widget is
        a QSpinBox or a QDoubleSpinBox depending on `decimals` -- only the
        latter has .decimals().
        """
        return 1.0 / self._scale

    def set_minimum(self, lo: float):
        """
        Raises the lower bound, e.g. to a hardware-imposed floor.

        Guarded because changing a range can clamp the current value, and an
        unguarded clamp would look like a user edit and be echoed straight
        back to the device.
        """
        if abs(self.box.minimum() - lo) < 1e-9:
            return
        self._guard = True
        self.box.setMinimum(lo)
        self.slider.setMinimum(int(lo * self._scale))
        self._guard = False

    def set_enabled(self, on: bool):
        self.slider.setEnabled(on)
        self.box.setEnabled(on)


class IndexSlider(QtWidgets.QWidget):
    """Discrete slider over a list of labels (gear selection)."""

    committed = QtCore.Signal(int)

    def __init__(self, title: str, labels: list[str], tip: str = ""):
        super().__init__()
        self._guard = False
        lay = QtWidgets.QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(4)

        top = QtWidgets.QHBoxLayout()
        top.setContentsMargins(0, 0, 0, 0)
        self.caption = QtWidgets.QLabel(title)
        self.caption.setObjectName("caption")
        top.addWidget(self.caption)
        top.addStretch(1)
        self.value_label = QtWidgets.QLabel("--")
        self.value_label.setObjectName("statval")
        top.addWidget(self.value_label)
        lay.addLayout(top)

        self.slider = QtWidgets.QSlider(QtCore.Qt.Orientation.Horizontal)
        self.slider.setTickPosition(QtWidgets.QSlider.TickPosition.TicksBelow)
        self.slider.setTickInterval(1)
        self.slider.setPageStep(1)
        lay.addWidget(self.slider)

        if tip:
            self.setToolTip(tip)
        self.set_labels(labels)
        self.slider.valueChanged.connect(self._on_change)
        self.slider.sliderReleased.connect(
            lambda: self.committed.emit(self.slider.value()))

    def set_labels(self, labels: list[str]):
        self._labels = labels or ["--"]
        self._guard = True
        cur = self.slider.value()
        self.slider.setRange(0, max(0, len(self._labels) - 1))
        self.slider.setValue(min(cur, len(self._labels) - 1))
        self._guard = False
        self._refresh_label()

    def _refresh_label(self):
        i = self.slider.value()
        if 0 <= i < len(self._labels):
            self.value_label.setText(self._labels[i])

    def _on_change(self, _v: int):
        self._refresh_label()
        if self._guard:
            return
        if not self.slider.isSliderDown():
            self.committed.emit(self.slider.value())

    def value(self) -> int:
        return self.slider.value()

    def set_value_silently(self, i: int):
        self._guard = True
        self.slider.setValue(i)
        self._guard = False
        self._refresh_label()

    def set_enabled(self, on: bool):
        self.slider.setEnabled(on)


def card(title: str) -> tuple[QtWidgets.QGroupBox, QtWidgets.QVBoxLayout]:
    box = QtWidgets.QGroupBox(title)
    lay = QtWidgets.QVBoxLayout(box)
    lay.setSpacing(11)
    return box, lay


def hint(text: str) -> QtWidgets.QLabel:
    lb = QtWidgets.QLabel(text)
    lb.setObjectName("hint")
    lb.setWordWrap(True)
    # Translated paragraphs can be substantially longer than English. Ignore
    # their unwrapped width hint so the sidebar keeps wrapping at the viewport
    # instead of growing horizontally.
    lb.setSizePolicy(QtWidgets.QSizePolicy.Policy.Ignored,
                     QtWidgets.QSizePolicy.Policy.Preferred)
    return lb


def path_label() -> QtWidgets.QLabel:
    """
    A hint label safe to put a filesystem path in.

    Word wrap cannot break a path -- it has no spaces -- so an ordinary
    wrapped QLabel reports a minimum width of the entire path and drags the
    whole panel wider than its pane. Ignored horizontal policy makes the
    layout stop consulting that width; the full path lives in the tooltip.
    """
    lb = hint("")
    lb.setWordWrap(False)
    lb.setSizePolicy(QtWidgets.QSizePolicy.Policy.Ignored,
                     QtWidgets.QSizePolicy.Policy.Preferred)
    return lb


def set_path(lb: QtWidgets.QLabel, path) -> None:
    p = Path(path)
    shown = str(Path(*p.parts[-2:])) if len(p.parts) > 2 else str(p)
    if len(shown) > 44:
        shown = "…" + shown[-43:]
    lb.setText(shown)
    lb.setToolTip(str(p))


def register_translatable_tree(root: QtWidgets.QWidget, language: str,
                               exclude=()) -> None:
    """Remember English widget strings and apply the selected language."""
    excluded = {id(widget) for widget in exclude}
    for widget in [root, *root.findChildren(QtWidgets.QWidget)]:
        if id(widget) in excluded:
            continue
        if isinstance(widget, QtWidgets.QGroupBox):
            source = widget.title()
            if has_translation(source):
                widget.setProperty("i18n_title", source)
        elif isinstance(widget, (QtWidgets.QAbstractButton, QtWidgets.QLabel)):
            source = widget.text()
            if has_translation(source):
                widget.setProperty("i18n_text", source)
        if isinstance(widget, QtWidgets.QLineEdit):
            source = widget.placeholderText()
            if has_translation(source):
                widget.setProperty("i18n_placeholder", source)
        source = widget.toolTip()
        if has_translation(source):
            widget.setProperty("i18n_tooltip", source)
    retranslate_tree(root, language)


def retranslate_tree(root: QtWidgets.QWidget, language: str) -> None:
    for widget in [root, *root.findChildren(QtWidgets.QWidget)]:
        source = widget.property("i18n_title")
        if source and isinstance(widget, QtWidgets.QGroupBox):
            widget.setTitle(tr(language, source))
        source = widget.property("i18n_text")
        if source and isinstance(widget, (QtWidgets.QAbstractButton, QtWidgets.QLabel)):
            widget.setText(tr(language, source))
        source = widget.property("i18n_placeholder")
        if source and isinstance(widget, QtWidgets.QLineEdit):
            widget.setPlaceholderText(tr(language, source))
        source = widget.property("i18n_tooltip")
        if source:
            widget.setToolTip(tr(language, source))


# ==========================================================================
# New experiment dialog
# ==========================================================================

class NewExperimentDialog(QtWidgets.QDialog):
    """
    Collects everything needed to begin a fresh run.

    A run is more than a new file: the plot has to be cleared and the elapsed
    clock restarted, otherwise the previous experiment's curve stays on screen
    and the new data lands hours along the x axis.
    """

    def __init__(self, parent, outdir: Path, name: str, language: str = EN,
                 can_blank: bool = True, stored: int = 0,
                 paced_blank: bool = True):
        super().__init__(parent)
        self.language = language
        self.outdir = Path(outdir)
        self.stored = max(0, int(stored))
        self.paced_blank = paced_blank
        self.setMinimumWidth(470)

        lay = QtWidgets.QVBoxLayout(self)
        lay.setContentsMargins(16, 16, 16, 16)
        lay.setSpacing(12)

        title = QtWidgets.QLabel("Start a new experiment")
        title.setObjectName("h1")
        lay.addWidget(title)

        form = QtWidgets.QGridLayout()
        form.setHorizontalSpacing(10)
        form.setVerticalSpacing(8)

        form.addWidget(QtWidgets.QLabel("Experiment name"), 0, 0)
        self.nameEdit = QtWidgets.QLineEdit(name)
        self.nameEdit.selectAll()
        form.addWidget(self.nameEdit, 0, 1, 1, 2)

        form.addWidget(QtWidgets.QLabel("Save to folder"), 1, 0)
        self.dirLabel = path_label()
        set_path(self.dirLabel, self.outdir)
        form.addWidget(self.dirLabel, 1, 1)
        browse = QtWidgets.QPushButton("Browse")
        browse.clicked.connect(self._browse)
        form.addWidget(browse, 1, 2)
        lay.addLayout(form)

        # Off by default: a new experiment starts at the moment it is
        # created. The device buffer is normally the tail of the previous
        # run, and inheriting it silently is what made the plot and the file
        # disagree about when the experiment began.
        self.seedChk = QtWidgets.QCheckBox("")
        self.seedChk.setChecked(False)
        self.seedChk.setEnabled(self.stored > 0)
        lay.addWidget(self.seedChk)
        self.seedHint = hint("")
        lay.addWidget(self.seedHint)

        self.blankChk = QtWidgets.QCheckBox("Run blanking sweep (zero) first")
        self.blankChk.setChecked(can_blank)
        self.blankChk.setEnabled(can_blank)
        lay.addWidget(self.blankChk)

        # Slow is the default where the firmware supports it: a back-to-back
        # sweep captures each gear at a different point along its own heat
        # pulse, and the finished table then belongs to a thermal state the
        # instrument leaves the moment it starts measuring.
        self.blankMode = QtWidgets.QComboBox()
        self.blankMode.addItems(["", ""])
        self.blankMode.setCurrentIndex(1 if paced_blank else 0)
        self.blankMode.setEnabled(can_blank and paced_blank)
        lay.addWidget(self.blankMode)

        self.startChk = QtWidgets.QCheckBox("Start measuring automatically")
        self.startChk.setChecked(True)
        lay.addWidget(self.startChk)

        self.blankHint = hint("")
        lay.addWidget(self.blankHint)
        self.blankChk.toggled.connect(self._sync_blank_mode)
        self.blankMode.currentIndexChanged.connect(self._refresh_blank_hint)

        self.summary = hint("")
        lay.addWidget(self.summary)

        btns = QtWidgets.QHBoxLayout()
        btns.addStretch(1)
        cancel = QtWidgets.QPushButton("Cancel")
        cancel.clicked.connect(self.reject)
        btns.addWidget(cancel)
        ok = QtWidgets.QPushButton("New experiment")
        ok.setObjectName("primary")
        ok.setDefault(True)
        ok.clicked.connect(self.accept)
        btns.addWidget(ok)
        lay.addLayout(btns)

        # The seeding labels carry a live sample count, and the sweep-mode
        # entries carry a duty figure, so both are written here rather than
        # looked up from an English source string.
        register_translatable_tree(
            self, language,
            exclude=(self.seedChk, self.seedHint, self.blankHint))
        self.setWindowTitle(tr(language, "New experiment"))
        self._refresh_summary()
        self._refresh_seed()
        self._refresh_blank_mode_labels()
        self._sync_blank_mode()

    def _browse(self):
        d = QtWidgets.QFileDialog.getExistingDirectory(
            self, tr(self.language, "Output folder"), str(self.outdir))
        if d:
            self.outdir = Path(d)
            set_path(self.dirLabel, self.outdir)
            self._refresh_summary()

    def _refresh_summary(self):
        self.summary.setText(tr(self.language, "new_experiment_summary",
                                path=str(self.outdir)))

    def _refresh_seed(self):
        self.seedChk.setText(tr(self.language, "seed_from_device",
                                count=self.stored))
        self.seedHint.setText(tr(
            self.language,
            "seed_hint" if self.stored else "seed_hint_empty"))

    def _refresh_blank_mode_labels(self):
        current = self.blankMode.currentIndex()
        self.blankMode.setItemText(0, tr(self.language, "blank_mode_fast"))
        self.blankMode.setItemText(
            1, tr(self.language, "blank_mode_slow", duty=f"{SLOW_BLANK_DUTY_PCT:g}"))
        self.blankMode.setCurrentIndex(current)

    def _sync_blank_mode(self):
        on = self.blankChk.isChecked()
        self.blankMode.setEnabled(on and self.paced_blank)
        self._refresh_blank_hint()

    def _refresh_blank_hint(self):
        if not self.blankChk.isChecked():
            self.blankHint.setText(tr(self.language, "blank_hint_skipped"))
        elif not self.paced_blank:
            self.blankHint.setText(tr(self.language, "blank_hint_no_pacing"))
        elif self.blankMode.currentIndex() == 1:
            self.blankHint.setText(tr(self.language, "blank_hint_slow"))
        else:
            self.blankHint.setText(tr(self.language, "blank_hint_fast"))

    def result_values(self) -> tuple[Path, str, bool, bool, bool, float]:
        """
        (folder, name, run a blank, start measuring, seed from device,
        sweep duty %). A duty of 0 means the fast back-to-back sweep.
        """
        slow = (self.blankMode.currentIndex() == 1
                and self.blankMode.isEnabled())
        return (self.outdir, self.nameEdit.text().strip() or "run",
                self.blankChk.isChecked(), self.startChk.isChecked(),
                self.seedChk.isChecked() and self.seedChk.isEnabled(),
                SLOW_BLANK_DUTY_PCT if slow else 0.0)


# ==========================================================================
# Connection dialog
# ==========================================================================

class ConnectDialog(QtWidgets.QDialog):
    """Pick a transport before the main window opens."""

    def __init__(self, parent=None, default_host: str = DEFAULT_HTTP_HOST,
                 prefer: str = "serial", language: str = EN):
        super().__init__(parent)
        self.language = normalize_language(language)
        self.default_host = default_host
        self._status_source = ""
        self._status_values = {}
        self.setMinimumWidth(430)
        self.transport: Optional[Transport] = None
        self.prefer = prefer

        lay = QtWidgets.QVBoxLayout(self)
        lay.setSpacing(14)

        title = QtWidgets.QLabel("Biomass Sensor")
        title.setObjectName("h1")
        title_row = QtWidgets.QHBoxLayout()
        title_row.addWidget(title)
        title_row.addStretch(1)
        self.languageBtn = QtWidgets.QPushButton()
        self.languageBtn.clicked.connect(self._toggle_language)
        title_row.addWidget(self.languageBtn)
        lay.addLayout(title_row)

        self.mode = QtWidgets.QComboBox()
        self.mode.addItems(["USB serial (recommended)", "WiFi / device AP"])
        lay.addWidget(self.mode)

        # -- serial page
        self.serial_page = QtWidgets.QWidget()
        sp = QtWidgets.QHBoxLayout(self.serial_page)
        sp.setContentsMargins(0, 0, 0, 0)
        self.port = QtWidgets.QComboBox()
        self.port.setSizePolicy(QtWidgets.QSizePolicy.Policy.Expanding,
                                QtWidgets.QSizePolicy.Policy.Fixed)
        refresh = QtWidgets.QPushButton("Refresh")
        refresh.clicked.connect(self._refresh_ports)
        sp.addWidget(QtWidgets.QLabel("Port"))
        sp.addWidget(self.port, 1)
        sp.addWidget(refresh)
        lay.addWidget(self.serial_page)

        # -- network page
        self.net_page = QtWidgets.QWidget()
        np_ = QtWidgets.QHBoxLayout(self.net_page)
        np_.setContentsMargins(0, 0, 0, 0)
        self.host = QtWidgets.QLineEdit(default_host)
        np_.addWidget(QtWidgets.QLabel("Host"))
        np_.addWidget(self.host, 1)
        lay.addWidget(self.net_page)

        self.connectionHint = hint("")
        lay.addWidget(self.connectionHint)

        self.status = QtWidgets.QLabel("")
        self.status.setObjectName("hint")
        self.status.setWordWrap(True)
        lay.addWidget(self.status)

        btns = QtWidgets.QHBoxLayout()
        self.auto_btn = QtWidgets.QPushButton("Auto-detect")
        self.auto_btn.clicked.connect(self._auto)
        self.ok_btn = QtWidgets.QPushButton("Connect")
        self.ok_btn.setObjectName("primary")
        self.ok_btn.clicked.connect(self._connect)
        cancel = QtWidgets.QPushButton("Cancel")
        cancel.clicked.connect(self.reject)
        btns.addWidget(self.auto_btn)
        btns.addStretch(1)
        btns.addWidget(cancel)
        btns.addWidget(self.ok_btn)
        lay.addLayout(btns)

        self.mode.currentIndexChanged.connect(self._sync_pages)
        register_translatable_tree(
            self, self.language, exclude=(self.status, self.languageBtn))
        self._retranslate_ui()
        self._refresh_ports()
        self._sync_pages()

    def _t(self, source: str, **values) -> str:
        return tr(self.language, source, **values)

    def _set_status(self, source: str, **values) -> None:
        self._status_source = source
        self._status_values = values
        self.status.setText(self._t(source, **values) if source else "")

    def _toggle_language(self):
        self.language = PT_BR if self.language == EN else EN
        self._retranslate_ui()

    def _retranslate_ui(self):
        retranslate_tree(self, self.language)
        self.setWindowTitle(self._t("Connect to sensor"))
        current = self.mode.currentIndex()
        self.mode.setItemText(0, self._t("USB serial (recommended)"))
        self.mode.setItemText(1, self._t("WiFi / device AP"))
        self.mode.setCurrentIndex(current)
        self.connectionHint.setText(
            self._t("connect_hint", host=self.default_host))
        self.languageBtn.setText(
            "English" if self.language == PT_BR else "Português (Brasil)")
        if self._status_source:
            self._set_status(self._status_source, **self._status_values)

    def _sync_pages(self):
        serial = self.mode.currentIndex() == 0
        self.serial_page.setVisible(serial)
        self.net_page.setVisible(not serial)

    def _refresh_ports(self):
        self.port.clear()
        ports = list_candidate_ports()
        if ports:
            self.port.addItems(ports)
        else:
            self._set_status("No serial ports found.")

    def _busy(self, source: str):
        self._set_status(source)
        self.ok_btn.setEnabled(False)
        self.auto_btn.setEnabled(False)
        QtWidgets.QApplication.processEvents()

    def _done(self):
        self.ok_btn.setEnabled(True)
        self.auto_btn.setEnabled(True)

    def _try(self, t: Transport) -> bool:
        try:
            t.open()
            st = t.get_status()
            if "fw" not in st:
                raise TransportError("responded, but not with sensor status")
            self.transport = t
            return True
        except Exception as exc:
            self._set_status("Failed: {error}", error=exc)
            try:
                t.close()
            except Exception:
                pass
            return False

    def _connect(self):
        self._busy("Connecting...")
        if self.mode.currentIndex() == 0:
            if not self.port.currentText():
                self._set_status("No port selected.")
                self._done()
                return
            t = SerialTransport(self.port.currentText())
        else:
            t = HttpTransport(self.host.text().strip() or DEFAULT_HTTP_HOST)
        ok = self._try(t)
        self._done()
        if ok:
            self.accept()

    def _auto(self):
        self._busy("Looking for the sensor...")

        def try_serial() -> bool:
            for port in list_candidate_ports():
                self._set_status("Probing {port} ...", port=port)
                QtWidgets.QApplication.processEvents()
                if self._try(SerialTransport(port, timeout=2.0)):
                    return True
            return False

        def try_http() -> bool:
            self._set_status("Probing the device AP ...")
            QtWidgets.QApplication.processEvents()
            host = self.host.text().strip() or DEFAULT_HTTP_HOST
            return self._try(HttpTransport(host, timeout=3.0))

        probes = (try_http, try_serial) if self.prefer == "http" \
            else (try_serial, try_http)
        for probe in probes:
            if probe():
                self._done()
                self.accept()
                return
        self._set_status("No sensor found on any port or at the AP.")
        self._done()


# ==========================================================================
# Main window
# ==========================================================================

class MainWindow(QtWidgets.QMainWindow):

    def __init__(self, transport: Transport, outdir: Path, run_name: str,
                 interval: float = 1.0, autosave: bool = False,
                 theme: str = "dark", language: str = EN):
        super().__init__()
        self.t = transport
        self.outdir = Path(outdir)
        self.run_name = run_name
        self.theme_name = theme
        self.language = normalize_language(language)
        self.interval = interval
        self._guard = False          # suppress echo while syncing from device
        self._paused = False
        self._span_s = 0             # 0 = show everything
        self._fw_ok_manual = True
        self._debounce: dict[str, QtCore.QTimer] = {}
        self._debounce_fns: dict[str, Callable] = {}
        #: (when, message, category); category drives the log filter
        self._log_entries: list[tuple[datetime, str, str]] = []
        #: kept alongside rather than recounted, so appending a filtered line
        #: stays O(1) instead of walking the whole history
        self._hidden_count = 0
        self._last_saved: Optional[tuple[int, Path]] = None
        self._threshold_invalid = False
        #: guards the run-name box against re-entering relocation while its
        #: own modal dialog steals focus and fires editingFinished again
        self._relocating = False
        # New-experiment sequencing: the blanking sweep runs on the device,
        # so "start measuring afterwards" has to wait for a status update
        # rather than block the UI thread.
        self._await_blank = False
        self._pending_start = False
        self._blank_seen = False
        self._blank_deadline = datetime.now()

        # data arrays for the plots
        self.xs = np.empty(0)
        self.ys = np.empty(0)
        self.raws = np.empty(0)
        self.i0s = np.empty(0)
        self.singles: list[float] = []
        self.single_y: list[float] = []
        self.t0: Optional[float] = None

        self.setWindowTitle("Biomass Sensor")
        self.resize(1420, 900)
        self.setMinimumSize(1280, 700)

        self._build()
        register_translatable_tree(
            self, self.language,
            exclude=(self.chip, self.themeBtn, self.languageBtn, self.connBtn,
                     self.pauseBtn, self.recBtn, self.recInfo))
        self._retranslate_ui()
        self._apply_theme()

        self.client = SensorClient(
            transport, recorder=None,
            on_sample=self._on_sample, on_status=self._on_status,
            on_log=self.log)

        self.log(f"connected over {transport.name}")

        # Interactively this is False, and that is the point: prime() pulls
        # whatever the device has kept since its own boot, normally a previous
        # experiment. Those samples belong on the plot so the operator can see
        # what the sensor has been doing, but writing them to disk unasked is
        # how merely opening the app created a run folder full of somebody
        # else's data. Here, recording begins from New experiment.
        #
        # Unattended logging (--autosave) means the opposite -- capture
        # everything from connection onwards, buffer included -- so it opens
        # the file before the initial sync.
        if autosave:
            self._start_recording()
        try:
            self.client.prime()
        except TransportError as exc:
            self.log(f"initial sync failed: {exc}")
        self._redraw()

        self.timer = QtCore.QTimer(self)
        self.timer.timeout.connect(self._tick)
        self.timer.start(int(interval * 1000))

    # -- construction ----------------------------------------------------
    def _build(self):
        central = QtWidgets.QWidget()
        central.setObjectName("appRoot")
        self.setCentralWidget(central)
        root = QtWidgets.QVBoxLayout(central)
        root.setContentsMargins(12, 12, 12, 8)
        root.setSpacing(10)

        root.addWidget(self._build_header())

        self.mainSplitter = QtWidgets.QSplitter(QtCore.Qt.Orientation.Horizontal)
        self.mainSplitter.setChildrenCollapsible(False)
        self.mainSplitter.addWidget(self._build_left())
        self.mainSplitter.addWidget(self._build_sidebar())
        self.mainSplitter.setStretchFactor(0, 1)
        self.mainSplitter.setStretchFactor(1, 0)
        self.mainSplitter.setSizes([940, 460])
        root.addWidget(self.mainSplitter, 1)

        # The device narrates everything it does on the same wire it sends
        # data on -- a gear search alone is twenty lines, a sweep is thirty --
        # and every setting the window sends is echoed back. Useful when
        # something is wrong, noise the rest of the time, so both are off
        # until asked for. Anything the app itself has to say, including
        # every failure, is never filtered.
        filt = QtWidgets.QHBoxLayout()
        filt.setContentsMargins(2, 0, 2, 0)
        filt.setSpacing(12)
        logCaption = QtWidgets.QLabel("LOG")
        logCaption.setObjectName("caption")
        filt.addWidget(logCaption)
        self.showDeviceChk = QtWidgets.QCheckBox("Device output")
        self.showCmdChk = QtWidgets.QCheckBox("Commands sent")
        for box in (self.showDeviceChk, self.showCmdChk):
            box.setChecked(False)
            box.toggled.connect(self._render_log)
            filt.addWidget(box)
        filt.addStretch(1)
        self.logCount = QtWidgets.QLabel("")
        self.logCount.setObjectName("hint")
        filt.addWidget(self.logCount)
        root.addLayout(filt)

        self.logBox = QtWidgets.QPlainTextEdit()
        self.logBox.setReadOnly(True)
        self.logBox.setFixedHeight(104)
        root.addWidget(self.logBox)

        # The native dotted resize grip clashes with the custom theme and can
        # appear as stray pixels in the lower-right corner.
        self.statusBar().setSizeGripEnabled(False)
        self.statusBar().showMessage("not recording")

    def _t(self, source: str, **values) -> str:
        return tr(self.language, source, **values)

    def _build_header(self) -> QtWidgets.QWidget:
        f = QtWidgets.QFrame()
        f.setObjectName("card")
        lay = QtWidgets.QHBoxLayout(f)
        lay.setContentsMargins(14, 10, 14, 10)
        lay.setSpacing(14)

        title = QtWidgets.QLabel("Biomass Sensor")
        title.setObjectName("h1")
        lay.addWidget(title)

        self.chip = QtWidgets.QLabel("connecting")
        self.chip.setObjectName("chip")
        lay.addWidget(self.chip)

        self.devinfo = QtWidgets.QLabel("")
        self.devinfo.setObjectName("hint")
        lay.addWidget(self.devinfo)
        lay.addStretch(1)

        self.themeBtn = QtWidgets.QPushButton("Light")
        self.themeBtn.setFixedWidth(80)
        self.themeBtn.clicked.connect(self._toggle_theme)
        lay.addWidget(self.themeBtn)

        self.languageBtn = QtWidgets.QPushButton()
        self.languageBtn.clicked.connect(self._toggle_language)
        lay.addWidget(self.languageBtn)

        self.connBtn = QtWidgets.QPushButton("Disconnect")
        self.connBtn.clicked.connect(self._toggle_connection)
        lay.addWidget(self.connBtn)
        return f

    def _build_left(self) -> QtWidgets.QWidget:
        w = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(w)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(10)

        # -- readout
        rf = QtWidgets.QFrame()
        rf.setObjectName("card")
        rl = QtWidgets.QHBoxLayout(rf)
        rl.setContentsMargins(16, 12, 16, 12)
        rl.setSpacing(18)

        self.bigAbs = QtWidgets.QLabel("--")
        self.bigAbs.setObjectName("big")
        rl.addWidget(self.bigAbs)
        au = QtWidgets.QLabel("AU")
        au.setObjectName("unit")
        au.setAlignment(QtCore.Qt.AlignmentFlag.AlignBottom)
        rl.addWidget(au)
        rl.addSpacing(10)

        self.stat = {}
        for key, cap in [("raw", "RAW I"), ("i0", "BLANK I0"), ("tr", "TRANSM."),
                         ("it", "INT. TIME"), ("pwm", "LED DUTY"),
                         ("seq", "SAMPLES"), ("rate", "INTERVAL")]:
            col = QtWidgets.QVBoxLayout()
            col.setSpacing(1)
            c = QtWidgets.QLabel(cap)
            c.setObjectName("caption")
            v = QtWidgets.QLabel("--")
            v.setObjectName("statval")
            col.addWidget(c)
            col.addWidget(v)
            rl.addLayout(col)
            self.stat[key] = v
        rl.addStretch(1)
        lay.addWidget(rf)

        # -- plots
        pf = QtWidgets.QFrame()
        pf.setObjectName("card")
        pl = QtWidgets.QVBoxLayout(pf)
        pl.setContentsMargins(8, 8, 8, 8)
        pl.setSpacing(6)

        psplit = QtWidgets.QSplitter(QtCore.Qt.Orientation.Vertical)
        psplit.setChildrenCollapsible(False)

        self.p1 = pg.PlotWidget()
        self.p1.setLabel("left", "Absorbance (AU)")
        self.p1.showGrid(x=True, y=True, alpha=0.3)
        self.legend1 = self.p1.addLegend(offset=(-8, 8))
        self.curveA = self.p1.plot(name="absorbance", connect="finite",
                                   antialias=True)
        self.scatterSingle = pg.ScatterPlotItem(size=9, symbol="d",
                                                name="single shot")
        self.p1.addItem(self.scatterSingle)
        psplit.addWidget(self.p1)

        self.p2 = pg.PlotWidget()
        self.p2.setLabel("left", "counts")
        self.p2.setLabel("bottom", "elapsed (h)")
        self.p2.showGrid(x=True, y=True, alpha=0.3)
        self.legend2 = self.p2.addLegend(offset=(-8, 8))
        self.curveRaw = self.p2.plot(name="raw I", antialias=True)
        self.curveI0 = self.p2.plot(name="blank I0", antialias=True)
        self.p2.setXLink(self.p1)
        psplit.addWidget(self.p2)

        # Axis honesty. pyqtgraph factors a common power out of the tick
        # labels and prints it beside the axis title, so a run sitting at
        # 0.003 AU gets ticks reading 1, 2, 3 under an easy-to-miss x10^-3 --
        # a drift a thousand times larger than the real one. Print the real
        # numbers instead.
        for plot in (self.p1, self.p2):
            for axis in ("left", "bottom"):
                plot.getAxis(axis).enableAutoSIPrefix(False)
        # And a flat trace must look flat: floor the visible span so autoscale
        # cannot expand noise to fill the plot.
        self.p1.getViewBox().setLimits(minYRange=MIN_ABS_SPAN)
        self.p2.getViewBox().setLimits(minYRange=MIN_COUNT_SPAN)
        psplit.setSizes([420, 220])
        pl.addWidget(psplit, 1)

        bar = QtWidgets.QHBoxLayout()
        bar.setSpacing(6)
        for label, secs in [("All", 0), ("6 h", 21600), ("1 h", 3600),
                            ("15 min", 900)]:
            b = QtWidgets.QPushButton(label)
            b.setCheckable(True)
            b.setChecked(secs == 0)
            b.setFixedWidth(74 if label == "15 min" else 62)
            b.clicked.connect(lambda _c, s=secs, btn=b: self._set_span(s, btn))
            bar.addWidget(b)
            if secs == 0:
                self._span_btn = b
        self._span_buttons = [bar.itemAt(i).widget() for i in range(bar.count())]

        bar.addSpacing(12)
        self.pauseBtn = QtWidgets.QPushButton("Pause plot")
        self.pauseBtn.setObjectName("plotAction")
        self.pauseBtn.setCheckable(True)
        self.pauseBtn.toggled.connect(self._on_pause)
        bar.addWidget(self.pauseBtn)

        self.autoscaleBtn = QtWidgets.QPushButton("Autoscale")
        self.autoscaleBtn.setObjectName("plotAction")
        self.autoscaleBtn.clicked.connect(self._autoscale)
        bar.addWidget(self.autoscaleBtn)
        self.clearPlotBtn = QtWidgets.QPushButton("Clear plot")
        self.clearPlotBtn.setObjectName("plotAction")
        self.clearPlotBtn.setToolTip(
            "Discards what is drawn without touching the recorded file.")
        self.clearPlotBtn.clicked.connect(self._clear_plot_action)
        bar.addWidget(self.clearPlotBtn)

        self.exportImageBtn = QtWidgets.QPushButton("Export image")
        self.exportImageBtn.setObjectName("plotAction")
        self.exportImageBtn.clicked.connect(self._export_image)
        bar.addWidget(self.exportImageBtn)
        bar.addStretch(1)
        # States the absorbance window in absolute numbers, so how much of the
        # plot a wiggle covers never has to be read as how large it is.
        self.spanInfo = QtWidgets.QLabel("")
        self.spanInfo.setObjectName("hint")
        bar.addWidget(self.spanInfo)
        self.npts = QtWidgets.QLabel("")
        self.npts.setObjectName("hint")
        bar.addWidget(self.npts)
        pl.addLayout(bar)

        lay.addWidget(pf, 1)
        return w

    def _scroll(self, inner: QtWidgets.QWidget) -> QtWidgets.QScrollArea:
        sa = QtWidgets.QScrollArea()
        sa.viewport().setObjectName("scrollViewport")
        inner.setObjectName("scrollPage")
        sa.setWidgetResizable(True)
        sa.setWidget(inner)
        sa.setHorizontalScrollBarPolicy(
            QtCore.Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        return sa

    def _build_sidebar(self) -> QtWidgets.QWidget:
        tabs = QtWidgets.QTabWidget()
        # Wide enough for the widest row (three action buttons, and a slider
        # caption beside its 96 px spin box). Narrower than this and the
        # scroll area clips controls off the right edge instead of scrolling,
        # because the horizontal scrollbar is deliberately off.
        tabs.setMinimumWidth(460)
        tabs.addTab(self._scroll(self._tab_run()), "Run")
        tabs.addTab(self._scroll(self._tab_optics()), "Optics")
        tabs.addTab(self._scroll(self._tab_ranging()), "Ranging")
        tabs.addTab(self._scroll(self._tab_advanced()), "Advanced")
        self.sidebarTabs = tabs
        return tabs

    # -- tab: Run --------------------------------------------------------
    def _tab_run(self) -> QtWidgets.QWidget:
        w = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(w)
        lay.setSpacing(12)

        # One card owns the run: what it is called, where it is being written,
        # how it starts, and how it stops. Splitting the name and folder into
        # a second card was how an operator could retype a run name and watch
        # the samples keep landing in the old file.
        box, bl = card("EXPERIMENT")
        # What is running, and under what name -- the first thing an operator
        # returning to the bench needs to know.
        self.expStatus = QtWidgets.QLabel("")
        self.expStatus.setObjectName("expstatus")
        self.expStatus.setWordWrap(True)
        bl.addWidget(self.expStatus)

        r = QtWidgets.QHBoxLayout()
        r.addWidget(QtWidgets.QLabel("Run"))
        self.nameEdit = QtWidgets.QLineEdit(self.run_name)
        self.nameEdit.editingFinished.connect(self._on_run_name_edited)
        r.addWidget(self.nameEdit, 1)
        bl.addLayout(r)

        r = QtWidgets.QHBoxLayout()
        self.dirLabel = path_label()
        set_path(self.dirLabel, self.outdir)
        r.addWidget(self.dirLabel, 1)
        pick = QtWidgets.QPushButton("Browse")
        pick.setFixedWidth(96)
        pick.clicked.connect(self._pick_dir)
        r.addWidget(pick)
        bl.addLayout(r)

        self.recInfo = path_label()
        self.recInfo.setText("not recording")
        bl.addWidget(self.recInfo)
        bl.addWidget(hint("Changing the name or the folder while a run is "
                          "recording moves it: the app asks whether to carry "
                          "the samples already saved into the new file."))

        self.bNewExp = QtWidgets.QPushButton("New experiment")
        self.bNewExp.setObjectName("primary")
        self.bNewExp.clicked.connect(self._new_experiment)
        bl.addWidget(self.bNewExp)
        bl.addWidget(hint("Clears the plot, closes the current data file, "
                          "optionally re-zeros the instrument, and starts "
                          "recording to a folder you choose. It can also begin "
                          "from the samples the sensor already holds in its "
                          "own memory."))

        # Below New experiment and its explanation: the stop is the exception,
        # not the primary action, and it reads as the undo of the button above.
        self.recBtn = QtWidgets.QPushButton("Start recording")
        self.recBtn.setCheckable(True)
        self.recBtn.toggled.connect(self._on_record_toggle)
        bl.addWidget(self.recBtn)
        bl.addWidget(hint("Stopping closes the CSV. The device keeps "
                          "measuring and keeps its last 1024 samples, so a run "
                          "started again later can still recover them."))

        r = QtWidgets.QHBoxLayout()
        self.noteEdit = QtWidgets.QLineEdit()
        self.noteEdit.setPlaceholderText("note (e.g. fed 5 mL glucose)")
        self.noteEdit.returnPressed.connect(self._add_note)
        r.addWidget(self.noteEdit, 1)
        nb = QtWidgets.QPushButton("Add")
        nb.clicked.connect(self._add_note)
        r.addWidget(nb)
        bl.addLayout(r)
        bl.addWidget(hint("Notes are timestamped into meta.json with the "
                          "current sample number."))
        lay.addWidget(box)

        measBox, bl = card("MEASUREMENT")
        row = QtWidgets.QHBoxLayout()
        self.bBlank = QtWidgets.QPushButton("Blank")
        self.bBlank.setObjectName("primary")
        self.bBlank.clicked.connect(self._do_blank)
        self.bStart = QtWidgets.QPushButton("Start")
        self.bStart.setObjectName("primary")
        self.bStart.clicked.connect(lambda: self._cmd("start"))
        self.bStop = QtWidgets.QPushButton("Stop")
        self.bStop.clicked.connect(lambda: self._cmd("stop"))
        for b in (self.bBlank, self.bStart, self.bStop):
            row.addWidget(b)
        bl.addLayout(row)

        self.bOnce = QtWidgets.QPushButton("Single reading")
        self.bOnce.clicked.connect(lambda: self._cmd("read_once"))
        bl.addWidget(self.bOnce)
        bl.addWidget(hint("A single reading is unfiltered (no median/EMA "
                          "history for a one-off) and is flagged in the CSV "
                          "so it can be excluded from fits."))

        # Whole seconds. Sub-second precision was never usable here -- the
        # thermal floor is tens of seconds -- and a fractional control invited
        # values like 24.4 s that read as false precision on a quantity where
        # nothing below a second matters.
        self.sInterval = Slider("SAMPLING INTERVAL", 1, 3600, 0, 1, " s",
                                "How often a measurement is taken while "
                                "running. The LED is only on during the "
                                "read itself.")
        self.sInterval.committed.connect(
            lambda v: self._send(refresh_ms=int(v * 1000)))
        bl.addWidget(self.sInterval)
        self.intervalHint = hint("")
        bl.addWidget(self.intervalHint)
        lay.addWidget(measBox)

        box, bl = card("CONNECTION")
        self.hubChk = QtWidgets.QCheckBox("Push to TECNAL hub")
        self.hubChk.toggled.connect(
            lambda on: self._cmd("hub_on" if on else "hub_off"))
        bl.addWidget(self.hubChk)
        bl.addWidget(hint("Off means direct control only. Leave it off when "
                          "no hub is present: the device scans for WiFi every "
                          "10 s while enabled, and each scan briefly disturbs "
                          "clients on its own AP."))
        lay.addWidget(box)

        lay.addStretch(1)
        return w

    # -- tab: Optics -----------------------------------------------------
    def _tab_optics(self) -> QtWidgets.QWidget:
        w = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(w)
        lay.setSpacing(12)

        box, bl = card("GEAR CONTROL")
        # Kept separate from gearHint: the capability warning and the
        # gear-lock note are set at different points in _on_status, and
        # sharing one label meant whichever ran last silently won.
        self.fwWarn = QtWidgets.QLabel("")
        self.fwWarn.setObjectName("hint")
        self.fwWarn.setWordWrap(True)
        self.fwWarn.setVisible(False)
        bl.addWidget(self.fwWarn)

        self.autoChk = QtWidgets.QCheckBox("Auto-ranging")
        self.autoChk.setChecked(True)
        self.autoChk.toggled.connect(self._on_auto_toggle)
        bl.addWidget(self.autoChk)
        bl.addWidget(hint("Off locks the gear below. High Density Mode is "
                          "also suppressed, so nothing overrides your choice."))

        self.sIT = IndexSlider("INTEGRATION TIME", ["--"],
                               "Sensor integration time. Longer collects more "
                               "light, so it suits dense cultures.")
        self.sIT.committed.connect(lambda _i: self._send_gear())
        bl.addWidget(self.sIT)

        self.sPWM = IndexSlider("LED LEVEL", ["--"],
                                "One of the 8 calibrated duty levels. Only "
                                "these have a blank, so only these give "
                                "valid absorbance.")
        self.sPWM.committed.connect(lambda _i: self._send_gear())
        bl.addWidget(self.sPWM)

        self.gearHint = hint("")
        bl.addWidget(self.gearHint)
        lay.addWidget(box)

        box, bl = card("DIRECT LED (IDLE ONLY)")
        self.sLED = Slider("LED DUTY", 0, 100, 1, 0.5, " %",
                           "Drives the LED at an arbitrary duty for optical "
                           "alignment. Produces no valid absorbance.")
        self.sLED.committed.connect(lambda v: self._send_cmd_kw("led", duty=v))
        bl.addWidget(self.sLED)
        r = QtWidgets.QHBoxLayout()
        self.ledOffBtn = QtWidgets.QPushButton("LED off")
        self.ledOffBtn.clicked.connect(self._led_off)
        r.addWidget(self.ledOffBtn)
        self.testChk = QtWidgets.QPushButton("Sweep")
        self.testChk.setCheckable(True)
        self.testChk.toggled.connect(
            lambda on: self._cmd("test_on" if on else "test_off"))
        r.addWidget(self.testChk)
        bl.addLayout(r)
        self.sTestPeriod = Slider("SWEEP STEP", 5, 500, 0, 5, " ms")
        self.sTestPeriod.committed.connect(
            lambda v: self._send(test_period=int(v)))
        bl.addWidget(self.sTestPeriod)
        bl.addWidget(hint("There is no blank for an uncalibrated duty, so "
                          "absorbance cannot be computed here. Alignment and "
                          "LED checks only — stop the run first."))
        lay.addWidget(box)
        lay.addStretch(1)
        return w

    # -- tab: Ranging ----------------------------------------------------
    def _tab_ranging(self) -> QtWidgets.QWidget:
        w = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(w)
        lay.setSpacing(12)

        box, bl = card("AUTO-RANGE THRESHOLDS")
        self.sLow = Slider("LOW", 0, 65535, 0, 500,
                           tip="Below this raw count for 10 readings in a "
                               "row, the device searches for a more "
                               "sensitive gear.")
        self.sOpt = Slider("OPTIMAL TARGET", 0, 65535, 0, 500,
                           tip="The raw count a gear search aims for. Best "
                               "signal-to-noise is well below saturation.")
        self.sHigh = Slider("HIGH", 0, 65535, 0, 500,
                            tip="Above this raw count for 10 readings in a "
                                "row, the device searches for a less "
                                "sensitive gear.")
        for s in (self.sLow, self.sOpt, self.sHigh):
            s.committed.connect(self._send_thresholds)
            bl.addWidget(s)
        self.threshHint = hint("")
        bl.addWidget(self.threshHint)
        self.saveThresholdsBtn = QtWidgets.QPushButton("Save to device flash")
        self.saveThresholdsBtn.clicked.connect(lambda: self._cmd("save_config"))
        bl.addWidget(self.saveThresholdsBtn)
        bl.addWidget(hint("Threshold changes apply immediately but live in "
                          "RAM. Without saving, a reboot restores the stored "
                          "values."))
        lay.addWidget(box)

        box, bl = card("FILTERING")
        self.sEma = Slider("EMA COEFFICIENT", 0.01, 1.00, 2, 0.01, "",
                           "Low-pass on the median-filtered signal. 1.0 "
                           "disables it; lower is smoother but slower to "
                           "follow real change.")
        self.sEma.committed.connect(lambda v: self._send(ema=round(v, 2)))
        bl.addWidget(self.sEma)
        bl.addWidget(hint("Applied after a 5-sample median. The median kills "
                          "single-sample spikes (bubbles); the EMA smooths "
                          "what is left."))
        lay.addWidget(box)

        box, bl = card("HEALTH")
        self.healthLabel = QtWidgets.QLabel("--")
        self.healthLabel.setObjectName("hint")
        self.healthLabel.setWordWrap(True)
        bl.addWidget(self.healthLabel)
        # Anchoring failures are a correctness signal, not a nuisance counter:
        # a read that could not see a conversion boundary fell back to a blind
        # wait, which is the timing v5.0 exists to stop relying on. Called out
        # separately from the counters line so it cannot be skimmed past.
        self.boundaryWarn = QtWidgets.QLabel("")
        self.boundaryWarn.setObjectName("hint")
        self.boundaryWarn.setWordWrap(True)
        self.boundaryWarn.setVisible(False)
        bl.addWidget(self.boundaryWarn)
        self.resetHealthBtn = QtWidgets.QPushButton("Reset counters")
        self.resetHealthBtn.clicked.connect(lambda: self._cmd("reset_health"))
        bl.addWidget(self.resetHealthBtn)
        # Short label on purpose: this card sits in a fixed-width side panel,
        # and the Portuguese string for the full phrase pushes the panel past
        # the minimum supported window width. The dialog title carries the
        # long form.
        self.probeBtn = QtWidgets.QPushButton("Measure period")
        self.probeBtn.clicked.connect(self._probe_period)
        self.probeBtn.setVisible(False)
        bl.addWidget(self.probeBtn)
        self.probeHint = QtWidgets.QLabel("")
        self.probeHint.setObjectName("hint")
        self.probeHint.setWordWrap(True)
        self.probeHint.setVisible(False)
        bl.addWidget(self.probeHint)
        lay.addWidget(box)
        lay.addStretch(1)
        return w

    # -- tab: Advanced ---------------------------------------------------
    def _tab_advanced(self) -> QtWidgets.QWidget:
        w = QtWidgets.QWidget()
        lay = QtWidgets.QVBoxLayout(w)
        lay.setSpacing(12)

        box, bl = card("LED LEVEL TABLE")
        bl.addWidget(hint("The 8 duty levels auto-ranging can choose from. "
                          "Changing any of them ERASES the stored blank: the "
                          "blank is indexed by gear, so a moved level makes "
                          "its I0 wrong and every absorbance from it silently "
                          "incorrect."))
        grid = QtWidgets.QGridLayout()
        grid.setSpacing(6)
        self.pwmBoxes = []
        for i in range(8):
            sb = QtWidgets.QDoubleSpinBox()
            sb.setRange(0.0, 100.0)
            sb.setDecimals(1)
            sb.setSuffix(" %")
            sb.setFixedWidth(88)
            sb.editingFinished.connect(
                lambda idx=i: self._set_pwm_level(idx))
            lb = QtWidgets.QLabel(f"{i}")
            lb.setObjectName("caption")
            grid.addWidget(lb, i // 2, (i % 2) * 2)
            grid.addWidget(sb, i // 2, (i % 2) * 2 + 1)
            self.pwmBoxes.append(sb)
        bl.addLayout(grid)

        self.bPwmPreset = QtWidgets.QPushButton("Recommended levels")
        self.bPwmPreset.setToolTip(
            "Restores the geometric ladder (2 → 100 %, each step ~1.75x the "
            "light of the one below). Equal ratios mean equal absorbance "
            "steps, so auto-ranging moves evenly across the whole range.")
        self.bPwmPreset.clicked.connect(self._restore_pwm_preset)
        bl.addWidget(self.bPwmPreset)
        lay.addWidget(box)

        box, bl = card("INTEGRATION TIME TABLE")
        bl.addWidget(hint("The 4 integration-time slots, each chosen from the "
                          "VEML7700's supported values. Also erases the "
                          "blank."))
        grid = QtWidgets.QGridLayout()
        grid.setSpacing(6)
        self.itCombos = []
        self.slotLabels = []
        for i in range(4):
            cb = QtWidgets.QComboBox()
            for _code, label in IT_CHOICES:
                cb.addItem(label)
            cb.activated.connect(lambda _x, idx=i: self._set_it_slot(idx))
            lb = QtWidgets.QLabel(f"slot {i}")
            lb.setObjectName("caption")
            grid.addWidget(lb, i, 0)
            grid.addWidget(cb, i, 1)
            self.itCombos.append(cb)
            self.slotLabels.append(lb)
        bl.addLayout(grid)
        lay.addWidget(box)

        box, bl = card("BLANK CALIBRATION")
        self.blankInfo = QtWidgets.QLabel("--")
        self.blankInfo.setObjectName("hint")
        self.blankInfo.setWordWrap(True)
        bl.addWidget(self.blankInfo)
        b = QtWidgets.QPushButton("View blank table")
        b.clicked.connect(self._show_blank)
        bl.addWidget(b)
        lay.addWidget(box)

        box, bl = card("DEVICE MEMORY")
        # hint(), not a bare QLabel: its width hint is ignored, so a long
        # translated line wraps inside the sidebar instead of widening it.
        self.bufferInfo = hint("")
        bl.addWidget(self.bufferInfo)
        self.clearBufferBtn = QtWidgets.QPushButton("Clear device memory")
        self.clearBufferBtn.setObjectName("danger")
        self.clearBufferBtn.clicked.connect(self._clear_device_buffer)
        bl.addWidget(self.clearBufferBtn)
        bl.addWidget(hint("The sensor keeps its last samples in RAM so the app "
                          "can recover anything it missed. Clearing it drops "
                          "that history, which is the way to make sure a new "
                          "experiment cannot pick up the previous one. Saved "
                          "CSV files are not affected."))
        lay.addWidget(box)

        box, bl = card("DEVICE")
        self.saveConfigBtn = QtWidgets.QPushButton("Save config")
        self.saveConfigBtn.clicked.connect(lambda: self._cmd("save_config"))
        bl.addWidget(self.saveConfigBtn)
        self.reloadConfigBtn = QtWidgets.QPushButton("Reload config")
        self.reloadConfigBtn.clicked.connect(lambda: self._cmd("load_config"))
        bl.addWidget(self.reloadConfigBtn)
        self.factoryResetBtn = QtWidgets.QPushButton("Factory reset")
        self.factoryResetBtn.setObjectName("danger")
        self.factoryResetBtn.clicked.connect(self._factory_reset)
        bl.addWidget(self.factoryResetBtn)
        lay.addWidget(box)
        lay.addStretch(1)
        return w

    # ------------------------------------------------------------------
    # Device commands
    # ------------------------------------------------------------------
    def _set_locale(self):
        if self.language == PT_BR:
            locale = QtCore.QLocale(
                QtCore.QLocale.Language.Portuguese,
                QtCore.QLocale.Country.Brazil)
        else:
            locale = QtCore.QLocale(
                QtCore.QLocale.Language.English,
                QtCore.QLocale.Country.UnitedStates)
        for widget in self.findChildren(QtWidgets.QAbstractSpinBox):
            widget.setLocale(locale)

    @staticmethod
    def _set_legend_names(legend, names):
        for (_sample, label), name in zip(legend.items, names):
            label.setText(name)

    def _update_experiment_banner(self):
        """Shows whether a run is recording, under what name, and how big.

        Derived from the recorder itself rather than tracked separately, so
        the banner cannot disagree with what is actually being written.
        """
        if not hasattr(self, "expStatus"):
            return
        rec = self.client.recorder if hasattr(self, "client") else None
        th = THEMES[self.theme_name]
        if rec is not None:
            name = getattr(rec, "name", "") or self.run_name
            self.expStatus.setText(
                self._t("experiment_running", name=name, count=rec.count))
            self.expStatus.setStyleSheet(
                f"color:{th['ok']};font-weight:700;")
            self.expStatus.setToolTip(str(rec.csv_path))
        else:
            self.expStatus.setText(self._t("experiment_idle"))
            self.expStatus.setStyleSheet(f"color:{th['muted']};")
            self.expStatus.setToolTip("")

    def _retranslate_recording(self):
        recorder = self.client.recorder if hasattr(self, "client") else None
        if recorder:
            self.recBtn.setText(self._t("Stop recording"))
            set_path(self.recInfo, recorder.csv_path)
            self.statusBar().showMessage(self._t(
                "recording_samples", path=recorder.csv_path,
                count=recorder.count))
        elif self._last_saved:
            count, path = self._last_saved
            set_path(self.recInfo, path)
            self.recInfo.setText(self._t(
                "saved_samples", count=count, path=self.recInfo.text()))
            self.recBtn.setText(self._t("Start recording"))
            self.statusBar().showMessage(self._t("not recording"))
        else:
            self.recBtn.setText(self._t("Start recording"))
            self.recInfo.setText(self._t("not recording"))
            self.recInfo.setToolTip("")
            self.statusBar().showMessage(self._t("not recording"))
        self._update_experiment_banner()

    @staticmethod
    def _log_category(message: str) -> str:
        """Where a log line came from. The prefixes are set in biomass_core."""
        if message.startswith("[dev] "):
            return "device"
        if message.startswith("-> "):
            return "command"
        return "event"

    def _log_visible(self, category: str) -> bool:
        if category == "device":
            return self.showDeviceChk.isChecked()
        if category == "command":
            return self.showCmdChk.isChecked()
        return True          # app events, including every failure

    def _append_log_line(self, when: datetime, message: str):
        self.logBox.appendPlainText(
            f"{when:%H:%M:%S}  {translate_log(self.language, message)}")

    def _scroll_log_to_end(self):
        sb = self.logBox.verticalScrollBar()
        sb.setValue(sb.maximum())

    def _render_log(self):
        self.logBox.clear()
        shown = 0
        for when, message, category in self._log_entries:
            if self._log_visible(category):
                self._append_log_line(when, message)
                shown += 1
        self._scroll_log_to_end()
        self._hidden_count = len(self._log_entries) - shown
        self.logCount.setText(self._t("log_hidden", count=self._hidden_count)
                              if self._hidden_count else "")

    def _retranslate_ui(self):
        retranslate_tree(self, self.language)
        self._set_locale()
        self.languageBtn.setText(
            "English" if self.language == PT_BR else "Português (Brasil)")
        self.themeBtn.setText(self._t(
            "Dark" if self.theme_name == "light" else "Light"))
        connected = not hasattr(self, "timer") or self.timer.isActive()
        self.connBtn.setText(self._t("Disconnect" if connected else "Connect"))
        self.pauseBtn.setText(self._t(
            "Resume plot" if self._paused else "Pause plot"))

        for index, source in enumerate(("Run", "Optics", "Ranging", "Advanced")):
            self.sidebarTabs.setTabText(index, self._t(source))
        for index, label in enumerate(self.slotLabels):
            label.setText(self._t("slot {index}", index=index))

        self.p1.setLabel("left", self._t("Absorbance (AU)"))
        self.p2.setLabel("left", self._t("counts"))
        self.p2.setLabel("bottom", self._t("elapsed (h)"))
        self._set_legend_names(
            self.legend1, (self._t("absorbance"), self._t("single shot")))
        self._set_legend_names(
            self.legend2, (self._t("raw I"), self._t("blank I0")))

        self._retranslate_recording()
        if hasattr(self, "client") and self.client.status:
            self._on_status(self.client.status)
        else:
            self.chip.setText(self._t("connecting"))
            self.setWindowTitle(self._t("Biomass Sensor"))
        if self._threshold_invalid:
            self.threshHint.setText(self._t(
                "Not applied: the device needs low < optimal < high."))
        self._redraw()
        self._render_log()

    def _toggle_language(self):
        self.language = PT_BR if self.language == EN else EN
        self._retranslate_ui()

    def log(self, msg: str):
        entry = (datetime.now(), msg, self._log_category(msg))
        self._log_entries.append(entry)
        # Append the one new line rather than redrawing the whole box. The old
        # clear-and-refill was quadratic in the number of entries, which a
        # multi-day run at the bench is quite capable of noticing.
        if len(self._log_entries) > LOG_LIMIT:
            del self._log_entries[:len(self._log_entries) - LOG_TRIM_TO]
            self._render_log()
            return
        if self._log_visible(entry[2]):
            self._append_log_line(entry[0], entry[1])
            self._scroll_log_to_end()
            return
        self._hidden_count += 1
        if hasattr(self, "logCount"):
            self.logCount.setText(self._t("log_hidden",
                                          count=self._hidden_count))

    def _cmd(self, name: str, **kw):
        try:
            self.client.command(name, **kw)
        except TransportError as exc:
            self.log(f"command '{name}' failed: {exc}")

    def _send(self, **kw):
        try:
            self.client.set_param(**kw)
        except TransportError as exc:
            self.log(f"setting failed: {exc}")

    def _send_cmd_kw(self, name: str, **kw):
        self._cmd(name, **kw)

    def _debounced(self, key: str, fn: Callable, ms: int = 250):
        """
        Coalesces rapid slider changes into one command.

        The timer per key is created once and permanently wired to a
        dispatcher; only the pending callable is swapped. Reconnecting the
        signal on every call instead would mean disconnecting a signal that
        may have no connections, which PySide warns about.
        """
        self._debounce_fns[key] = fn
        t = self._debounce.get(key)
        if t is None:
            t = QtCore.QTimer(self)
            t.setSingleShot(True)
            t.timeout.connect(lambda k=key: self._fire_debounced(k))
            self._debounce[key] = t
        t.start(ms)

    def _fire_debounced(self, key: str):
        fn = self._debounce_fns.pop(key, None)
        if fn is not None:
            fn()

    def _send_thresholds(self, *_):
        if self._guard:
            return
        lo, op, hi = (int(self.sLow.value()), int(self.sOpt.value()),
                      int(self.sHigh.value()))
        if not (lo < op < hi):
            self._threshold_invalid = True
            self.threshHint.setText(
                self._t("Not applied: the device needs low < optimal < high."))
            return
        self._threshold_invalid = False
        self.threshHint.setText("")
        self._debounced("thr", lambda: self._send(low=lo, high=hi, opt=op))

    def _send_gear(self):
        if self._guard:
            return
        it, pwm = self.sIT.value(), self.sPWM.value()
        if self.autoChk.isChecked():
            self.gearHint.setText(
                self._t("Auto-ranging is on — the device will move off this gear "
                        "on its own. Turn it off to hold."))
        self._debounced("gear",
                        lambda: self._cmd("set_gear", it=it, pwm=pwm), 200)

    def _on_auto_toggle(self, on: bool):
        if self._guard:
            return
        self._cmd("auto" if on else "manual")
        self.gearHint.setText("" if not on else "")

    def _on_pause(self, on: bool):
        self._paused = on
        self.pauseBtn.setText(self._t("Resume plot" if on else "Pause plot"))
        if not on:
            self._redraw()

    def _led_off(self):
        self.sLED.set_value_silently(0)
        self.testChk.setChecked(False)
        self._cmd("led_off")

    def _confirm(self, title: str, message: str, warning: bool = False) -> bool:
        box = QtWidgets.QMessageBox(self)
        box.setWindowTitle(title)
        box.setText(message)
        box.setIcon(QtWidgets.QMessageBox.Icon.Warning if warning
                    else QtWidgets.QMessageBox.Icon.Question)
        yes = box.addButton(self._t("Yes"),
                            QtWidgets.QMessageBox.ButtonRole.AcceptRole)
        box.addButton(self._t("Cancel"),
                      QtWidgets.QMessageBox.ButtonRole.RejectRole)
        box.exec()
        return box.clickedButton() is yes

    def _send_blank(self, duty_pct: float):
        """Asks for a sweep, paced or not. One place, so the log agrees."""
        if duty_pct > 0:
            self._cmd("blank", duty_pct=duty_pct)
            self.log(f"blanking sweep requested, paced at {duty_pct:g}% "
                     f"LED duty (about 3 min)")
        else:
            self._cmd("blank")
            self.log("blanking sweep requested (fast, about 25 s)")

    def _ask_blank_mode(self, paced: bool) -> Optional[float]:
        """
        Which sweep to run: the duty to pace at, 0 for back to back, or None
        if the operator backed out.

        A seam of its own, like _confirm and _ask_relocate, so the choice can
        be driven in a test without a modal waiting for a click that will
        never come.
        """
        box = QtWidgets.QMessageBox(self)
        box.setWindowTitle(self._t("Run blanking sweep"))
        box.setIcon(QtWidgets.QMessageBox.Icon.Question)
        box.setText(self._t("blank_confirm" if paced else "blank_confirm_fast"))
        slow = (box.addButton(self._t("Slow sweep"),
                              QtWidgets.QMessageBox.ButtonRole.AcceptRole)
                if paced else None)
        fast = box.addButton(self._t("Fast sweep"),
                             QtWidgets.QMessageBox.ButtonRole.AcceptRole)
        box.addButton(self._t("Cancel"),
                      QtWidgets.QMessageBox.ButtonRole.RejectRole)
        box.setDefaultButton(slow or fast)
        box.exec()
        clicked = box.clickedButton()
        if clicked is slow and slow is not None:
            return SLOW_BLANK_DUTY_PCT
        return 0.0 if clicked is fast else None

    def _do_blank(self):
        paced = self._fw_tuple(self.client.status or {}) >= MIN_FW_PACED_BLANK
        duty = self._ask_blank_mode(paced)
        if duty is None:
            return
        # Armed even though nothing is pending afterwards: the wait is what
        # notices the sweep finished, and that is when the run's metadata has
        # to be re-stamped with the new blank.
        self._begin_blank_wait(start_after=False)
        self._send_blank(duty)

    def _probe_period(self):
        """Runs the v5.0 conversion-period diagnostic.

        The device answers with one JSON line per integration slot on the
        SERIAL port only -- probe_period arriving over HTTP is deferred to the
        main loop and the HTTP reply is just the status object, so the results
        would never reach an HTTP client. Rather than offer a button that
        quietly does nothing, say where the answer will appear, and over HTTP
        say that it will not.
        """
        # Gated on HTTP specifically, not on "is it serial": the demo has a
        # log channel too, and treating anything non-serial as unable to
        # report would wrongly warn in demo mode.
        http_only = isinstance(self.client.t, HttpTransport)
        ok = self._confirm(
            self._t("Measure conversion period"),
            self._t("probe_period_http") if http_only
            else self._t("probe_period_confirm"),
            warning=http_only)
        if not ok:
            return
        self.log(self._t("probe_period_started"))
        self._cmd("probe_period")

    def _clear_device_buffer(self):
        """Drops the device's ring buffer, after saying what that costs."""
        stored = int((self.client.status or {}).get("hist_stored", 0) or 0)
        if not self._confirm(self._t("Clear device memory"),
                             self._t("clear_buffer_warning", count=stored),
                             warning=True):
            return
        if self.client.clear_device_history():
            self.log(f"device memory cleared ({stored} sample(s) discarded)")
        else:
            # Unknown commands are ignored silently by the firmware, so a
            # buffer that did not empty is the only symptom of a device too
            # old to have the command at all.
            self.log("device memory not cleared -- the firmware may predate "
                     "the clear_history command")
        self._refresh_buffer_info(self.client.status)

    def _refresh_buffer_info(self, st: Optional[dict] = None):
        st = st if st is not None else (self.client.status or {})
        self.bufferInfo.setText(self._t(
            "device_buffer", stored=int(st.get("hist_stored", 0) or 0),
            size=int(st.get("hist_size", 0) or 0)))

    def _factory_reset(self):
        ok = self._confirm(
            self._t("Factory reset"),
            self._t("Reset all device settings to defaults and ERASE the blank "
                    "calibration?\n\nYou will have to re-blank before measuring."),
            warning=True)
        if ok:
            self._cmd("factory")

    def _set_pwm_level(self, idx: int):
        if self._guard:
            return
        val = self.pwmBoxes[idx].value()
        ok = self._confirm(
            self._t("Change LED level table"),
            self._t("set_pwm_warning", index=idx, value=val),
            warning=True)
        if ok:
            self._cmd("set_pwm", index=idx, value=val)
        else:
            self._refresh_tables_from_status()

    def _restore_pwm_preset(self):
        if not self._confirm(
                self._t("Restore recommended LED levels"),
                self._t("set_pwm_preset_warning"), warning=True):
            self._refresh_tables_from_status()
            return
        self._cmd("pwm_preset")

    def _set_it_slot(self, idx: int):
        if self._guard:
            return
        code = self.itCombos[idx].currentIndex()
        ok = self._confirm(
            self._t("Change integration time table"),
            self._t("set_it_warning", index=idx, value=IT_CHOICES[code][1]),
            warning=True)
        if ok:
            self._cmd("set_it", index=idx, code=code)
        else:
            self._refresh_tables_from_status()

    def _show_blank(self):
        try:
            b = self.t.get_blank_table()
        except TransportError as exc:
            self.log(f"could not read blank table: {exc}")
            return
        its = b.get("it_ms", [])
        pwms = b.get("pwm_pct", [])
        rows = b.get("i0", [])
        th = THEMES[self.theme_name]

        dlg = QtWidgets.QDialog(self)
        dlg.setWindowTitle(self._t("Blank calibration (I0)"))
        v = QtWidgets.QVBoxLayout(dlg)
        v.setContentsMargins(16, 16, 16, 16)
        v.setSpacing(12)

        head = QtWidgets.QLabel(self._t(
            "Sensor counts with clear media at each gear. Absorbance is "
            "-log10(I/I0) using the cell for the gear in use."))
        head.setObjectName("hint")
        head.setWordWrap(True)
        v.addWidget(head)

        tbl = QtWidgets.QTableWidget(len(rows), len(pwms) if pwms else 0)
        tbl.setHorizontalHeaderLabels([f"{p:g} %" for p in pwms])
        tbl.setVerticalHeaderLabels([f"{t:g} ms" for t in its])
        tbl.setEditTriggers(
            QtWidgets.QAbstractItemView.EditTrigger.NoEditTriggers)
        tbl.setSelectionMode(
            QtWidgets.QAbstractItemView.SelectionMode.NoSelection)
        tbl.setFocusPolicy(QtCore.Qt.FocusPolicy.NoFocus)
        tbl.verticalHeader().setSectionResizeMode(
            QtWidgets.QHeaderView.ResizeMode.Fixed)
        tbl.horizontalHeader().setSectionResizeMode(
            QtWidgets.QHeaderView.ResizeMode.Stretch)

        mono = QtGui.QFont("Consolas")
        mono.setStyleHint(QtGui.QFont.StyleHint.Monospace)
        st = self.client.status or {}
        cur = (st.get("it_index"), st.get("pwm_index"))
        n_sat = 0
        n_dim = 0

        for i, row in enumerate(rows):
            for j, val in enumerate(row):
                # Classified by the same rule the firmware selects gears with,
                # so the table cannot show a cell as usable that the device
                # will refuse. Before v5.0 only saturation was flagged, and a
                # stale blank of 9 counts looked like a perfectly good gear.
                cell = blank_cell_state(val)
                if cell == "saturated":
                    n_sat += 1
                    text = self._t("sat.")
                else:
                    if cell == "too_dim":
                        n_dim += 1
                    text = f"{val:,}".replace(",", " ")
                item = QtWidgets.QTableWidgetItem(text)
                item.setFont(mono)
                item.setTextAlignment(QtCore.Qt.AlignmentFlag.AlignRight |
                                      QtCore.Qt.AlignmentFlag.AlignVCenter)
                if cell == "saturated":
                    item.setForeground(QtGui.QColor(th["warn"]))
                    item.setBackground(QtGui.QColor(th["warn"] + "22"))
                    item.setToolTip(self._t("saturated during blanking"))
                elif cell == "too_dim":
                    item.setForeground(QtGui.QColor(th["warn"]))
                    item.setBackground(QtGui.QColor(th["warn"] + "22"))
                    item.setToolTip(self._t("blank_too_dim_tip"))
                elif (i, j) == cur:
                    # The gear actually in use, so the operator can see which
                    # number the live absorbance is being divided by.
                    item.setForeground(QtGui.QColor(th["accent"]))
                    item.setBackground(QtGui.QColor(th["accent"] + "26"))
                    item.setToolTip(self._t("gear currently in use"))
                tbl.setItem(i, j, item)

        tbl.resizeRowsToContents()
        # Guarantee every gear row is visible: the grid is only ever 4 rows,
        # so scrolling it would be a bug rather than a feature.
        rows_h = sum(tbl.rowHeight(i) for i in range(tbl.rowCount()))
        exact_h = (rows_h + tbl.horizontalHeader().height() +
                   2 * tbl.frameWidth() + 4)
        tbl.setFixedHeight(exact_h)
        v.addWidget(tbl, 0)
        v.addStretch(1)

        legend = QtWidgets.QLabel(self._t(
            "blank_legend", sat=n_sat, dim=n_dim,
            total=len(rows) * (len(pwms) or 1)))
        legend.setObjectName("hint")
        legend.setWordWrap(True)
        v.addWidget(legend)

        btns = QtWidgets.QHBoxLayout()
        btns.addStretch(1)
        close = QtWidgets.QPushButton(self._t("Close"))
        close.setObjectName("primary")
        close.clicked.connect(dlg.accept)
        btns.addWidget(close)
        v.addLayout(btns)

        dlg.resize(700, 360)
        dlg.exec()

    # ------------------------------------------------------------------
    # Experiment lifecycle
    # ------------------------------------------------------------------
    def _reset_plot_data(self):
        """Drops every plotted point and restarts the elapsed clock."""
        self.xs = np.empty(0)
        self.ys = np.empty(0)
        self.raws = np.empty(0)
        self.i0s = np.empty(0)
        self.singles = []
        self.single_y = []
        self.t0 = None
        # The client keeps its own list for backfill bookkeeping; letting it
        # grow across experiments would leak memory over a multi-day session.
        self.client.samples = []
        self.bigAbs.setText("--")
        for key in self.stat:
            self.stat[key].setText("--")
        self._redraw()
        self._autoscale()

    def _clear_plot_action(self):
        if self.xs.size and not self._confirm(
                self._t("Clear plot"),
                self._t("Discard the plotted data? The recorded file is "
                        "not affected.")):
            return
        self._reset_plot_data()
        self.log("plot cleared")

    def _new_experiment(self):
        """
        Full reset: close the current file, clear the plot, optionally
        re-zero, and begin recording into a folder the operator picks.
        """
        st = self.client.status or {}
        dlg = NewExperimentDialog(
            self, self.outdir, self.nameEdit.text().strip() or self.run_name,
            language=self.language,
            can_blank=bool(st) and st.get("state") in (None, "idle",
                                                       "measuring"),
            stored=int(st.get("hist_stored", 0) or 0),
            paced_blank=self._fw_tuple(st) >= MIN_FW_PACED_BLANK)
        if dlg.exec() != QtWidgets.QDialog.DialogCode.Accepted:
            return
        outdir, name, do_blank, do_start, seed, duty = dlg.result_values()

        # Stop the device before touching anything: a blank started while
        # measuring is refused by the firmware, and a run left going would
        # write into the old file during the changeover.
        self._cmd("stop")

        if self.client.recorder:
            self._stop_recording()

        self.outdir = Path(outdir)
        set_path(self.dirLabel, self.outdir)
        self.run_name = name
        self._set_name_silently(name)

        self._reset_plot_data()
        self._start_recording()
        self.log(f"new experiment '{name}'")

        if seed:
            # Deliberately after the file exists and the plot is empty, so
            # the device's own record of what happened before the operator
            # got here lands in this run with its original timestamps.
            recovered = self.client.replay_history(0)
            self.log(f"seeded from device memory: {recovered} sample(s)")
            self._redraw()
            self._autoscale()

        if do_blank:
            # The sweep runs on the device; _on_status starts measuring once
            # it reports idle again, so the UI never blocks on it.
            self._begin_blank_wait(start_after=do_start)
            self._send_blank(duty)
        else:
            self._await_blank = False
            self._pending_start = False
            if do_start:
                QtCore.QTimer.singleShot(400, lambda: self._cmd("start"))

    def _begin_blank_wait(self, start_after: bool,
                          timeout_s: int = 90) -> None:
        """
        Arms the wait for a blanking sweep.

        Kept as one call because the three pieces of state are only ever
        meaningful together -- arming the wait without a fresh deadline
        leaves a stale one in the past, and the very next status is then
        treated as a timeout.

        The deadline is only for the window before the sweep is OBSERVED, not
        for the sweep itself, so it can be generous. It was 25 s, which was
        shorter than a v5.0 sweep actually takes (~30 s: every pulse now waits
        out a dark conversion first). Any hiccup in status polling during that
        window ended the wait and the run never started.
        """
        self._await_blank = True
        self._blank_seen = False
        self._pending_start = start_after
        self._blank_deadline = datetime.now() + timedelta(seconds=timeout_s)
        # Positive evidence that a NEW blank was written, independent of
        # catching the transient "blanking" state in a status poll. The
        # firmware stamps this when it saves, so a changed value means the
        # sweep completed even if every poll happened to miss the state.
        self._blank_ts_before = self._read_blank_timestamp()

    def _read_blank_timestamp(self):
        """The stored blank's timestamp, or None if it cannot be read."""
        try:
            return self.client.t.get_blank_table().get("timestamp")
        except Exception:
            return None

    def _restamp_blank_table(self):
        """
        Re-reads the blank into the run's metadata after a sweep.

        The recorder captures the blank table when it opens, which for a new
        experiment is BEFORE the sweep it asked for. Left alone, meta.json
        documents the calibration the run replaced rather than the one every
        row was computed against -- and the two disagree by exactly the
        amount the re-zero moved.
        """
        rec = self.client.recorder
        if rec is None:
            return
        try:
            rec.set_meta(blank_table=self.client.t.get_blank_table())
        except (TransportError, OSError) as exc:
            self.log(f"could not read blank table: {exc}")

    def _service_pending_experiment(self, st: dict):
        """
        Starts measuring once a requested blanking sweep has finished.

        Completion is detected from the state transition, not from
        blank_done: a previous sweep may already have set that flag, so it
        says nothing about the sweep we just asked for.

        The device does not necessarily enter BLANKING the instant it is
        asked -- over HTTP the command is deferred to the main loop -- so a
        status arriving in that window would still read "idle" and would
        otherwise be mistaken for a finished sweep. Waiting until the sweep
        has actually been observed closes that race, and the deadline stops
        us waiting forever if the command never took.
        """
        if not self._await_blank:
            return
        state = st.get("state")

        if state in ("blanking", "searching"):
            self._blank_seen = True
            return

        if state == "idle" and not self._blank_seen:
            if datetime.now() > self._blank_deadline:
                self._await_blank = False
                # Never seeing the state does not prove the sweep never ran --
                # it may simply have fallen between polls. Ask the device
                # whether a new blank was actually written before reporting
                # failure, and honour the pending start if it was.
                ts_now = self._read_blank_timestamp()
                if (ts_now is not None
                        and ts_now != self._blank_ts_before
                        and st.get("blank_done")):
                    self.log("blanking complete (sweep finished between "
                             "status polls)")
                    self._restamp_blank_table()
                    if self._pending_start:
                        self._pending_start = False
                        QtCore.QTimer.singleShot(300,
                                                 lambda: self._cmd("start"))
                else:
                    self.log("device never started the blanking sweep")
            return

        if state == "idle":
            self._await_blank = False
            if not st.get("blank_done"):
                self.log("blanking did not produce a valid blank")
                return
            # Reaching idle with blank_done set is not proof the sweep
            # finished: "stop" during one aborts it and the firmware restores
            # the previous table, which still reports blank_done. Only a
            # changed timestamp means a new table was written -- and a paced
            # sweep runs for three minutes, which is plenty of time for an
            # operator to think better of it.
            if self._read_blank_timestamp() == self._blank_ts_before:
                self.log("blanking aborted; the previous calibration is "
                         "still in use")
                self._pending_start = False
                return
            self.log("blanking complete")
            self._restamp_blank_table()
            if self._pending_start:
                self._pending_start = False
                QtCore.QTimer.singleShot(300, lambda: self._cmd("start"))

    def _add_note(self):
        text = self.noteEdit.text().strip()
        if not text:
            return
        if not self.client.recorder:
            self.log("note ignored: not recording")
            return
        self.client.annotate(text)
        self.noteEdit.clear()

    # ------------------------------------------------------------------
    # Recording
    # ------------------------------------------------------------------
    def _pick_dir(self):
        if self._relocating:
            return
        d = QtWidgets.QFileDialog.getExistingDirectory(
            self, self._t("Output folder"), str(self.outdir))
        if d:
            self._relocate_run(Path(d),
                               self.nameEdit.text().strip() or self.run_name)

    def _on_run_name_edited(self):
        """A retyped run name has to reach the file, not just the label."""
        if self._guard or self._relocating:
            return
        name = self.nameEdit.text().strip()
        if not name or name == self.run_name:
            return
        if not self._relocate_run(self.outdir, name):
            self._set_name_silently(self.run_name)

    def _set_name_silently(self, name: str):
        self._guard = True
        self.nameEdit.setText(name)
        self._guard = False

    def _ask_relocate(self, count: int) -> str:
        """Move / start empty / cancel, for a run that changed identity."""
        box = QtWidgets.QMessageBox(self)
        box.setWindowTitle(self._t("Move the recording"))
        box.setIcon(QtWidgets.QMessageBox.Icon.Warning)
        box.setText(self._t("relocate_warning", count=count))
        move = box.addButton(self._t("Move the data"),
                             QtWidgets.QMessageBox.ButtonRole.AcceptRole)
        fresh = box.addButton(self._t("Start a new file"),
                              QtWidgets.QMessageBox.ButtonRole.ActionRole)
        box.addButton(self._t("Cancel"),
                      QtWidgets.QMessageBox.ButtonRole.RejectRole)
        box.exec()
        clicked = box.clickedButton()
        return "move" if clicked is move else \
               "new" if clicked is fresh else "cancel"

    def _relocate_run(self, outdir: Path, name: str) -> bool:
        """
        Points the run at a new folder and/or name, live.

        Returns False only if the operator cancelled, so the widget that
        asked can put itself back: a name box showing a name nothing is being
        saved under is the confusion this exists to remove.
        """
        outdir = Path(outdir)
        rec = self.client.recorder
        if rec is None:                      # nothing being written yet
            self.outdir, self.run_name = outdir, name
            set_path(self.dirLabel, self.outdir)
            self._set_name_silently(name)
            return True
        if outdir == self.outdir and name == self.run_name:
            return True

        # Held across the whole exchange: the modal below takes focus off the
        # name box, and the editingFinished that fires as it does would
        # otherwise open a second copy of this dialog on top of the first.
        self._relocating = True
        try:
            choice = self._ask_relocate(rec.count)
        finally:
            self._relocating = False
        if choice == "cancel":
            return False

        old_dir, old_csv, old_meta = rec.dir, rec.csv_path, dict(rec.meta)
        self._stop_recording()
        self.outdir, self.run_name = outdir, name
        set_path(self.dirLabel, self.outdir)
        self._set_name_silently(name)
        self._start_recording()

        new_rec = self.client.recorder
        if choice == "move" and new_rec is not None:
            moved = new_rec.copy_rows_from(old_csv)
            # The run did not restart, it moved: keep the original start time
            # and the notes taken under the old name, or the elapsed clock in
            # the file would disagree with the samples in it.
            carried = {k: old_meta[k] for k in
                       ("started", "operator_notes", "blank_table",
                        "time_anchor_epoch", "time_anchor_note")
                       if k in old_meta}
            carried["moved_from"] = str(old_dir)
            carried["moved_samples"] = moved
            new_rec.set_meta(**carried)
            removed = self._discard_run_folder(old_dir)
            self.log(f"moved {moved} sample(s) to {new_rec.csv_path}"
                     + ("" if removed else f" (left {old_dir} in place)"))
        else:
            self.log(f"previous file kept at {old_csv}")
        self._retranslate_recording()
        return True

    @staticmethod
    def _discard_run_folder(folder: Path) -> bool:
        """
        Removes a run folder whose rows have been moved elsewhere.

        Deliberately refuses anything holding a file this app did not write.
        A stray folder is a much smaller problem than deleting data.
        """
        try:
            contents = list(folder.iterdir())
        except OSError:
            return False
        if not {p.name for p in contents} <= {"data.csv", "meta.json"}:
            return False
        try:
            for p in contents:
                p.unlink()
            folder.rmdir()
        except OSError:
            return False
        return True

    def _start_recording(self):
        name = self.nameEdit.text().strip() if hasattr(self, "nameEdit") \
            else self.run_name
        rec = Recorder(self.outdir, name or "run")
        self.run_name = name or "run"
        self._last_saved = None
        try:
            self.client.attach_recorder(rec)
        except Exception as exc:                 # disk or transport hiccup
            self.client.recorder = rec
            self.log(f"run metadata incomplete: {exc}")
        self.log(f"recording to {rec.csv_path}")
        set_path(self.recInfo, rec.csv_path)
        self.statusBar().showMessage(self._t(
            "recording_path", path=rec.csv_path))
        if hasattr(self, "recBtn"):
            self._guard = True
            self.recBtn.setChecked(True)
            self.recBtn.setText(self._t("Stop recording"))
            self._guard = False
        self._update_experiment_banner()

    def _stop_recording(self):
        rec = self.client.recorder
        if rec:
            rec.close()
            self.log(f"stopped recording ({rec.count} samples) -> {rec.csv_path}")
            self._last_saved = (rec.count, rec.csv_path)
        self.client.recorder = None
        self._retranslate_recording()

    def _on_record_toggle(self, on: bool):
        if self._guard:
            return
        if on:
            self._start_recording()
        else:
            self._stop_recording()

    # ------------------------------------------------------------------
    # Data / plotting
    # ------------------------------------------------------------------
    def _on_sample(self, s: Sample):
        if self.t0 is None:
            self.t0 = s.wall_epoch
        x = (s.wall_epoch - self.t0) / 3600.0
        y = s.absorbance if s.valid else np.nan
        self.xs = np.append(self.xs, x)
        self.ys = np.append(self.ys, y)
        self.raws = np.append(self.raws, s.raw)
        self.i0s = np.append(self.i0s, s.i0)
        if s.single and s.valid:
            self.singles.append(x)
            self.single_y.append(s.absorbance)

        self.bigAbs.setText(f"{s.absorbance:.3f}" if s.valid else self._t("ERR"))
        self.stat["raw"].setText(str(s.raw))
        self.stat["i0"].setText(str(s.i0))
        tr = s.transmittance
        self.stat["tr"].setText("--" if tr is None else f"{tr * 100:.1f} %")
        self.stat["it"].setText(f"{s.it_ms} ms")
        self.stat["pwm"].setText(f"{s.pwm_pct:.1f} %")
        self.stat["seq"].setText(str(s.seq))
        if self.client.recorder:
            self.statusBar().showMessage(
                self._t("recording_samples",
                        path=self.client.recorder.csv_path,
                        count=self.client.recorder.count))
            self._update_experiment_banner()

    def _visible_mask(self):
        if not self._span_s or self.xs.size == 0:
            return slice(None)
        cut = self.xs[-1] - self._span_s / 3600.0
        return self.xs >= cut

    def _redraw(self):
        if self._paused:
            return
        m = self._visible_mask()
        xs, ys = self.xs[m], self.ys[m]
        self.curveA.setData(xs, ys)
        self.curveRaw.setData(xs, self.raws[m])
        self.curveI0.setData(xs, self.i0s[m])
        if self.singles:
            sx = np.array(self.singles)
            sy = np.array(self.single_y)
            if self._span_s and self.xs.size:
                cut = self.xs[-1] - self._span_s / 3600.0
                keep = sx >= cut
                sx, sy = sx[keep], sy[keep]
            self.scatterSingle.setData(sx, sy)
        self.npts.setText(self._t(
            "{visible} of {total} points", visible=xs.size, total=self.xs.size))
        finite = ys[np.isfinite(ys)] if ys.size else ys
        self.spanInfo.setText(self._t(
            "plot_span", lo=f"{finite.min():.4f}", hi=f"{finite.max():.4f}",
            span=f"{(finite.max() - finite.min()) * 1000:.2f}")
            if finite.size else "")

    def _set_span(self, secs: int, btn):
        self._span_s = secs
        for b in self._span_buttons:
            b.setChecked(b is btn)
        self._redraw()
        self._autoscale()

    def _autoscale(self):
        self.p1.enableAutoRange()
        self.p2.enableAutoRange()

    def _export_image(self):
        path, _ = QtWidgets.QFileDialog.getSaveFileName(
            self, self._t("Export plot"), str(self.outdir / "absorbance.png"),
            self._t("PNG image (*.png)"))
        if not path:
            return
        try:
            pixmap = self.p1.grab()
            pixmap.save(path)
            self.log(f"plot exported to {path}")
        except Exception as exc:
            self.log(f"export failed: {exc}")

    # ------------------------------------------------------------------
    # Status sync
    # ------------------------------------------------------------------
    def _fw_tuple(self, st: dict) -> tuple:
        # The firmware reports its version with a leading "v" ("v11.1"), so a
        # bare int() on the first field raised ValueError and every capability
        # gated on this -- the paced blank among them -- silently fell back to
        # the "oldest firmware" branch. Pull the leading digit run out of each
        # of the first two dotted fields; anything unparseable counts as 0.
        out = []
        for part in str(st.get("fw", "0")).split(".")[:2]:
            digits = ""
            for ch in part:
                if ch.isdigit():
                    digits += ch
                elif digits:
                    break
            out.append(int(digits) if digits else 0)
        return tuple(out)

    def _on_status(self, st: dict):
        self._guard = True
        try:
            state = st.get("state", "?")
            idle = state == "idle"
            simulated = bool(st.get("simulated"))
            colors = {"idle": "muted", "measuring": "ok", "blanking": "warn",
                      "searching": "warn"}
            th = THEMES[self.theme_name]
            col = th[colors.get(state, "muted")]
            state_source = f"state_{state}"
            state_text = self._t(state_source)
            if state_text == state_source:
                state_text = self._t("state_unknown")
            label = ("DEMO / " if simulated else "") + state_text
            label += " / " + self._t("high-density") if st.get("hd_mode") else ""
            if not st.get("auto_range", True):
                label += " / " + self._t("manual")
            self.chip.setText(label)
            self.chip.setStyleSheet(
                f"color:{col};border-color:{col};padding:4px 12px;"
                f"border-radius:10px;font-weight:700;font-size:11px;"
                f"border:1px solid {col};")

            self.devinfo.setText(
                ((self._t("SIMULATED") + "  ·  ") if simulated else "") +
                self._t("device_info", fw=st.get("fw", "?"),
                        boot=st.get("boot_id", "?"),
                        uptime=self._fmt_uptime(st.get("uptime_ms", 0)),
                        heap=int(st.get("free_heap", 0)) // 1024))

            self.bStart.setEnabled(idle and bool(st.get("blank_done")))
            self.bStop.setEnabled(not idle)
            self.bBlank.setEnabled(idle)
            self.hubChk.setChecked(bool(st.get("hub_enabled")))

            # -- firmware capability gate
            fw = self._fw_tuple(st)
            manual_ok = fw >= MIN_FW_MANUAL
            capability_changed = manual_ok != self._fw_ok_manual
            self._fw_ok_manual = manual_ok
            self._set_manual_enabled(manual_ok, st.get("fw", "?"), state)
            if capability_changed and not manual_ok:
                self.log(f"firmware v{st.get('fw', '?')} predates manual "
                         "control; those controls are disabled")

            # Bound the control to what the firmware will actually accept.
            # The floor depends on the longest integration time reachable,
            # so it moves when the gear lock changes.
            if "min_refresh_ms" in st:
                # Round UP to the control's own resolution. The spin box holds
                # one decimal, so a raw floor of 24.325 s would present as
                # 24.3 -- a value the user can select and the device then
                # clamps. The control must never offer an illegal value.
                raw_s = int(st["min_refresh_ms"]) / 1000.0
                step = self.sInterval.resolution
                floor_s = max(1.0, math.ceil(raw_s / step - 1e-9) * step)
                self.sInterval.set_minimum(floor_s)
                duty = float(st.get("led_duty_limit", 0.08)) * 100.0
                self.intervalHint.setText(self._t(
                    "interval_floor", floor=f"{floor_s:g}", duty=f"{duty:g}"))
            if "refresh_ms" in st:
                self.sInterval.set_value_silently(
                    int(st["refresh_ms"]) / 1000.0)
                self.stat["rate"].setText(f"{int(st['refresh_ms']) / 1000:g} s")
            for key, w in (("low", self.sLow), ("opt", self.sOpt),
                           ("high", self.sHigh)):
                if key in st:
                    w.set_value_silently(int(st[key]))
            if "ema" in st:
                self.sEma.set_value_silently(float(st["ema"]))
            if "auto_range" in st:
                self.autoChk.setChecked(bool(st["auto_range"]))
                locked = not bool(st["auto_range"])
                self.gearHint.setText(
                    self._t("Gear locked. Absorbance stays valid because this gear is "
                            "in the calibrated table.") if locked else "")
            if "led_duty" in st and state == "idle":
                self.sLED.set_value_silently(float(st["led_duty"]))
            if "led_test" in st:
                self.testChk.setChecked(bool(st["led_test"]))
            if "test_period" in st:
                self.sTestPeriod.set_value_silently(int(st["test_period"]))

            self._refresh_tables_from_status(st)

            self.healthLabel.setText(
                self._t("health_status", i2c=st.get("i2c_errors", 0),
                        sat=st.get("saturation_events", 0),
                        resets=st.get("sensor_resets", 0),
                        failed=st.get("failed_searches", 0),
                        temp=(f"{float(st['soc_temp_c']):.0f} °C"
                              if "soc_temp_c" in st else "--")))

            # Anchoring is what makes a reading trustworthy on v5.0, so a
            # non-zero miss count is shown as a warning rather than folded in
            # with the routine counters. Absent on older firmware: hide it
            # rather than report a reassuring zero the device never sent.
            misses = st.get("boundary_misses")
            if misses:
                self.boundaryWarn.setText(self._t("boundary_misses_warn",
                                                  n=int(misses)))
                self.boundaryWarn.setStyleSheet(f"color:{th['warn']};")
                self.boundaryWarn.setVisible(True)
            else:
                self.boundaryWarn.setVisible(False)

            probe_ok = fw >= MIN_FW_PROBE
            self.probeBtn.setVisible(probe_ok)
            self.probeBtn.setEnabled(probe_ok and idle)
            self.probeHint.setVisible(probe_ok)
            if probe_ok:
                self.probeHint.setText(self._t("probe_period_hint"))
            self.blankInfo.setText(
                self._t("Blank present.") if st.get("blank_done")
                else self._t("No valid blank stored — run a blanking sweep before "
                             "measuring."))
            self._refresh_buffer_info(st)

            title = self._t("Biomass Sensor") + (" [DEMO]" if simulated else "")
            self.setWindowTitle(f"{title} — {label}")

            self._service_pending_experiment(st)
        finally:
            self._guard = False

    def _set_manual_enabled(self, on: bool, fw: str, state: str = "idle"):
        """Apply firmware and device-state gates to v4.1-only controls."""
        idle = state == "idle"
        for w in (self.sIT, self.sPWM):
            w.set_enabled(on)
        for w in (self.sLED, self.sTestPeriod):
            w.set_enabled(on and idle)
        self.autoChk.setEnabled(on)
        self.bOnce.setEnabled(on and idle)
        self.testChk.setEnabled(on and idle)
        for sb in self.pwmBoxes:
            sb.setEnabled(on and idle)
        for cb in self.itCombos:
            cb.setEnabled(on and idle)
        self.fwWarn.setVisible(not on)
        if not on:
            self.fwWarn.setText(
                self._t("firmware_warning", fw=fw,
                        required=f"{MIN_FW_MANUAL[0]}.{MIN_FW_MANUAL[1]}"))
        else:
            self.fwWarn.clear()

    def _refresh_tables_from_status(self, st: Optional[dict] = None):
        st = st if st is not None else self.client.status
        if not st:
            return
        was = self._guard
        self._guard = True
        try:
            pwm_table = st.get("pwm_table")
            if pwm_table:
                self.sPWM.set_labels([f"{v:g} %" for v in pwm_table])
                for i, v in enumerate(pwm_table[:len(self.pwmBoxes)]):
                    self.pwmBoxes[i].setValue(float(v))
            it_table = st.get("it_table")
            if it_table:
                self.sIT.set_labels([f"{v:g} ms" for v in it_table])
                for i, v in enumerate(it_table[:len(self.itCombos)]):
                    if int(v) in IT_MS:
                        self.itCombos[i].setCurrentIndex(IT_MS.index(int(v)))
            if "it_index" in st:
                self.sIT.set_value_silently(int(st["it_index"]))
            if "pwm_index" in st:
                self.sPWM.set_value_silently(int(st["pwm_index"]))
        finally:
            self._guard = was

    @staticmethod
    def _fmt_uptime(ms: int) -> str:
        s = int(ms) // 1000
        d, s = divmod(s, 86400)
        h, s = divmod(s, 3600)
        m = s // 60
        return (f"{d}d " if d else "") + f"{h}h {m}m"

    # ------------------------------------------------------------------
    # Lifecycle
    # ------------------------------------------------------------------
    def _tick(self):
        try:
            new = self.client.poll()
        except TransportError as exc:
            self.log(f"poll error: {exc}")
            return
        if new:
            self._redraw()

    def _toggle_connection(self):
        if self.timer.isActive():
            self.timer.stop()
            try:
                self.t.close()
            except Exception:
                pass
            self.connBtn.setText(self._t("Connect"))
            self.chip.setText(self._t("disconnected"))
            self.log("disconnected")
        else:
            try:
                self.t.open()
                self.client.prime()
                self.timer.start(int(self.interval * 1000))
                self.connBtn.setText(self._t("Disconnect"))
                self.log("reconnected")
            except Exception as exc:
                self.log(f"reconnect failed: {exc}")

    def _toggle_theme(self):
        self.theme_name = "light" if self.theme_name == "dark" else "dark"
        self._apply_theme()
        self._retranslate_ui()

    def _apply_theme(self):
        th = THEMES[self.theme_name]
        QtWidgets.QApplication.instance().setStyleSheet(QSS.format(**th))
        for p in (self.p1, self.p2):
            p.setBackground(th["plot_bg"])
            for ax in ("left", "bottom"):
                p.getAxis(ax).setPen(pg.mkPen(th["grid"]))
                p.getAxis(ax).setTextPen(pg.mkPen(th["muted"]))
        self.curveA.setPen(pg.mkPen(th["trace"], width=2))
        self.curveRaw.setPen(pg.mkPen(th["trace2"], width=1.5))
        self.curveI0.setPen(pg.mkPen(th["trace3"], width=1,
                                     style=QtCore.Qt.PenStyle.DashLine))
        self.scatterSingle.setBrush(pg.mkBrush(th["warn"]))
        self.scatterSingle.setPen(pg.mkPen(th["warn"]))

    def closeEvent(self, ev):
        self.timer.stop()
        if self.client.recorder:
            self._stop_recording()
        try:
            self.t.close()
        except Exception:
            pass
        super().closeEvent(ev)


# ==========================================================================
# Entry
# ==========================================================================

def launch(args) -> int:
    app = QtWidgets.QApplication.instance() or QtWidgets.QApplication([])
    app.setStyleSheet(QSS.format(**THEMES[args.theme]))
    language = normalize_language(getattr(args, "language", EN))

    transport: Optional[Transport] = None
    if getattr(args, "demo", False):
        from biomass_demo import DemoTransport
        transport = DemoTransport()
    elif args.serial:
        transport = SerialTransport(args.serial, baud=args.baud)
    elif args.http:
        transport = HttpTransport(args.http)

    if transport is not None:
        try:
            transport.open()
            transport.get_status()
        except Exception as exc:
            QtWidgets.QMessageBox.critical(
                None, tr(language, "Connection failed"), str(exc))
            transport = None

    if transport is None:
        dlg = ConnectDialog(default_host=DEFAULT_HTTP_HOST,
                            prefer=getattr(args, "prefer", "serial"),
                            language=language)
        if getattr(args, "auto", False):
            QtCore.QTimer.singleShot(0, dlg._auto)
        if dlg.exec() != QtWidgets.QDialog.DialogCode.Accepted:
            return 1
        transport = dlg.transport
        language = dlg.language

    win = MainWindow(transport, Path(args.outdir), args.name,
                     interval=args.interval,
                     autosave=getattr(args, "autosave", False),
                     theme=args.theme, language=language)
    win.show()
    if args.start:
        QtCore.QTimer.singleShot(1500, lambda: win._cmd("start"))
    return app.exec()
