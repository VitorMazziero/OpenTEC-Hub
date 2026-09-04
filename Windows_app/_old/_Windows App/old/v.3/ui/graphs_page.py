#!/usr/bin/env python3
# -*- coding: utf-8 -*-
# graphs_page.py

from PyQt6.QtWidgets import QWidget, QVBoxLayout, QHBoxLayout, QTabWidget
from PyQt6.QtCore import QTimer, Qt
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

        # Horizontal layout containing two independent tab widgets
        graphs_layout = QHBoxLayout()
        self.tab_widget_left = QTabWidget()
        self.tab_widget_right = QTabWidget()
        graphs_layout.addWidget(self.tab_widget_left)
        graphs_layout.addWidget(self.tab_widget_right)
        main_layout.addLayout(graphs_layout)

        # Data buffers shared across both graphs (timestamp vs value)
        self.data_buffers = {}
        self.MAX_POINTS = 1000

        # Two separate maps for each graph's tabs: index 0 = left, 1 = right
        self.tab_widgets = [self.tab_widget_left, self.tab_widget_right]
        self.tab_widgets_data = [{}, {}]

        # Timer to periodically update
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.update_data)
        if hasattr(self, "main_window"):
            self.timer.start(self.main_window.dataDelay)
        else:
            self.timer.start(1000)

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
                "setpoint": lambda: None,
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

    def update_tabs(self):
        """
        Ensure that each active parameter has a tab (and corresponding plot)
        in both left and right tab widgets, and that inactive parameters are removed.
        """
        for param, config in self.monitored_params.items():
            if config["active"]():
                # Add tabs for newly active parameters
                for idx, tab_widget in enumerate(self.tab_widgets):
                    tab_data = self.tab_widgets_data[idx]
                    if param not in tab_data:
                        container = QWidget()
                        vbox = QVBoxLayout(container)
                        plot = pg.PlotWidget(title=param)
                        plot.showGrid(x=True, y=True)
                        # Optional setpoint line
                        set_line = None
                        if config["setpoint"] is not None:
                            set_line = pg.InfiniteLine(
                                angle=0,
                                pen=pg.mkPen('w', style=Qt.PenStyle.DashLine)  # Qt6 enum
                            )
                            plot.addItem(set_line)
                        vbox.addWidget(plot)
                        container.setLayout(vbox)

                        tab_data[param] = {
                            "widget": container,
                            "plot": plot,
                            "line": set_line
                        }
                        # Initialize buffer with a fixed max length
                        self.data_buffers[param] = (deque(maxlen=1000), deque(maxlen=1000))

                        tab_widget.addTab(container, param)
            else:
                # Remove tabs for parameters that were deactivated
                for idx, tab_widget in enumerate(self.tab_widgets):
                    tab_data = self.tab_widgets_data[idx]
                    if param in tab_data:
                        widget = tab_data[param]["widget"]
                        index = tab_widget.indexOf(widget)
                        if index != -1:
                            tab_widget.removeTab(index)
                        del tab_data[param]
                # Clear shared buffer
                if param in self.data_buffers:
                    del self.data_buffers[param]

    def update_data(self):
        # Refresh tab widgets based on active parameters
        self.update_tabs()

        # Read from communication handler
        self.comm_handler.read_and_parse_data()
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
            for tab_data in self.tab_widgets_data:
                for wd in tab_data.values():
                    wd["plot"].clear()
        self.last_elapsed = elapsed

        # Update each active parameter's plot in both graphs
        for param, config in self.monitored_params.items():
            if config["active"]() and param in self.data_buffers:
                # Determine the value to plot
                if param == "Motor RPM":
                    value = self.main_window.current_motor_rpm
                else:
                    try:
                        value = float(sensor_data.get(config["data_key"], None))
                    except (TypeError, ValueError):
                        value = None

                if value is not None and value >= 0:
                    x_buf, y_buf = self.data_buffers[param]
                    x_buf.append(elapsed)
                    y_buf.append(value)

                    # Draw on both left and right graphs
                    for tab_data in self.tab_widgets_data:
                        plot = tab_data[param]["plot"]
                        plot.clear()
                        plot.plot(x_buf, y_buf, pen=config["pen"])

                        # Update setpoint line if applicable
                        if config["setpoint"] is not None:
                            sp_val = config["setpoint"]()
                            tab_data[param]["line"].setValue(sp_val)