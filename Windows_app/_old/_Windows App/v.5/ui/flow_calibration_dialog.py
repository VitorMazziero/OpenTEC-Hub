import sys
import time
import numpy as np
from PySide6.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QTableWidget, QTableWidgetItem,
    QPushButton, QLabel, QLineEdit, QHeaderView, QWidget, QMessageBox,
    QAbstractItemView, QStyle, QGroupBox
)
from PySide6.QtCore import Qt, QTimer
import pyqtgraph as pg

class ReadingPopup(QDialog):
    def __init__(self, comm_handler, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Ajuste Fino")
        self.comm_handler = comm_handler
        self.resize(350, 300)

        layout = QVBoxLayout(self)

        # Current Reading Display (Voltage)
        self.voltage_label = QLabel("Tensão Atual: -- V")
        self.voltage_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.voltage_label.setStyleSheet("font-size: 18px; font-weight: bold; color: #333;")
        layout.addWidget(self.voltage_label)

        # Corrected Value Display (Setpoint)
        self.corrected_label = QLabel("Valor Corrigido: --")
        self.corrected_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.corrected_label.setStyleSheet("font-size: 14px; font-weight: bold; color: #00796B;")
        layout.addWidget(self.corrected_label)

        # Step Control Group
        step_group = QGroupBox("Controle de Setpoint")
        step_layout = QVBoxLayout()
        
        input_row = QHBoxLayout()
        input_row.addWidget(QLabel("Passo (L/min):"))
        self.step_edit = QLineEdit("0.1")
        input_row.addWidget(self.step_edit)
        step_layout.addLayout(input_row)

        # Up/Down Buttons
        btn_layout = QHBoxLayout()
        
        self.down_btn = QPushButton("▼")
        self.down_btn.setFixedSize(60, 50)
        self.down_btn.clicked.connect(self.decrease_flow)
        
        self.up_btn = QPushButton("▲")
        self.up_btn.setFixedSize(60, 50)
        self.up_btn.clicked.connect(self.increase_flow)
        
        btn_layout.addStretch()
        btn_layout.addWidget(self.down_btn)
        btn_layout.addWidget(self.up_btn)
        btn_layout.addStretch()
        
        step_layout.addLayout(btn_layout)
        
        # Confirmation Feedback
        self.feedback_label = QLabel("")
        self.feedback_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.feedback_label.setStyleSheet("color: gray; font-size: 10px;")
        step_layout.addWidget(self.feedback_label)

        step_group.setLayout(step_layout)
        layout.addWidget(step_group)

        # OK Button
        self.ok_btn = QPushButton("OK (Capturar Tensão)")
        self.ok_btn.setMinimumHeight(40)
        self.ok_btn.clicked.connect(self.accept_reading)
        layout.addWidget(self.ok_btn)

        # Timer to update voltage label
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.update_voltage)
        self.timer.start(500)

        self.mean_voltage = 0.0
        self.samples = []
        
        # Initialize tracking variables
        self.initial_setpoint = 0.0
        self.current_setpoint = 0.0
        
        # Initialize the display
        self.init_setpoint_display()

    def get_voltage_safely(self):
        """Tries to find the voltage value in comm_handler."""
        # 1. Check for the exact attribute created in comm_handler
        if hasattr(self.comm_handler, "FlowVoltage"):
             try:
                 val = float(self.comm_handler.FlowVoltage)
                 # Only return if it looks like a real reading (not the default 0 if disconnected)
                 # or if we are sure it's connected.
                 return val
             except:
                 pass

        # 2. Check the raw data dictionary (The most robust method)
        # This reads directly from the JSON {"FlowVoltage": 0.0275}
        if hasattr(self.comm_handler, "data") and isinstance(self.comm_handler.data, dict):
            if "FlowVoltage" in self.comm_handler.data:
                try:
                    return float(self.comm_handler.data["FlowVoltage"])
                except:
                    pass

        return 0.0

    def update_voltage(self):
        val = self.get_voltage_safely()
        self.voltage_label.setText(f"Tensão Atual: {val:.4f} V")

    def init_setpoint_display(self):
        """Initializes the local setpoint tracking from the comm_handler."""
        val = 0.0
        # Try to read current state from handler
        if hasattr(self.comm_handler, "flowSetpoint"):
            try:
                val = float(self.comm_handler.flowSetpoint)
            except:
                pass
        elif hasattr(self.comm_handler, "flow_setpoint"):
            try:
                val = float(self.comm_handler.flow_setpoint)
            except:
                pass
        
        # Store in local variables
        self.initial_setpoint = val
        self.current_setpoint = val
        self.update_corrected_label()

    def update_corrected_label(self):
        """Updates label with Total Value and (Accumulated Diff)."""
        diff = self.current_setpoint - self.initial_setpoint
        # Format: "Valor Corrigido: 1.50 (+0.50)"
        sign = "+" if diff >= 0 else ""
        self.corrected_label.setText(f"Valor Corrigido: {self.current_setpoint:.2f} ({sign}{diff:.2f})")

    def get_step(self):
        try:
            val = float(self.step_edit.text().replace(",", "."))
            return val
        except ValueError:
            return 0.1

    def increase_flow(self):
        self.adjust_flow(self.get_step())

    def decrease_flow(self):
        self.adjust_flow(-self.get_step())

    def adjust_flow(self, delta):
        # Update LOCAL variable directly to be cumulative
        self.current_setpoint = max(0.0, self.current_setpoint + delta)
        
        # Update UI
        self.update_corrected_label()
        
        # Feedback
        self.feedback_label.setText("Comando enviado!")
        self.feedback_label.setStyleSheet("color: green; font-size: 11px; font-weight: bold;")
        QTimer.singleShot(1000, lambda: self.feedback_label.setText(""))

        # Send command using the updated local setpoint
        command = {"flowmeterComm": 1, "flowSetpoint": self.current_setpoint}
        self.comm_handler.send_command(command)

    def accept_reading(self):
        self.ok_btn.setEnabled(False)
        self.samples = []
        
        # HARDWARE SYNC: Data comes every ~2s. We must wait >2s for a new value.
        # 10 samples * 2.2s = ~22 seconds total duration.
        self.sample_interval = 2200  
        self.target_samples = 10     
        
        self.ok_btn.setText(f"Iniciando... (0/{self.target_samples})")
        
        # Setup sampling timer
        self.sample_timer = QTimer(self)
        self.sample_timer.timeout.connect(self.collect_sample)
        self.sample_timer.start(self.sample_interval)

    def collect_sample(self):
        # Get value
        val = self.get_voltage_safely()
        self.samples.append(val)
        
        # Update button text to show progress
        self.ok_btn.setText(f"Lendo... ({len(self.samples)}/{self.target_samples})")

        # Stop after target samples
        if len(self.samples) >= self.target_samples:
            self.sample_timer.stop()
            self.finish_sampling()

    def finish_sampling(self):
        # Ensure timer is stopped (redundant safety)
        if self.sample_timer.isActive():
            self.sample_timer.stop()
            
        if self.samples:
            self.mean_voltage = sum(self.samples) / len(self.samples)
        else:
            self.mean_voltage = 0.0
            
        self.accept()

class FlowCalibrationDialog(QDialog):
    def __init__(self, comm_handler, initial_data=None, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Calibração do Fluxômetro")
        self.resize(1000, 600)
        self.comm_handler = comm_handler

        layout = QHBoxLayout(self)

        # --- Left: Graph ---
        graph_layout = QVBoxLayout()
        self.plot_widget = pg.PlotWidget(title="Curva de Calibração")
        self.plot_widget.setLabel('left', 'Vazão Real (L/min)')
        self.plot_widget.setLabel('bottom', 'Tensão (V)')
        self.plot_widget.showGrid(x=True, y=True)
        self.plot_widget.addLegend()
        
        # MODIFIED: Changed background to black ('k') to match other graphs
        self.plot_widget.setBackground('k') 
        
        graph_layout.addWidget(self.plot_widget)
        layout.addLayout(graph_layout, stretch=2)

        # --- Right: Table & Controls ---
        right_layout = QVBoxLayout()
        
        self.table = QTableWidget()
        self.table.setColumnCount(3)
        self.table.setHorizontalHeaderLabels(["Vazão Real", "Tensão (V)", "Enviar"])
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Stretch)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        right_layout.addWidget(self.table)

        # Add Row Button
        self.add_row_btn = QPushButton("Adicionar Ponto Vazio")
        self.add_row_btn.clicked.connect(self.add_empty_row)
        right_layout.addWidget(self.add_row_btn)

        self.equation_label = QLabel("Nenhuma equação calculada.")
        self.equation_label.setWordWrap(True)
        self.equation_label.setStyleSheet("font-weight: bold; color: #333; margin-top: 10px; font-size: 12px;")
        self.equation_label.setAlignment(Qt.AlignmentFlag.AlignTop)
        right_layout.addWidget(self.equation_label)

        # Buttons Layout
        btns_layout = QHBoxLayout()

        self.cancel_btn = QPushButton("Fechar")
        self.cancel_btn.clicked.connect(self.reject)
        btns_layout.addWidget(self.cancel_btn)

        self.save_btn = QPushButton("Salvar Calibração")
        self.save_btn.clicked.connect(self.calculate_and_save)
        self.save_btn.setStyleSheet("background-color: #4CAF50; color: white; font-weight: bold; padding: 5px;")
        self.save_btn.setEnabled(False) 
        btns_layout.addWidget(self.save_btn)

        right_layout.addLayout(btns_layout)
        layout.addLayout(right_layout, stretch=1)

        # Initialize Graph Items
        # Note: Changed pen to None (dots only) but ensured brush is visible on black background
        self.data_scatter = pg.ScatterPlotItem(pen=pg.mkPen(None), brush=pg.mkBrush(0, 100, 255, 200), size=10, name="Pontos Medidos")
        self.plot_widget.addItem(self.data_scatter)
        
        self.curve1_plot = pg.PlotCurveItem(pen=pg.mkPen('r', width=2), name="Curva 1")
        self.plot_widget.addItem(self.curve1_plot)
        
        self.curve2_plot = pg.PlotCurveItem(pen=pg.mkPen('g', width=2), name="Curva 2 (> 0.0545V)")
        self.plot_widget.addItem(self.curve2_plot)

        # Helper variables for calculated coefficients
        self.k1 = self.f1 = self.c1 = None
        self.k2 = self.f2 = self.c2 = None

        if initial_data:
            for flow, volt in initial_data:
                self.add_row(flow, volt)
            self.update_data_and_graph()
        else:
            self.add_empty_row()

    def add_empty_row(self):
        self.add_row("", "")

    def get_data(self):
        """Returns the list of (flow, voltage) tuples from the table."""
        points = []
        for r in range(self.table.rowCount()):
            try:
                flow_text = self.table.item(r, 0).text().replace(",", ".")
                volt_text = self.table.item(r, 1).text().replace(",", ".")
                if flow_text and volt_text:
                    points.append([float(flow_text), float(volt_text)])
            except ValueError:
                continue
        # Sort by flow for consistency
        points.sort(key=lambda x: x[0])
        return points

    def add_row(self, flow_val, volt_val):
        row = self.table.rowCount()
        self.table.insertRow(row)
        
        # Real Flow (Editable)
        self.table.setItem(row, 0, QTableWidgetItem(str(flow_val)))
        
        # Voltage (Read-only initially)
        volt_item = QTableWidgetItem(str(volt_val))
        volt_item.setFlags(volt_item.flags() ^ Qt.ItemFlag.ItemIsEditable)
        self.table.setItem(row, 1, volt_item)
        
        # Send Button
        btn = QPushButton()
        btn.setIcon(self.style().standardIcon(QStyle.StandardPixmap.SP_ArrowRight))
        btn.setToolTip("Enviar setpoint e ler tensão")
        btn.clicked.connect(lambda checked=False, r=row: self.on_send_clicked(r))
        self.table.setCellWidget(row, 2, btn)

    def on_send_clicked(self, row):
        # 1. Get Real Flow from table
        try:
            text = self.table.item(row, 0).text().replace(",", ".")
            real_flow = float(text)
        except ValueError:
            QMessageBox.warning(self, "Erro", "Insira um valor numérico válido para a Vazão Real.")
            return

        # 2. Send as setpoint (Initial guess)
        command = {"flowmeterComm": 1, "flowSetpoint": real_flow}
        self.comm_handler.send_command(command)

        # 3. Open Popup
        popup = ReadingPopup(self.comm_handler, self)
        
        if popup.exec() == QDialog.DialogCode.Accepted:
            # 4. Populate Voltage
            mean_voltage = popup.mean_voltage
            self.table.item(row, 1).setText(f"{mean_voltage:.6f}")
            
            # 5. Update Graph and Sort
            self.update_data_and_graph()

    def update_data_and_graph(self):
        # 1. Gather data
        points = []
        for r in range(self.table.rowCount()):
            try:
                flow_text = self.table.item(r, 0).text().replace(",", ".")
                volt_text = self.table.item(r, 1).text().replace(",", ".")
                
                if flow_text and volt_text:
                    flow = float(flow_text)
                    volt = float(volt_text)
                    points.append((flow, volt))
            except ValueError:
                continue

        # 2. Sort by Flow (Ascending)
        points.sort(key=lambda x: x[0])
        
        # 3. Rebuild Table (to show sorted order)
        self.table.setRowCount(0)
        for flow, volt in points:
            self.add_row(flow, f"{volt:.6f}")
        
        self.add_empty_row()
        
        # 4. Update Graph
        if not points:
            return

        flows = [p[0] for p in points]
        volts = [p[1] for p in points]
        
        self.data_scatter.setData(volts, flows)

        # 5. Auto-Calculate Curves
        self.calculate_curves(points)

    def calculate_curves(self, points):
        threshold = 0.0545
        
        # Split data based on voltage
        low_data = [p for p in points if p[1] <= threshold]
        high_data = [p for p in points if p[1] > threshold]
        
        # --- Curve 1 (Low Voltage) ---
        if len(low_data) > 2:
            x = np.array([p[1] for p in low_data])
            y = np.array([p[0] for p in low_data])
            try:
                coeffs = np.polyfit(x, y, 2) # kx^2 + fx + c
                self.k1, self.f1, self.c1 = coeffs
                
                # Plot line
                x_line = np.linspace(min(x), threshold, 50)
                y_line = self.k1 * x_line**2 + self.f1 * x_line + self.c1
                self.curve1_plot.setData(x_line, y_line)
            except Exception as e:
                print(f"Erro fitting curve 1: {e}")
        else:
            self.curve1_plot.setData([], [])
            self.k1 = self.f1 = self.c1 = None

        # --- Curve 2 (High Voltage) ---
        if len(high_data) >= 2: 
             x = np.array([p[1] for p in high_data])
             y = np.array([p[0] for p in high_data])
             try:
                 deg = 2 if len(high_data) > 2 else 1
                 coeffs = np.polyfit(x, y, deg)
                 
                 if deg == 2:
                     self.k2, self.f2, self.c2 = coeffs
                 else:
                     self.k2 = 0.0
                     self.f2, self.c2 = coeffs

                 x_line = np.linspace(threshold, max(x)*1.1, 50)
                 y_line = self.k2 * x_line**2 + self.f2 * x_line + self.c2
                 self.curve2_plot.setData(x_line, y_line)
             except Exception as e:
                 print(f"Erro fitting curve 2: {e}")
        else:
             self.curve2_plot.setData([], [])
             self.k2 = self.f2 = self.c2 = None

        eq_text = "<b>Equações Calculadas (y=Vazão, x=Tensão):</b><br>"
        has_eq = False

        if self.k1 is not None:
            # Format: y = ax^2 + bx + c
            sign_f = "+" if self.f1 >= 0 else "-"
            sign_c = "+" if self.c1 >= 0 else "-"
            eq_text += (f"• Curva 1 (≤ 0.0545V):<br>"
                        f"&nbsp;&nbsp;y = {self.k1:.4f}x² {sign_f} {abs(self.f1):.4f}x {sign_c} {abs(self.c1):.4f}<br>")
            has_eq = True

        if self.k2 is not None:
            sign_f = "+" if self.f2 >= 0 else "-"
            sign_c = "+" if self.c2 >= 0 else "-"
            eq_text += (f"• Curva 2 (> 0.0545V):<br>"
                        f"&nbsp;&nbsp;y = {self.k2:.4f}x² {sign_f} {abs(self.f2):.4f}x {sign_c} {abs(self.c2):.4f}")
            has_eq = True
        
        if not has_eq:
            self.equation_label.setText("Nenhuma equação calculada.")
        else:
            self.equation_label.setText(eq_text)

        # Enable Save Button if we have at least one valid curve calculated
        has_k1 = self.k1 is not None
        has_k2 = self.k2 is not None
        self.save_btn.setEnabled(has_k1 or has_k2)

    def calculate_and_save(self):
        command = {}
        msg = "Parâmetros calculados:\n"
        
        if self.k1 is not None:
            command.update({"k1": float(self.k1), "f1": float(self.f1), "c1": float(self.c1)})
            msg += f"Curva 1 (<=0.0545V): k={self.k1:.2f}, f={self.f1:.2f}, c={self.c1:.2f}\n"
        
        if self.k2 is not None:
            command.update({"k2": float(self.k2), "f2": float(self.f2), "c2": float(self.c2)})
            msg += f"Curva 2 (>0.0545V): k={self.k2:.2f}, f={self.f2:.2f}, c={self.c2:.2f}\n"
            
        if command:
            self.comm_handler.send_command(command)
            QMessageBox.information(self, "Sucesso", msg + "\nEnviados para o hardware!")
            self.accept()
        else:
            QMessageBox.warning(self, "Aviso", "Nenhuma curva foi calculada.")