import numpy as np
import math
import matplotlib.pyplot as plt

# Given values
d = 3.5   
R = 6   

# Define the function for the area of the circular segment as a function of d
def circular_segment_area(d, R):
    # This is the area of the circular segment for a given d and radius R
    return R**2 * np.arccos((R - d) / R) - (R - d) * np.sqrt(2 * R * d - d**2)

# Create an array of d values from 0 to 2R
d_values = np.linspace(0, 2*R, 500)
# Calculate the corresponding area values
area_values = circular_segment_area(d_values, R)

# Plot the function
plt.figure(figsize=(10, 5))
plt.plot(d_values, area_values, label='Intersection Area', color='blue')
plt.axhline(np.pi * R**2, color='red', linestyle='--', label='Full Circle Area')
plt.axhline(np.pi * R**2 / 2, color='green', linestyle='--', label='Half Circle Area')
plt.axhline(0, color='orange', linestyle='--', label='Zero Area')

plt.title('Circular Segment Intersection Area vs. d')
plt.xlabel('Distance d')
plt.ylabel('Intersection Area')
plt.legend()
plt.grid(True)
plt.show()

# Calculate the area of the circular segment
area_segment = circular_segment_area(d, R)

# Calculate the volume by multiplying the area of the segment by the height
volume_intersection = area_segment * d
corrected_volume = volume_intersection * math.pi/4
theorical_volume = 34.30
theoretical_volume_steinmetz = (16/3) * R**3

print(corrected_volume)

print(corrected_volume*100/144)

# Given values and functions are provided by the user. 
# The radius R and distance d are already in millimeters, so no conversion is needed.
R = 6

# The function for the area of the circular segment as a function of d is already defined.
# Define the corrected volume function as described by the user.
def corrected_volume(d, R):
    area_segment = circular_segment_area(d, R)
    volume_intersection = area_segment * d
    return volume_intersection * (math.pi / 4)

# Use the user-provided theoretical volume values for different d.
theoretical_volume_values = np.array([
    3.61, 9.81, 19.87, 34.26, 53.31, 75.25,
    106.26, 140.48, 179.92, 224.6, 274.41
])

# The d values corresponding to the theoretical volumes.
d_values_theoretical = np.array([1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 5.5, 6])

# Calculate the corrected volumes for the given d values
corrected_volumes = corrected_volume(d_values_theoretical, R)

# Plot the corrected volume versus d
plt.figure(figsize=(10, 5))
plt.plot(d_values_theoretical, corrected_volumes, label='Corrected Volume', color='purple')
plt.scatter(d_values_theoretical, theoretical_volume_values, color='black', label='Theoretical Volume (CAD)')

# Labelling the graph
plt.title('Corrected Volume vs. Distance d')
plt.xlabel('Distance d (mm)')
plt.ylabel('Corrected Volume (mm³)')
plt.legend()
plt.grid(True)

# Display the plot
plt.show()
