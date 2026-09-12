import pandas as pd
import matplotlib.pyplot as plt
import numpy as np
from scipy.signal import butter, filtfilt

# Load the data from the provided text file
file_path = 'Not Filtered.txt'

# Read the data from the text file
data = pd.read_json(file_path, lines=True)

# Convert to DataFrame
df = pd.DataFrame(data)

# Calculate the first and second derivatives
first_derivative = np.abs(np.diff(data['flow_voltage']))
second_derivative = np.abs(np.diff(first_derivative))

# Apply a low pass filter to the derivatives
def low_pass_filter(data, cutoff_freq, sample_rate, order=5):
    nyquist = 0.5 * sample_rate
    normal_cutoff = cutoff_freq / nyquist
    b, a = butter(order, normal_cutoff, btype='low', analog=False)
    return filtfilt(b, a, data)

sample_rate = 1 / np.mean(np.diff(data['seconds']))  # Calculate sample rate based on time intervals
cutoff_freq = 0.25  # Adjust the cutoff frequency as needed

first_derivative_smoothed = low_pass_filter(first_derivative, cutoff_freq, sample_rate)
second_derivative_smoothed = low_pass_filter(second_derivative, cutoff_freq, sample_rate)

# Time axis for derivatives
time_first_derivative = data['seconds'][1:]
time_second_derivative = data['seconds'][2:]

# Plot everything on the same figure in different graphs
plt.figure(figsize=(12, 10))

# Plot first derivative
plt.subplot(3, 1, 1)
plt.plot(time_first_derivative, first_derivative, label='First Derivative', alpha=0.5)
plt.plot(time_first_derivative, first_derivative_smoothed, label='Smoothed First Derivative (Low Pass Filter)', linestyle='--')
plt.xlabel('Time (seconds)')
plt.ylabel('First Derivative')
plt.title('First Derivative of Flow Voltage vs Time')
plt.legend()
plt.grid(True)

# Plot second derivative
plt.subplot(3, 1, 2)
plt.plot(time_second_derivative, second_derivative, label='Second Derivative', alpha=0.5)
plt.plot(time_second_derivative, second_derivative_smoothed, label='Smoothed Second Derivative (Low Pass Filter)', linestyle='--')
plt.xlabel('Time (seconds)')
plt.ylabel('Second Derivative')
plt.title('Second Derivative of Flow Voltage vs Time')
plt.legend()
plt.grid(True)

# Plot the filtered flow voltage with second derivative filter applied
# Define the second derivative filter function with correct handling of indices
def second_derivative_filter(data, first_derivative_smoothed, second_derivative_smoothed, 
                             first_derivative_threshold_low, first_derivative_threshold_high, 
                             second_derivative_threshold_low, second_derivative_threshold_high):
    filtered_data = []
    previous_value = data[0]
    previous_filtered_value = data[0]
    
    for i in range(1, len(data)):
        new_value = data[i]
        first_derivative = first_derivative_smoothed[i-1] if i-1 < len(first_derivative_smoothed) else first_derivative_smoothed[-1]
        second_derivative = second_derivative_smoothed[i-2] if i-2 < len(second_derivative_smoothed) else second_derivative_smoothed[-1]
        
        if ((first_derivative < first_derivative_threshold_low or first_derivative > first_derivative_threshold_high) 
            and (second_derivative < second_derivative_threshold_low or second_derivative > second_derivative_threshold_high)):
        
            previous_filtered_value = new_value
        
        filtered_data.append(previous_filtered_value)
    
    return filtered_data

# Simulate the flow data with the second derivative filter applied
first_derivative_threshold_low = 0.00001  # Adjust threshold as needed
first_derivative_threshold_high = 0.001
second_derivative_threshold_low = 0.00001  # Adjust threshold as needed
second_derivative_threshold_high = 0.0003
filtered_flow_voltage_second_derivative = second_derivative_filter(data['flow_voltage'], first_derivative_smoothed, second_derivative_smoothed, 
                                                                   first_derivative_threshold_low, first_derivative_threshold_high, 
                                                                   second_derivative_threshold_low, second_derivative_threshold_high)
# Ensure the filtered data has the same length as the original data
filtered_flow_voltage_second_derivative = [data['flow_voltage'][0]] + filtered_flow_voltage_second_derivative

# Define the moving average function
def moving_average(data, window_size):
    return np.convolve(data, np.ones(window_size) / window_size, mode='same')

# Apply a moving average to the filtered data
window_size = 10  # Adjust window size as needed
filtered_flow_voltage_moving_average = moving_average(filtered_flow_voltage_second_derivative, window_size)

# Remove the first and last values from the plot data
trimmed_seconds = data['seconds'][5:-5]
trimmed_flow_voltage = filtered_flow_voltage_moving_average[5:-5]

# Plot the filtered data
plt.subplot(3, 1, 3)
plt.plot(trimmed_seconds, data['flow_voltage'][5:-5], label='Original Flow Voltage', alpha=0.5)
plt.plot(trimmed_seconds, trimmed_flow_voltage, label='Filtered Flow Voltage (Second Derivative Filter)', linewidth=2)
plt.xlabel('Time (seconds)')
plt.ylabel('Flow Voltage')
plt.title('Flow Voltage vs Time with Second Derivative Filter Applied')
plt.legend()
plt.grid(True)

plt.tight_layout()
plt.show()
