import sys
import asyncio
from PyQt5.QtWidgets import (
    QApplication, QMainWindow, QListWidget, QPushButton,
    QVBoxLayout, QWidget, QLabel, QMessageBox, QLineEdit, QListWidgetItem
)
from qasync import QEventLoop, asyncSlot
from bleak import BleakScanner, BleakClient

# UUID definitions for the Nordic UART Service:
SERVICE_UUID = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
TX_UUID = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  # Used for notifications from the ESP32
RX_UUID = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  # Used for sending commands to the ESP32

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Bluetooth Device Selector & Command Sender")
        self.resize(600, 500)

        self.layout = QVBoxLayout()
        self.label = QLabel("Press 'Scan' to search for nearby BLE devices.")
        self.layout.addWidget(self.label)

        self.scanButton = QPushButton("Scan")
        self.scanButton.clicked.connect(self.on_scan)
        self.layout.addWidget(self.scanButton)

        self.deviceList = QListWidget()
        self.layout.addWidget(self.deviceList)
        self.deviceList.itemDoubleClicked.connect(self.on_device_double_clicked)

        self.cmdLineEdit = QLineEdit()
        self.cmdLineEdit.setPlaceholderText("Enter command to send")
        self.cmdLineEdit.setEnabled(False)
        self.layout.addWidget(self.cmdLineEdit)

        self.sendButton = QPushButton("Send Command")
        self.sendButton.setEnabled(False)
        self.sendButton.clicked.connect(self.on_send_command)
        self.layout.addWidget(self.sendButton)

        container = QWidget()
        container.setLayout(self.layout)
        self.setCentralWidget(container)

        self.devices = []  # List to store discovered devices.
        self.client = None  # Will hold the BleakClient instance once a device is connected.

    def on_scan(self):
        asyncio.create_task(self.scan_devices())

    async def scan_devices(self):
        self.deviceList.clear()
        self.label.setText("Scanning for BLE devices...")
        # Discover devices (timeout of 10 seconds for a thorough scan)
        self.devices = await BleakScanner.discover(timeout=10.0)
        for device in self.devices:
            # Use device.rssi directly.
            rssi_val = device.rssi  
            device_name = device.name if device.name else "Unnamed"
            item_text = f"{device_name} | {device.address} | RSSI: {rssi_val}"
            self.deviceList.addItem(item_text)
        self.label.setText("Scan complete. Double-click a device to connect.")

    @asyncSlot("QListWidgetItem*")
    async def on_device_double_clicked(self, item: QListWidgetItem):
        text = item.text()
        try:
            # Expected format: "<Name> | <Address> | RSSI: <value>"
            address = text.split("|")[1].strip()
        except IndexError:
            QMessageBox.warning(self, "Parsing Error", "Could not extract device address.")
            return

        device = next((d for d in self.devices if d.address == address), None)
        if device is None:
            QMessageBox.warning(self, "Device Not Found", "The selected device was not found in the scan data.")
            return

        self.label.setText(f"Connecting to {device.name if device.name else device.address} ...")
        try:
            self.client = BleakClient(device.address)
            await self.client.connect()
            if self.client.is_connected:
                QMessageBox.information(
                    self, "Connected", f"Connected to {device.name if device.name else device.address}."
                )
                self.label.setText("Connected. You can now send commands.")
                self.cmdLineEdit.setEnabled(True)
                self.sendButton.setEnabled(True)
            else:
                QMessageBox.warning(self, "Connection Failed", "Failed to connect to the device.")
        except Exception as e:
            QMessageBox.critical(self, "Error", f"An error occurred during connection: {e}")

    @asyncSlot()
    async def on_send_command(self):
        command = self.cmdLineEdit.text().strip()
        if not command:
            QMessageBox.warning(self, "Empty Command", "Please enter a command to send.")
            return
        if self.client is None or not self.client.is_connected:
            QMessageBox.warning(self, "Not Connected", "No device is connected.")
            return
        try:
            # Send the command as UTF-8 encoded data.
            await self.client.write_gatt_char(RX_UUID, command.encode('utf-8'), response=True)
            QMessageBox.information(self, "Command Sent", f"Sent command: {command}")
            self.cmdLineEdit.clear()
        except Exception as e:
            QMessageBox.critical(self, "Error", f"Failed to send command: {e}")

    def closeEvent(self, event):
        if self.client and self.client.is_connected:
            asyncio.create_task(self.client.disconnect())
        event.accept()

if __name__ == '__main__':
    app = QApplication(sys.argv)
    loop = QEventLoop(app)
    asyncio.set_event_loop(loop)
    mainWin = MainWindow()
    mainWin.show()
    with loop:
        loop.run_forever()
