import re
import json
import sys
import tkinter as tk
from tkinter import filedialog

def calculate_average_voltage(filename):
    pattern = re.compile(r'\{.*?\}')
    voltages = []

    with open(filename, 'r') as file:
        for line in file:
            match = pattern.search(line)
            if match:
                try:
                    data = json.loads(match.group())
                    if 'flow_voltage' in data:
                        voltages.append(data['flow_voltage'])
                except json.JSONDecodeError:
                    continue

    if not voltages:
        print("No flow_voltage values found in the file.")
        return

    average_voltage = sum(voltages) / len(voltages)
    print(f"Average flow_voltage: {average_voltage:.6f} V")

if __name__ == "__main__":
    # Open file dialog to select the log file
    root = tk.Tk()
    root.withdraw()
    file_path = filedialog.askopenfilename(
        title="Select log file",
        filetypes=[("Text files", "*.txt"), ("All files", "*.*")]
    )
    if not file_path:
        print("No file selected.")
        sys.exit(0)

    calculate_average_voltage(file_path)
