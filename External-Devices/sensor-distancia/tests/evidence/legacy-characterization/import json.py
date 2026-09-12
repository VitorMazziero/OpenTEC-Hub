import json
import os
import pandas as pd
import tkinter as tk
from tkinter import filedialog

def convert_txt_to_excel(txt_file_path):
    data = []
    with open(txt_file_path, 'r') as f:
        for line in f:
            line = line.strip()
            # Look for lines containing a JSON object
            if "{" in line and "}" in line:
                # Assume the line format is: <date time><tab><json>
                parts = line.split('\t')
                if len(parts) >= 2:
                    json_str = parts[1]
                    try:
                        reading = json.loads(json_str)
                        # Only add if both "time" and "distance" keys exist
                        if "time" in reading and "distance" in reading:
                            data.append({
                                "time": reading["time"],
                                "distance": reading["distance"]
                            })
                    except json.JSONDecodeError:
                        # Skip if the JSON is invalid
                        continue

    if not data:
        print("No valid sensor readings found in the file.")
        return

    # Create a DataFrame and save to an Excel file
    df = pd.DataFrame(data)
    base, _ = os.path.splitext(txt_file_path)
    excel_file_path = base + '.xlsx'
    df.to_excel(excel_file_path, index=False)
    print(f"Excel file created at: {excel_file_path}")

def main():
    # Set up Tkinter and hide the root window
    root = tk.Tk()
    root.withdraw()
    
    # Open a file explorer dialog to select a text file
    txt_file_path = filedialog.askopenfilename(
        title="Select the sensor data text file",
        filetypes=[("Text Files", "*.txt")]
    )
    
    if not txt_file_path:
        print("No file selected. Exiting.")
        return
    
    convert_txt_to_excel(txt_file_path)

if __name__ == "__main__":
    main()
