import serial
import time

# Configure the serial port
serial_port = 'COM6'  # Replace with your actual serial port
baud_rate = 9600

# Initialize serial connection
ser = serial.Serial(serial_port, baud_rate, timeout=1)
time.sleep(2)  # Wait for the connection to initialize

# Test data to send
test_data = "Hello, Arduino!\n"

# Send test data
print(f"Sending: {test_data.strip()}")
ser.write(test_data.encode())

# Read response
response = ser.readline().decode().strip()
print(f"Received: {response}")

# Close the serial connection
ser.close()
