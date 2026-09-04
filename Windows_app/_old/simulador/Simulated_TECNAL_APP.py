import sys
import serial
import threading
import time

from PyQt5 import QtCore, QtGui, QtWidgets


class MotorControlApp(QtWidgets.QMainWindow):
    """
    A PyQt5 application for motor, temperature, pH, nutrient, antifoam, and pressure control,
    oxygen monitoring, and real-time logging to a text file.
    """

    # A custom signal to append text to the log from worker threads safely
    logSignal = QtCore.pyqtSignal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Motor Control App (PyQt)")

        # -------------------- Application State --------------------
        self.serial_connection = None
        self.running = False

        self.sensor_thread = None

        # Control booleans for sensors/controls
        self.temperature_control_on = False  # Temperature reading ON/OFF
        self.ph_control_on = False
        self.oxygen_monitoring_on = False
        self.nutrient_control_on = False
        self.antifoam_control_on = False
        self.pressure_monitoring_on = False  # NEW: Pressure monitoring flag

        # Threads for additional parameters
        self.ph_parameters_thread = None
        self.nutrient_parameters_thread = None
        self.antifoam_parameters_thread = None
        self.pressure_parameters_thread = None  # NEW: Pressure parameters thread

        # Store last commanded motor RPM so we can log it
        self.last_motor_value = "0"

        # Logging to file
        self.log_file_path = None
        self.start_time = None  # We'll store the time when logging begins

        # 1) Prompt user for a file in which to save logs (optional)
        # self.select_log_file()

        # Build UI
        self._init_ui()

        # Connect the custom log signal to a slot that updates the UI
        self.logSignal.connect(self.on_log_signal_emitted)

        # If a log file was chosen, write the header row
        if self.log_file_path:
            self.write_log_header()

    # ------------------------------------------------------------------
    #  Prompt for Log File at Startup (Optional)
    # ------------------------------------------------------------------
    def select_log_file(self):
        """
        Opens a file dialog for the user to select (or create) a log file.
        Stores the chosen path in self.log_file_path.
        """
        app = QtWidgets.QApplication.instance()
        if not app:  # If not already running
            app = QtWidgets.QApplication(sys.argv)

        options = QtWidgets.QFileDialog.Options()
        options |= QtWidgets.QFileDialog.DontUseNativeDialog
        fileName, _ = QtWidgets.QFileDialog.getSaveFileName(
            self,
            "Select (or Create) a TXT Log File",
            "log.txt",
            "Text Files (*.txt)",
            options=options
        )
        if fileName:
            self.log_file_path = fileName
            self.start_time = time.time()  # Starting time for logging

    def write_log_header(self):
        """
        Writes the column headers to the chosen log file.
        Columns: Time (s), Time (h), Motor (rpm), Temperatura (ºC), pH, Oxygen (%), Antifoam, Pressure
        """
        header = "Time (s)\tTime (h)\tMotor (rpm)\tTemperatura (ºC)\tpH\tOxygen (%)\tAntifoam\tPressure\n"
        try:
            with open(self.log_file_path, "w", encoding="utf-8") as f:
                f.write(header)
        except Exception as e:
            self.log_message(f"Error writing header to log file: {e}")

    def append_log_data(self, motor_rpm, temperature, ph_value, oxygen_value, antifoam_value, pressure_value):
        """
        Appends one line of data to the log file:
          Time (s) | Time (h) | Motor (rpm) | Temperatura (ºC) | pH | Oxygen (%) | Antifoam | Pressure
        If the user did not select a file, this does nothing.
        """
        if not self.log_file_path:
            return  # No file selected

        try:
            # Calculate elapsed time
            elapsed_s = time.time() - self.start_time
            elapsed_h = elapsed_s / 3600.0

            line = (
                f"{elapsed_s:.2f}\t"
                f"{elapsed_h:.4f}\t"
                f"{motor_rpm}\t"
                f"{temperature}\t"
                f"{ph_value}\t"
                f"{oxygen_value}\t"
                f"{antifoam_value}\t"
                f"{pressure_value}\n"
            )
            with open(self.log_file_path, "a", encoding="utf-8") as f:
                f.write(line)
        except Exception as e:
            self.log_message(f"Error appending to log file: {e}")

    # ------------------------------------------------------------------
    #  UI Construction
    # ------------------------------------------------------------------
    def _init_ui(self):
        """
        Build all widgets and layouts.
        """
        # Central Widget
        central_widget = QtWidgets.QWidget()
        self.setCentralWidget(central_widget)

        # Master Layout (vertical)
        master_layout = QtWidgets.QVBoxLayout(central_widget)

        # ---------------- Communication Block ----------------
        communication_group = QtWidgets.QGroupBox("Communication")
        communication_layout = QtWidgets.QHBoxLayout(communication_group)

        com_label = QtWidgets.QLabel("COM Port:")
        self.com_port_combo = QtWidgets.QComboBox()
        self.com_port_combo.addItems(self.get_available_ports())

        self.open_button = QtWidgets.QPushButton("Open Port")
        self.close_button = QtWidgets.QPushButton("Close Port")
        self.close_button.setEnabled(False)

        communication_layout.addWidget(com_label)
        communication_layout.addWidget(self.com_port_combo)
        communication_layout.addWidget(self.open_button)
        communication_layout.addWidget(self.close_button)

        master_layout.addWidget(communication_group)

        # ---------------- Main Content Layout (Horizontal) ----------------
        main_content_layout = QtWidgets.QHBoxLayout()

        # -------- Left Side: Motor, Temperature, pH, Oxygen, Pressure --------
        left_side_layout = QtWidgets.QVBoxLayout()

        # Motor Control Block
        motor_group = QtWidgets.QGroupBox("Motor Control")
        motor_layout = QtWidgets.QHBoxLayout(motor_group)
        self.motor_value_entry = QtWidgets.QLineEdit()
        self.motor_value_entry.setFixedWidth(60)
        self.motor_value_entry.setText("0")
        self.start_motor_button = QtWidgets.QPushButton("Start Motor")
        self.stop_motor_button = QtWidgets.QPushButton("Stop Motor")
        self.start_motor_button.setEnabled(False)
        self.stop_motor_button.setEnabled(False)
        motor_layout.addWidget(self.motor_value_entry)
        motor_layout.addWidget(self.start_motor_button)
        motor_layout.addWidget(self.stop_motor_button)
        left_side_layout.addWidget(motor_group)

        # Temperature Control Block
        temperature_group = QtWidgets.QGroupBox("Temperature Control")
        temperature_layout = QtWidgets.QHBoxLayout(temperature_group)
        self.temperature_value_entry = QtWidgets.QLineEdit()
        self.temperature_value_entry.setFixedWidth(60)
        self.temperature_value_entry.setText("30.0")
        self.set_temperature_button = QtWidgets.QPushButton("Set Temperature")
        self.set_temperature_button.setEnabled(False)
        self.turn_off_temperature_button = QtWidgets.QPushButton("Turn Off Temperature")
        self.turn_off_temperature_button.setEnabled(False)
        temperature_layout.addWidget(self.temperature_value_entry)
        temperature_layout.addWidget(self.set_temperature_button)
        temperature_layout.addWidget(self.turn_off_temperature_button)
        left_side_layout.addWidget(temperature_group)

        # pH Control Block
        ph_group = QtWidgets.QGroupBox("pH Control")
        ph_layout = QtWidgets.QGridLayout(ph_group)
        ph_setpoint_label = QtWidgets.QLabel("Setpoint:")
        self.ph_setpoint_entry = QtWidgets.QLineEdit("5.00")
        self.ph_setpoint_entry.setFixedWidth(60)
        ph_error_label = QtWidgets.QLabel("Erro pH:")
        self.ph_error_entry = QtWidgets.QLineEdit("0.17")
        self.ph_error_entry.setFixedWidth(60)
        ph_operation_label = QtWidgets.QLabel("Operação (s):")
        self.ph_operation_entry = QtWidgets.QLineEdit("2")
        self.ph_operation_entry.setFixedWidth(60)
        ph_mix_label = QtWidgets.QLabel("Mistura (s):")
        self.ph_mix_entry = QtWidgets.QLineEdit("10")
        self.ph_mix_entry.setFixedWidth(60)
        ph_intensity_label = QtWidgets.QLabel("Intensidade:")
        self.ph_intensity_entry = QtWidgets.QLineEdit("20")
        self.ph_intensity_entry.setFixedWidth(60)
        self.set_ph_button = QtWidgets.QPushButton("Set pH")
        self.set_ph_button.setEnabled(False)
        self.turn_off_ph_button = QtWidgets.QPushButton("Turn Off pH")
        self.turn_off_ph_button.setEnabled(False)
        ph_layout.addWidget(ph_setpoint_label, 0, 0)
        ph_layout.addWidget(self.ph_setpoint_entry, 0, 1)
        ph_layout.addWidget(ph_error_label, 1, 0)
        ph_layout.addWidget(self.ph_error_entry, 1, 1)
        ph_layout.addWidget(ph_operation_label, 2, 0)
        ph_layout.addWidget(self.ph_operation_entry, 2, 1)
        ph_layout.addWidget(ph_mix_label, 3, 0)
        ph_layout.addWidget(self.ph_mix_entry, 3, 1)
        ph_layout.addWidget(ph_intensity_label, 4, 0)
        ph_layout.addWidget(self.ph_intensity_entry, 4, 1)
        ph_layout.addWidget(self.set_ph_button, 5, 0, 1, 2)
        ph_layout.addWidget(self.turn_off_ph_button, 6, 0, 1, 2)
        left_side_layout.addWidget(ph_group)

        # Oxygen Monitoring Block
        oxygen_group = QtWidgets.QGroupBox("Oxygen Monitoring")
        oxygen_layout = QtWidgets.QHBoxLayout(oxygen_group)
        self.oxygen_checkbox = QtWidgets.QCheckBox("Activate Oxygen Monitoring")
        self.oxygen_checkbox.setEnabled(False)
        oxygen_layout.addWidget(self.oxygen_checkbox)
        left_side_layout.addWidget(oxygen_group)

        # ----- NEW: Pressure Monitoring Block -----
        pressure_group = QtWidgets.QGroupBox("Pressure Monitoring")
        pressure_layout = QtWidgets.QHBoxLayout(pressure_group)
        pressure_label = QtWidgets.QLabel("Pressure Reference (mmHg):")
        self.pressure_reference_entry = QtWidgets.QLineEdit("100")
        self.pressure_reference_entry.setFixedWidth(60)
        self.pressure_checkbox = QtWidgets.QCheckBox("Activate Pressure Monitoring")
        pressure_layout.addWidget(pressure_label)
        pressure_layout.addWidget(self.pressure_reference_entry)
        pressure_layout.addWidget(self.pressure_checkbox)
        left_side_layout.addWidget(pressure_group)
        # Wire the pressure checkbox signal
        self.pressure_checkbox.toggled.connect(self.toggle_pressure_monitoring)

        # Add Left Side layout to Main Content layout
        main_content_layout.addLayout(left_side_layout)

        # -------- Right Side: Nutrient and Antifoam Controls --------
        right_side_layout = QtWidgets.QVBoxLayout()

        # Nutrient Pump Control Block
        nutrient_group = QtWidgets.QGroupBox("Nutrient Pump Control")
        nutrient_layout = QtWidgets.QGridLayout(nutrient_group)
        nutri_intensity_label = QtWidgets.QLabel("Intensity:")
        self.nutri_intensity_entry = QtWidgets.QLineEdit("0")
        self.nutri_intensity_entry.setFixedWidth(60)
        nutri_operation_label = QtWidgets.QLabel("Operation(s):")
        self.nutri_operation_entry = QtWidgets.QLineEdit("999")
        self.nutri_operation_entry.setFixedWidth(60)
        nutri_mixing_label = QtWidgets.QLabel("Mixing(s):")
        self.nutri_mixing_entry = QtWidgets.QLineEdit("1")
        self.nutri_mixing_entry.setFixedWidth(60)
        nutri_op_cycle_label = QtWidgets.QLabel("Operation Cycle (min):")
        self.nutri_op_cycle_entry = QtWidgets.QLineEdit("500")
        self.nutri_op_cycle_entry.setFixedWidth(60)
        nutri_mix_cycle_label = QtWidgets.QLabel("Mixing Cycle (min):")
        self.nutri_mix_cycle_entry = QtWidgets.QLineEdit("1")
        self.nutri_mix_cycle_entry.setFixedWidth(60)
        self.set_nutrient_button = QtWidgets.QPushButton("Set Nutrient Pumps")
        self.set_nutrient_button.setEnabled(False)
        self.turn_off_nutrient_button = QtWidgets.QPushButton("Turn Off Nutrient Pumps")
        self.turn_off_nutrient_button.setEnabled(False)
        nutrient_layout.addWidget(nutri_intensity_label, 0, 0)
        nutrient_layout.addWidget(self.nutri_intensity_entry, 0, 1)
        nutrient_layout.addWidget(nutri_operation_label, 1, 0)
        nutrient_layout.addWidget(self.nutri_operation_entry, 1, 1)
        nutrient_layout.addWidget(nutri_mixing_label, 2, 0)
        nutrient_layout.addWidget(self.nutri_mixing_entry, 2, 1)
        nutrient_layout.addWidget(nutri_op_cycle_label, 3, 0)
        nutrient_layout.addWidget(self.nutri_op_cycle_entry, 3, 1)
        nutrient_layout.addWidget(nutri_mix_cycle_label, 4, 0)
        nutrient_layout.addWidget(self.nutri_mix_cycle_entry, 4, 1)
        nutrient_layout.addWidget(self.set_nutrient_button, 5, 0, 1, 2)
        nutrient_layout.addWidget(self.turn_off_nutrient_button, 6, 0, 1, 2)
        right_side_layout.addWidget(nutrient_group)

        # Antifoam Pump Control Block
        antifoam_group = QtWidgets.QGroupBox("Antifoam Pump Control")
        antifoam_layout = QtWidgets.QGridLayout(antifoam_group)
        antifoam_intensity_label = QtWidgets.QLabel("Intensity:")
        self.antifoam_intensity_entry = QtWidgets.QLineEdit("0")
        self.antifoam_intensity_entry.setFixedWidth(60)
        antifoam_operation_label = QtWidgets.QLabel("Operation(s):")
        self.antifoam_operation_entry = QtWidgets.QLineEdit("10")
        self.antifoam_operation_entry.setFixedWidth(60)
        antifoam_mixing_label = QtWidgets.QLabel("Mixing(s):")
        self.antifoam_mixing_entry = QtWidgets.QLineEdit("5")
        self.antifoam_mixing_entry.setFixedWidth(60)
        self.set_antifoam_button = QtWidgets.QPushButton("Set Antifoam Pump")
        self.set_antifoam_button.setEnabled(False)
        self.turn_off_antifoam_button = QtWidgets.QPushButton("Turn Off Antifoam Pump")
        self.turn_off_antifoam_button.setEnabled(False)
        antifoam_layout.addWidget(antifoam_intensity_label, 0, 0)
        antifoam_layout.addWidget(self.antifoam_intensity_entry, 0, 1)
        antifoam_layout.addWidget(antifoam_operation_label, 1, 0)
        antifoam_layout.addWidget(self.antifoam_operation_entry, 1, 1)
        antifoam_layout.addWidget(antifoam_mixing_label, 2, 0)
        antifoam_layout.addWidget(self.antifoam_mixing_entry, 2, 1)
        antifoam_layout.addWidget(self.set_antifoam_button, 3, 0, 1, 2)
        antifoam_layout.addWidget(self.turn_off_antifoam_button, 4, 0, 1, 2)
        right_side_layout.addWidget(antifoam_group)

        # Add Right Side layout to Main Content layout
        main_content_layout.addLayout(right_side_layout)

        # Add Main Content layout to Master Layout
        master_layout.addLayout(main_content_layout)

        # ---------------- Logging Area ----------------
        self.log_text = QtWidgets.QPlainTextEdit()
        self.log_text.setReadOnly(True)
        self.log_text.setFixedHeight(100)
        master_layout.addWidget(self.log_text)

        # ---- Wire up signals/slots ----
        self.open_button.clicked.connect(self.open_serial)
        self.close_button.clicked.connect(self.close_serial)
        self.start_motor_button.clicked.connect(self.start_motor)
        self.stop_motor_button.clicked.connect(self.stop_motor)
        self.set_temperature_button.clicked.connect(self.set_temperature)
        self.turn_off_temperature_button.clicked.connect(self.turn_off_temperature)
        self.set_ph_button.clicked.connect(self.set_ph_control)
        self.turn_off_ph_button.clicked.connect(self.turn_off_ph)
        self.oxygen_checkbox.toggled.connect(self.toggle_oxygen_monitoring)
        self.set_nutrient_button.clicked.connect(self.set_nutrient_control)
        self.turn_off_nutrient_button.clicked.connect(self.turn_off_nutrient_control)
        self.set_antifoam_button.clicked.connect(self.set_antifoam_control)
        self.turn_off_antifoam_button.clicked.connect(self.turn_off_antifoam_control)

    # ------------------------------------------------------------------
    #  Logging Helpers
    # ------------------------------------------------------------------
    def on_log_signal_emitted(self, message: str):
        """
        Slot that handles the custom log signal from threads.
        Safely updates the UI from a thread.
        """
        self.log_message_ui(message)

    def log_message(self, message: str):
        """
        Thread-safe method to log a message.
        """
        self.logSignal.emit(message)

    def log_message_ui(self, message: str):
        """
        Directly appends to the log UI.
        """
        self.log_text.appendPlainText(message)

    def get_available_ports(self):
        """
        Returns a list of dummy COM ports for demonstration.
        Adjust for your actual system if needed.
        """
        return [f"COM{i}" for i in range(1, 21)]

    # ------------------------------------------------------------------
    #  Centralized Command Send/Receive (Handshake)
    # ------------------------------------------------------------------
    def send_command(self, cmd: str, read_response: bool = True) -> str:
        """
        Sends a command to the serial device and optionally reads a response.
        Returns the response string (if any).
        """
        if not self.serial_connection:
            self.log_message("No serial connection. Command not sent.")
            return ""

        try:
            if not cmd.endswith("\n"):
                cmd += "\n"
            self.serial_connection.write(cmd.encode('utf-8'))
            self.log_message(f"Command sent: {cmd.strip()}")
            if read_response:
                response = self.serial_connection.readline().decode('utf-8').strip()
                if response:
                    self.log_message(f"Response: {response}")
                return response
            else:
                return ""
        except Exception as e:
            self.log_message(f"Error sending command '{cmd.strip()}': {e}")
            return ""

    # ------------------------------------------------------------------
    #  Open/Close Serial
    # ------------------------------------------------------------------
    def open_serial(self):
        port = self.com_port_combo.currentText()
        if not port:
            self.log_message("Please select a COM port.")
            return

        try:
            self.serial_connection = serial.Serial(port, 9600, timeout=1)
            self.running = True
            self.log_message(f"Opened serial port on {port}.")
            self.open_button.setEnabled(False)
            self.close_button.setEnabled(True)

            self.start_motor_button.setEnabled(True)
            self.stop_motor_button.setEnabled(True)
            self.set_temperature_button.setEnabled(True)
            self.turn_off_temperature_button.setEnabled(True)
            self.set_ph_button.setEnabled(True)
            self.turn_off_ph_button.setEnabled(True)
            self.oxygen_checkbox.setEnabled(True)
            self.set_nutrient_button.setEnabled(True)
            self.turn_off_nutrient_button.setEnabled(True)
            self.set_antifoam_button.setEnabled(True)
            self.turn_off_antifoam_button.setEnabled(True)
            self.pressure_checkbox.setEnabled(True)

            self.initialize_communication()

            # Start the unified sensor thread
            self.sensor_thread = threading.Thread(
                target=self.read_sensors_automatically, daemon=True
            )
            self.sensor_thread.start()

        except Exception as e:
            self.log_message(f"Error opening serial port: {e}")

    def close_serial(self):
        self.running = False
        self.ph_control_on = False
        self.oxygen_monitoring_on = False
        self.temperature_control_on = False
        self.nutrient_control_on = False
        self.antifoam_control_on = False
        self.pressure_monitoring_on = False

        if self.serial_connection:
            try:
                self.serial_connection.close()
            except:
                pass
            self.serial_connection = None

        self.log_message("Serial connection closed.")
        self.open_button.setEnabled(True)
        self.close_button.setEnabled(False)

        self.start_motor_button.setEnabled(False)
        self.stop_motor_button.setEnabled(False)
        self.set_temperature_button.setEnabled(False)
        self.turn_off_temperature_button.setEnabled(False)
        self.set_ph_button.setEnabled(False)
        self.turn_off_ph_button.setEnabled(False)
        self.oxygen_checkbox.setEnabled(False)
        self.set_nutrient_button.setEnabled(False)
        self.turn_off_nutrient_button.setEnabled(False)
        self.set_antifoam_button.setEnabled(False)
        self.turn_off_antifoam_button.setEnabled(False)
        self.pressure_checkbox.setEnabled(False)
        self.oxygen_checkbox.setChecked(False)

    def initialize_communication(self):
        if not self.serial_connection:
            return

        try:
            resp1 = self.send_command("i", read_response=True)
            if resp1 != "i":
                self.log_message("Initialization failed at step 'i'.")
                return

            resp2 = self.send_command("j", read_response=True)
            if resp2 != "j":
                self.log_message("Initialization failed at step 'j'.")
                return

            self.log_message("Communication initialized successfully.")

        except Exception as e:
            self.log_message(f"Error during initialization: {e}")

    # ------------------------------------------------------------------
    #  Unified Sensor Read Thread
    # ------------------------------------------------------------------
    def read_sensors_automatically(self):
        """
        Continuously sends commands to read temperature (if on), pH (if on),
        oxygen (if on), antifoam sensor (if antifoam control is on),
        and logs the current pressure reference if pressure monitoring is active.
        Each sensor response is expected to be a numeric value.
        """
        while self.running:
            temperature = -1
            ph_value = -1
            oxygen_value = -1
            antifoam_value = -1
            pressure_value = -1

            # Temperature: read only if enabled
            if self.temperature_control_on:
                resp_temp = self.send_command("b", read_response=True)
                if resp_temp:
                    try:
                        temperature = float(resp_temp)
                    except:
                        pass

            # pH: read if enabled
            if self.ph_control_on:
                resp_ph = self.send_command("d", read_response=True)
                if resp_ph:
                    try:
                        ph_value = float(resp_ph)
                    except:
                        pass

            # Oxygen: read if enabled
            if self.oxygen_monitoring_on:
                resp_o2 = self.send_command("g", read_response=True)
                if resp_o2:
                    try:
                        oxygen_value = float(resp_o2)
                    except:
                        pass

            # Antifoam sensor: read if antifoam control is on
            if self.antifoam_control_on:
                resp_antifoam = self.send_command("e", read_response=True)
                if resp_antifoam:
                    try:
                        antifoam_value = float(resp_antifoam)
                    except:
                        pass

            # Pressure: if pressure monitoring is ON, log the reference value
            if self.pressure_monitoring_on:
                try:
                    pressure_value = float(self.pressure_reference_entry.text())
                except:
                    pressure_value = -1

            self.append_log_data(
                motor_rpm=self.last_motor_value,
                temperature=temperature,
                ph_value=ph_value,
                oxygen_value=oxygen_value,
                antifoam_value=antifoam_value,
                pressure_value=pressure_value
            )
            time.sleep(1)

    # ------------------------------------------------------------------
    #  Motor Control
    # ------------------------------------------------------------------
    def start_motor(self):
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            motor_value = self.motor_value_entry.text().strip()
            if not motor_value.isdigit():
                self.log_message("Invalid motor value. Enter a numeric RPM.")
                return

            self.last_motor_value = motor_value
            self.send_command("1V", read_response=False)
            self.send_command(f"{motor_value}A", read_response=False)
            self.log_message(f"Motor started with value: {motor_value} RPM.")
        except Exception as e:
            self.log_message(f"Error starting motor: {e}")

    def stop_motor(self):
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            self.last_motor_value = "0"
            self.send_command("0V", read_response=False)
            self.send_command("0A", read_response=False)
            self.log_message("Motor stopped.")
        except Exception as e:
            self.log_message(f"Error stopping motor: {e}")

    # ------------------------------------------------------------------
    #  Temperature Control
    # ------------------------------------------------------------------
    def set_temperature(self):
        """
        Sets temperature control. If the entered value is 0, sends off command "100B"
        and disables temperature readings. Otherwise, sends the formatted command
        and enables temperature readings.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return

        try:
            temp_value = self.temperature_value_entry.text().strip()
            if not temp_value.replace('.', '', 1).isdigit():
                self.log_message("Invalid temperature value. Enter a valid number.")
                return

            temperature = float(temp_value)
            if temperature == 0.0:
                command = "100B"
                self.temperature_control_on = False
            else:
                formatted_temp = f"{temperature:.1f}".replace('.', '')
                command = f"{formatted_temp}B"
                self.temperature_control_on = True

            self.send_command(command, read_response=False)
            self.log_message(f"Temperature set command sent: {command}")
        except Exception as e:
            self.log_message(f"Error setting temperature: {e}")

    def turn_off_temperature(self):
        """
        Turns off temperature control.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            self.send_command("100B", read_response=False)
            self.temperature_control_on = False
            self.log_message("Temperature control turned off.")
        except Exception as e:
            self.log_message(f"Error turning off temperature control: {e}")

    # ------------------------------------------------------------------
    #  pH Control and Periodic Parameter Updates
    # ------------------------------------------------------------------
    def set_ph_control(self):
        """
        Sets the pH setpoint. If setpoint is nonzero, pH control is enabled and the
        additional parameters will be sent every 10 seconds.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            ph_setpoint = self.ph_setpoint_entry.text().strip()
            if not ph_setpoint.replace('.', '', 1).isdigit():
                self.log_message("Invalid pH setpoint. Enter a number.")
                return

            ph_no_decimal = ph_setpoint.replace('.', '')
            if ph_no_decimal == "0":
                self.ph_control_on = False
                self.send_command("0F", read_response=False)
                self.log_message("pH control turned off.")
            else:
                self.ph_control_on = True
                cmd_setpoint = ph_no_decimal + "D"
                self.send_command(cmd_setpoint, read_response=False)
                self.log_message(f"pH setpoint set to: {ph_setpoint}.")

                # Start periodic sending of pH additional parameters if not already running.
                if self.ph_parameters_thread is None or not self.ph_parameters_thread.is_alive():
                    self.ph_parameters_thread = threading.Thread(target=self.send_ph_additional_parameters, daemon=True)
                    self.ph_parameters_thread.start()
        except Exception as e:
            self.log_message(f"Error setting pH control: {e}")

    def send_ph_additional_parameters(self):
        """
        Sends pH additional parameters (error, operation, mixing, intensity)
        every 10 seconds as long as pH control is active.
        """
        while self.ph_control_on:
            try:
                cmd_error = self.ph_error_entry.text().replace('.', '') + "E"
                self.send_command(cmd_error, read_response=False)
                self.log_message(f"Erro pH set to: {self.ph_error_entry.text()}.")
                time.sleep(0.2)

                cmd_operation = self.ph_operation_entry.text() + "G"
                self.send_command(cmd_operation, read_response=False)
                self.log_message(f"Operação set to: {self.ph_operation_entry.text()}.")
                time.sleep(0.2)

                cmd_mixing = self.ph_mix_entry.text() + "H"
                self.send_command(cmd_mixing, read_response=False)
                self.log_message(f"Mistura set to: {self.ph_mix_entry.text()}.")
                time.sleep(0.2)

                cmd_intensity = self.ph_intensity_entry.text().replace('.', '') + "F"
                self.send_command(cmd_intensity, read_response=False)
                self.log_message(f"Intensidade set to: {self.ph_intensity_entry.text()}.")
            except Exception as e:
                self.log_message(f"Error sending pH additional parameters: {e}")
            time.sleep(10)

    def turn_off_ph(self):
        """
        Turns off pH control.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            self.ph_control_on = False
            self.send_command("0F", read_response=False)
            self.log_message("pH control turned off.")
        except Exception as e:
            self.log_message(f"Error turning off pH control: {e}")

    # ------------------------------------------------------------------
    #  Oxygen Monitoring
    # ------------------------------------------------------------------
    def toggle_oxygen_monitoring(self, checked: bool):
        """
        Toggles oxygen monitoring.
        """
        if not self.serial_connection:
            return

        if checked:
            self.oxygen_monitoring_on = True
            self.log_message("Oxygen monitoring activated.")
        else:
            self.oxygen_monitoring_on = False
            self.log_message("Oxygen monitoring deactivated.")

    # ------------------------------------------------------------------
    #  Nutrient Pump Control and Periodic Parameter Updates
    # ------------------------------------------------------------------
    def set_nutrient_control(self):
        """
        Enables nutrient pump control. If intensity is nonzero, the nutrient parameters
        will be sent every 10 seconds.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            intensity = int(self.nutri_intensity_entry.text().strip())
            if intensity == 0:
                self.nutrient_control_on = False
                self.send_command("0M", read_response=False)
                self.log_message("Nutrient pump control turned off due to intensity 0.")
            else:
                self.nutrient_control_on = True
                if self.nutrient_parameters_thread is None or not self.nutrient_parameters_thread.is_alive():
                    self.nutrient_parameters_thread = threading.Thread(target=self.send_nutrient_parameters, daemon=True)
                    self.nutrient_parameters_thread.start()
                self.log_message("Nutrient pump control turned on.")
        except Exception as e:
            self.log_message(f"Error setting nutrient pump control: {e}")

    def send_nutrient_parameters(self):
        """
        Sends nutrient pump additional parameters every 10 seconds.
        """
        while self.nutrient_control_on:
            try:
                intensity_val = int(self.nutri_intensity_entry.text().strip())
                if intensity_val == 0:
                    cmd_intensity = "0M"
                else:
                    cmd_intensity = f"{intensity_val}0M"
                self.send_command(cmd_intensity, read_response=False)
                self.log_message(f"Nutrient Intensity command sent: {cmd_intensity}")

                op_val = self.nutri_operation_entry.text().strip() or "999"
                cmd_operation = op_val + "N"
                self.send_command(cmd_operation, read_response=False)
                self.log_message(f"Nutrient Operation command sent: {cmd_operation}")

                mixing_val = self.nutri_mixing_entry.text().strip() or "1"
                cmd_mixing = mixing_val + "O"
                self.send_command(cmd_mixing, read_response=False)
                self.log_message(f"Nutrient Mixing command sent: {cmd_mixing}")

                op_cycle_val = self.nutri_op_cycle_entry.text().strip() or "500"
                cmd_op_cycle = op_cycle_val + "P"
                self.send_command(cmd_op_cycle, read_response=False)
                self.log_message(f"Nutrient Operation Cycle command sent: {cmd_op_cycle}")

                mix_cycle_val = self.nutri_mix_cycle_entry.text().strip() or "1"
                cmd_mix_cycle = mix_cycle_val + "Q"
                self.send_command(cmd_mix_cycle, read_response=False)
                self.log_message(f"Nutrient Mixing Cycle command sent: {cmd_mix_cycle}")

            except Exception as e:
                self.log_message(f"Error sending nutrient parameters: {e}")
            time.sleep(10)

    def turn_off_nutrient_control(self):
        """
        Turns off nutrient pump control.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            self.nutrient_control_on = False
            self.send_command("0M", read_response=False)
            self.log_message("Nutrient pump control turned off.")
        except Exception as e:
            self.log_message(f"Error turning off nutrient pump control: {e}")

    # ------------------------------------------------------------------
    #  Antifoam Pump Control and Periodic Parameter Updates
    # ------------------------------------------------------------------
    def set_antifoam_control(self):
        """
        Enables antifoam pump control. If intensity is nonzero, the antifoam parameters
        will be sent every 10 seconds.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            intensity = int(self.antifoam_intensity_entry.text().strip())
            if intensity == 0:
                self.antifoam_control_on = False
                self.send_command("0I", read_response=False)
                self.log_message("Antifoam pump control turned off due to intensity 0.")
            else:
                self.antifoam_control_on = True
                if self.antifoam_parameters_thread is None or not self.antifoam_parameters_thread.is_alive():
                    self.antifoam_parameters_thread = threading.Thread(target=self.send_antifoam_parameters, daemon=True)
                    self.antifoam_parameters_thread.start()
                self.log_message("Antifoam pump control turned on.")
        except Exception as e:
            self.log_message(f"Error setting antifoam pump control: {e}")

    def send_antifoam_parameters(self):
        """
        Sends antifoam pump additional parameters every 10 seconds.
        """
        while self.antifoam_control_on:
            try:
                intensity_val = int(self.antifoam_intensity_entry.text().strip())
                if intensity_val == 0:
                    cmd_intensity = "0I"
                else:
                    cmd_intensity = f"{intensity_val}0I"
                self.send_command(cmd_intensity, read_response=False)
                self.log_message(f"Antifoam Intensity command sent: {cmd_intensity}")

                op_val = self.antifoam_operation_entry.text().strip() or "10"
                cmd_operation = op_val + "J"
                self.send_command(cmd_operation, read_response=False)
                self.log_message(f"Antifoam Operation command sent: {cmd_operation}")

                mixing_val = self.antifoam_mixing_entry.text().strip() or "5"
                cmd_mixing = mixing_val + "L"
                self.send_command(cmd_mixing, read_response=False)
                self.log_message(f"Antifoam Mixing command sent: {cmd_mixing}")

            except Exception as e:
                self.log_message(f"Error sending antifoam parameters: {e}")
            time.sleep(10)

    def turn_off_antifoam_control(self):
        """
        Turns off antifoam pump control.
        """
        if not self.serial_connection:
            self.log_message("Serial connection is not open.")
            return
        try:
            self.antifoam_control_on = False
            self.send_command("0I", read_response=False)
            self.log_message("Antifoam pump control turned off.")
        except Exception as e:
            self.log_message(f"Error turning off antifoam pump control: {e}")

    # ------------------------------------------------------------------
    #  Pressure Monitoring and Periodic Reference Updates
    # ------------------------------------------------------------------
    def toggle_pressure_monitoring(self, checked: bool):
        """
        Toggles pressure monitoring.
        """
        if not self.serial_connection:
            return

        if checked:
            self.pressure_monitoring_on = True
            self.log_message("Pressure monitoring activated.")
            if self.pressure_parameters_thread is None or not self.pressure_parameters_thread.is_alive():
                self.pressure_parameters_thread = threading.Thread(target=self.send_pressure_parameters, daemon=True)
                self.pressure_parameters_thread.start()
        else:
            self.pressure_monitoring_on = False
            self.log_message("Pressure monitoring deactivated.")

    def send_pressure_parameters(self):
        """
        Sends the pressure reference command every 10 seconds.
        Command protocol: <value> + "C" (e.g., "100C").
        """
        while self.pressure_monitoring_on:
            try:
                ref_value = self.pressure_reference_entry.text().strip()
                cmd = ref_value + "C"
                self.send_command(cmd, read_response=False)
                self.log_message(f"Pressure reference command sent: {cmd}")
            except Exception as e:
                self.log_message(f"Error sending pressure parameters: {e}")
            time.sleep(10)


# ------------------ Main Entry Point ------------------
if __name__ == "__main__":
    app = QtWidgets.QApplication(sys.argv)
    window = MotorControlApp()
    window.resize(400, 300)
    window.show()
    sys.exit(app.exec_())
