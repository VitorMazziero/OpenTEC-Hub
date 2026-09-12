import pandas as pd
import matplotlib.pyplot as plt

# Load the data from the provided text file
file_path = 'Filtered.txt'

# Read the data from the text file
data = pd.read_json(file_path, lines=True)

# Convert to DataFrame
df = pd.DataFrame(data)

# Extract the time and flow voltage
time = df['seconds']
flow_voltage = df['flow_voltage']

# Plot the filtered flow voltage
plt.figure(figsize=(10, 6))
plt.plot(time, flow_voltage, label='Filtered Flow Voltage')
plt.xlabel('Time (seconds)')
plt.ylabel('Flow Voltage')
plt.title('Filtered Flow Voltage vs Time')
plt.legend()
plt.grid(True)
plt.show()
