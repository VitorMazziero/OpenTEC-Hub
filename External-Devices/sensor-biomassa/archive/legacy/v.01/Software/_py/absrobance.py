import numpy as np
import matplotlib.pyplot as plt

# Constants
L1 = 1  # mm
L2 = 15.0  # mm
D = 80.0  # mm
alpha = 0.3  # Absorption coefficient (arbitrary value for demonstration)

# Function to calculate transmittance
def transmittance(y):
    L_y = L1 + (L2 - L1) * (y / D)
    T_y = np.exp(-alpha * L_y)
    return T_y

# Generate y values from 2.5 to 15 mm
y_values = np.linspace(1, 15, 1000)
T_values = transmittance(y_values)

# Plot the transmittance
plt.figure(figsize=(10, 6))
plt.plot(y_values, T_values, label='Transmittance', color='blue')
plt.xlabel('y (mm)')
plt.ylabel('Transmittance')
plt.title('Transmittance over different y from 2.5 to 15 mm')
plt.grid(True)
plt.legend()
plt.show()


# Function to calculate absorbance
def absorbance(y):
    L_y = L1 + (L2 - L1) * (y / D)
    A_y = alpha * L_y
    return A_y

# Generate y values from 2.5 to 15 mm
y_values = np.linspace(1, 15, 1000)
A_values = absorbance(y_values)

# Filter y values where absorbance is between 0.2 and 0.8
valid_indices = np.where((A_values >= 0.2) & (A_values <= 0.8))
valid_y_values = y_values[valid_indices]
valid_A_values = A_values[valid_indices]

# Plot the absorbance
plt.figure(figsize=(10, 6))
plt.plot(y_values, A_values, label='Absorbance', color='green')
plt.scatter(valid_y_values, valid_A_values, color='red', label='Valid Absorbance Range')
plt.xlabel('y (mm)')
plt.ylabel('Absorbance')
plt.title('Absorbance of B. subtilis over different y from 2.5 to 15 mm')
plt.axhline(y=0.2, color='blue', linestyle='--', label='Lower Bound (0.2)')
plt.axhline(y=0.8, color='blue', linestyle='--', label='Upper Bound (0.8)')
plt.grid(True)
plt.legend()
plt.show()

import pandas as pd

# Display valid y and absorbance values
valid_data = pd.DataFrame({
    'y (mm)': valid_y_values,
    'Absorbance': valid_A_values
})

print(valid_data)
