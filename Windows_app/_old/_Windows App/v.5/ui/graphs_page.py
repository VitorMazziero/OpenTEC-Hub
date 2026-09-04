#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# graphs_page.py

from PySide6.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout, QTabWidget, QPushButton
from PySide6.QtCore import QTimer, Qt
import pyqtgraph as pg
from collections import deque

class GraphsPage(QWidget):
    def __init__(self, comm_handler, parameter_settings_page, configurations_page):
        super().__init__()
        self.comm_handler = comm_handler
        self.parameter_settings_page = parameter_settings_page
        self.configurations_page = configurations_page

        # Main vertical layout
        main_layout = QVBoxLayout(self)

        # Two-column graph area, each column can hold top + bottom graph slots
        graphs_layout = QHBoxLayout()

        self.left_column_layout = QVBoxLayout()
        self.right_column_layout = QVBoxLayout()

        self.left_column_layout.setContentsMargins(0, 0, 0, 0)
        self.right_column_layout.setContentsMargins(0, 0, 0, 0)
        self.left_column_layout.setSpacing(2)
        self.right_column_layout.setSpacing(2)

        graphs_layout.setContentsMargins(0, 0, 0, 0)
        graphs_layout.setSpacing(3)
        main_layout.setContentsMargins(0, 0, 0, 0)
        main_layout.setSpacing(2)

        graphs_layout.addLayout(self.left_column_layout)
        graphs_layout.addLayout(self.right_column_layout)

        main_layout.addLayout(graphs_layout)

        self.graph_slots = [None, None, None, None]
        self.slot_visible = [True, True, False, False]

        self._create_graph_slot(0, self.left_column_layout, is_top=True)
        self._create_graph_slot(2, self.left_column_layout, is_top=False)

        self._create_graph_slot(1, self.right_column_layout, is_top=True)
        self._create_graph_slot(3, self.right_column_layout, is_top=False)

        # Data buffers shared across both graphs (timestamp vs value)
        self.data_buffers = {}
        self.MAX_POINTS = 1000
        self._last_active_params = None

        # Each slot owns its own tab widget and tab-data dictionary
        # Access through self.graph_slots[slot_index]["tab_widget"]
        # and self.graph_slots[slot_index]["tabs_data"]

        # Timer to periodically update
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.update_data)

        # Configuration for monitored parameters
        self.monitored_params = {
            "Temperatura": {
                "active": lambda: self.parameter_settings_page.temp_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_temperature_setpoint,
                "unit": "°C", "pen": 'r', "data_key": "Tempval"
            },
            "Motor RPM": {
                "active": lambda: self.parameter_settings_page.motor_block.checkbox.isChecked(),
                "setpoint": None,
                "unit": "RPM", "pen": 'g', "data_key": "Motor"
            },
            "pH": {
                "active": lambda: self.parameter_settings_page.ph_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_ph_setpoint,
                "unit": "", "pen": 'b', "data_key": "pHval"
            },
            "Oxigênio": {
                "active": lambda: self.parameter_settings_page.oxy_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_oxygen_setpoint,
                "unit": "%", "pen": 'm', "data_key": "Oxyval"
            },
            "Antiespumante": {
                "active": lambda: self.parameter_settings_page.antifoam_block.checkbox.isChecked(),
                "setpoint": None,
                "unit": "", "pen": 'orange', "data_key": "Antifoam"
            },
            "Pressão": {
                "active": lambda: self.parameter_settings_page.pressure_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_pressure_setpoint,
                "unit": "mmHg", "pen": 'c', "data_key": "Pressure"
            },
            "Fluxômetro": {
                "active": lambda: self.parameter_settings_page.flow_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_flow_setpoint,
                "unit": "", "pen": 'y', "data_key": "FlowRate"
            },
            "Distância": {
                "active": lambda: self.parameter_settings_page.distance_block.checkbox.isChecked(),
                "setpoint": self.parameter_settings_page.get_distance_min,
                "unit": "cm", "pen": 'w', "data_key": "Distance"
            },
            "OUR": {
                "active": lambda: self.parameter_settings_page.oxy_kla_cascade_checkbox.isChecked(),
                "setpoint": None,
                "unit": "", "pen": 'w', "data_key": "OUR"
            },
            "Absorbância": {
                "active": lambda: self.parameter_settings_page.biomass_block.checkbox.isChecked(),
                "setpoint": None,
                "unit": "Abs", "pen": '#00796B', "data_key": "Absorbancia"
            },
            "Volume Bomba": {
                "active": lambda: self.parameter_settings_page.extern_pump_block.checkbox.isChecked(),
                "setpoint": None,
                "unit": "mL", "pen": '#FFB300', "data_key": "PumpVol" # Laranja/Ambar
            },
            "Vazão Bomba": {
                "active": lambda: self.parameter_settings_page.extern_pump_block.checkbox.isChecked(),
                "setpoint": None,
                "unit": "mL/min", "pen": '#8E24AA', "data_key": "PumpFlow" # Roxo
            }
        }

        self.main_window = None
        self.last_elapsed = None
        self.update_tabs()
        self._reposition_overlay_buttons()

    def refresh_graph_layout(self):
        self._last_active_params = None
        self.update_tabs()

    def _create_graph_slot(self, slot_index: int, parent_layout: QVBoxLayout, is_top: bool):
        container = QWidget()
        vbox = QVBoxLayout(container)
        vbox.setContentsMargins(0, 0, 0, 0)
        vbox.setSpacing(0)

        tab_widget = QTabWidget()
        vbox.addWidget(tab_widget)

        plus_button = None
        minus_button = None

        if is_top:
            plus_button = QPushButton("+", container)
            plus_button.setFixedSize(28, 28)
            plus_button.clicked.connect(lambda _, idx=slot_index: self.add_bottom_graph(idx))
            plus_button.setStyleSheet("""
                QPushButton {
                    background-color: rgba(30, 30, 30, 65);
                    color: white;
                    border: 1px solid rgba(255,255,255,45);
                    border-radius: 6px;
                    font-weight: bold;
                }
                QPushButton:hover {
                    background-color: rgba(30, 30, 30, 210);
                    border: 1px solid rgba(255,255,255,110);
                }
            """)
        else:
            minus_button = QPushButton("−", container)
            minus_button.setFixedSize(28, 28)
            minus_button.clicked.connect(lambda _, idx=slot_index: self.remove_graph_slot(idx))
            minus_button.setStyleSheet("""
                QPushButton {
                    background-color: rgba(30, 30, 30, 65);
                    color: white;
                    border: 1px solid rgba(255,255,255,45);
                    border-radius: 6px;
                    font-weight: bold;
                }
                QPushButton:hover {
                    background-color: rgba(30, 30, 30, 210);
                    border: 1px solid rgba(255,255,255,110);
                }
            """)

        container.setLayout(vbox)

        parent_layout.addWidget(container, 1)

        self.graph_slots[slot_index] = {
            "container": container,
            "tab_widget": tab_widget,
            "tabs_data": {},
            "plus_button": plus_button,
            "minus_button": minus_button,
            "is_top": is_top,
        }

        container.setVisible(self.slot_visible[slot_index])

    def _reposition_overlay_buttons(self):
        for slot in self.graph_slots:
            if slot is None:
                continue

            container = slot["container"]
            margin = 8

            if slot["plus_button"] is not None:
                btn = slot["plus_button"]
                btn.raise_()
                btn.move(
                    container.width() - btn.width() - margin,
                    margin + 18
                )

            if slot["minus_button"] is not None:
                btn = slot["minus_button"]
                btn.raise_()
                btn.move(
                    container.width() - btn.width() - margin,
                    margin + 18
                )

    def resizeEvent(self, event):
        super().resizeEvent(event)
        self._reposition_overlay_buttons()

    def add_bottom_graph(self, top_slot_index: int):
        """
        top_slot_index must be 0 (left top) or 1 (right top).
        It enables the bottom graph in the same column.
        """
        if top_slot_index == 0:
            self.slot_visible[2] = True
            self.graph_slots[2]["container"].setVisible(True)
            if self.graph_slots[0]["plus_button"] is not None:
                self.graph_slots[0]["plus_button"].hide()

        elif top_slot_index == 1:
            self.slot_visible[3] = True
            self.graph_slots[3]["container"].setVisible(True)
            if self.graph_slots[1]["plus_button"] is not None:
                self.graph_slots[1]["plus_button"].hide()

        self.update_tabs()
        self._reposition_overlay_buttons()

    def remove_graph_slot(self, slot_index: int):
        """
        slot_index must be 2 (bottom left) or 3 (bottom right).
        Hides the slot and removes its tabs.
        """
        if slot_index not in (2, 3):
            return

        slot = self.graph_slots[slot_index]
        tab_widget = slot["tab_widget"]
        tabs_data = slot["tabs_data"]

        # Remove all visible tabs from this slot
        for param in list(tabs_data.keys()):
            widget = tabs_data[param]["widget"]
            index = tab_widget.indexOf(widget)
            if index != -1:
                tab_widget.removeTab(index)
            del tabs_data[param]

        self.slot_visible[slot_index] = False
        slot["container"].setVisible(False)

        if slot_index == 2 and self.graph_slots[0]["plus_button"] is not None:
            self.graph_slots[0]["plus_button"].show()
        elif slot_index == 3 and self.graph_slots[1]["plus_button"] is not None:
            self.graph_slots[1]["plus_button"].show()

        self._reposition_overlay_buttons()

    def update_tabs(self):
        """
        Ensure that each active parameter has a tab in every visible graph slot,
        and inactive parameters are removed from all slots.
        """
        visible_slots = [
            self.graph_slots[i]
            for i in range(len(self.graph_slots))
            if self.slot_visible[i]
        ]

        for param, config in self.monitored_params.items():
            if config["active"]():
                if param not in self.data_buffers:
                    self.data_buffers[param] = (
                        deque(maxlen=self.MAX_POINTS),
                        deque(maxlen=self.MAX_POINTS)
                    )

                for slot in visible_slots:
                    tab_widget = slot["tab_widget"]
                    tab_data = slot["tabs_data"]

                    if param not in tab_data:
                        container = QWidget()
                        vbox = QVBoxLayout(container)
                        vbox.setContentsMargins(0, 0, 0, 0)
                        vbox.setSpacing(0)

                        plot = pg.PlotWidget(title=param)
                        plot.showGrid(x=True, y=True)

                        set_line = None
                        if config["setpoint"] is not None:
                            set_line = pg.InfiniteLine(
                                angle=0,
                                pen=pg.mkPen('w', style=Qt.PenStyle.DashLine)
                            )
                            plot.addItem(set_line)

                        vbox.addWidget(plot)
                        container.setLayout(vbox)

                        curve = plot.plot([], [], pen=config["pen"])

                        tab_data[param] = {
                            "widget": container,
                            "plot": plot,
                            "line": set_line,
                            "curve": curve,
                        }

                        tab_widget.addTab(container, param)

            else:
                for slot in self.graph_slots:
                    tab_widget = slot["tab_widget"]
                    tab_data = slot["tabs_data"]

                    if param in tab_data:
                        widget = tab_data[param]["widget"]
                        index = tab_widget.indexOf(widget)
                        if index != -1:
                            tab_widget.removeTab(index)
                        del tab_data[param]

                if param in self.data_buffers:
                    del self.data_buffers[param]

    def update_data(self):
        try:
            # Carrega o max_points das preferências
            main_prefs = self.main_window.preferences
            self.MAX_POINTS = int(float(main_prefs.get("Configurations", {}).get("graph_max_points", "1000")))
        except Exception:
            self.MAX_POINTS = 1000
        # Refresh tab widgets based on active parameters
        current_active = frozenset(
            p for p, cfg in self.monitored_params.items() if cfg["active"]()
        )
        if current_active != self._last_active_params:
            self.update_tabs()
            self._last_active_params = current_active

        # Read from communication handler
        sensor_data = {
            "Tempval": self.comm_handler.tempReadVal,
            "Oxyval": self.comm_handler.oxyReadVal,
            "pHval": self.comm_handler.phReadVal,
            "Pressure": self.comm_handler.pressureReadVal,
            "FlowRate": self.comm_handler.flowReadVal,
            "Distance": self.comm_handler.distanceReadVal,
            "Antifoam": self.comm_handler.antifoamReadVal,
            "Absorbancia": self.comm_handler.biomassAbsorbance,
            "PumpVol": self.comm_handler.pumpVolReadVal,
            "PumpFlow": self.comm_handler.pumpFlowReadVal
        }

        # Ensure motor RPM recorded correctly
        if not self.parameter_settings_page.motor_block.checkbox.isChecked():
            self.main_window.current_motor_rpm = 0

        # Log the data row
        connection_status = self.comm_handler.connection_status
        
        # Format the current_motor_rpm to 3 decimal places to fix logging issues.
        row = (
            f"{self.comm_handler.timeReadVal:.2f}\t"
            f"{self.comm_handler.tempReadVal:.2f}\t"
            f"{self.main_window.current_motor_rpm:.3f}\t"
            f"{self.comm_handler.phReadVal:.2f}\t"
            f"{self.comm_handler.antifoamReadVal:.3f}\t"
            f"{self.comm_handler.pressureReadVal}\t"
            f"{self.comm_handler.oxyReadVal:.3f}\t"
            f"{self.comm_handler.flowReadVal:.3f}\t"
            f"{self.comm_handler.distanceReadVal:.2f}\t"
            f"{self.main_window.current_OUR:.5f}\t"
            f"{self.comm_handler.biomassAbsorbance:.4f}\t"
            f"{self.comm_handler.pumpVolReadVal:.3f}\t"
            f"{self.comm_handler.pumpFlowReadVal:.3f}\t"
            f"{connection_status}\n"
        )
        self.main_window.write_log_row(row)

        elapsed = self.comm_handler.timeReadVal

        # Reset graphs if time has looped
        if self.last_elapsed is not None and elapsed < self.last_elapsed:
            for key in self.data_buffers:
                self.data_buffers[key] = (deque(maxlen=self.MAX_POINTS), deque(maxlen=self.MAX_POINTS))
            for slot in self.graph_slots:
                for wd in slot["tabs_data"].values():
                    if "curve" in wd:
                        wd["curve"].setData([], [])
        self.last_elapsed = elapsed

        # Update each active parameter's plot in both graphs
        for param, config in self.monitored_params.items():
            if config["active"]() and param in self.data_buffers:
                # Determine the value to plot
                if param == "Motor RPM":
                    value = self.main_window.current_motor_rpm
                elif param == "OUR":                       
                    value = self.main_window.current_OUR
                else:
                    try:
                        value = float(sensor_data.get(config["data_key"], None))
                    except (TypeError, ValueError):
                        value = None

                if value is not None and value >= 0:
                    x_buf, y_buf = self.data_buffers[param]
                    x_buf.append(elapsed)
                    y_buf.append(value)

                    # Draw on every visible graph slot
                    for i, slot in enumerate(self.graph_slots):
                        if not self.slot_visible[i]:
                            continue

                        tab_data = slot["tabs_data"]
                        if param not in tab_data:
                            continue

                        tab_data[param]["curve"].setData(list(x_buf), list(y_buf))

                        if config["setpoint"] is not None and tab_data[param]["line"] is not None:
                            sp_val = config["setpoint"]()
                            if sp_val is not None:
                                tab_data[param]["line"].setValue(sp_val)