import tkinter as tk
from tkinter import filedialog
import pandas as pd
import os

def main():
    # Create and hide the root Tkinter window
    root = tk.Tk()
    root.withdraw()
    
    # Open a file explorer dialog to select the TXT file
    file_path = filedialog.askopenfilename(
        title="Select TXT File", 
        filetypes=[("Text Files", "*.txt")]
    )
    
    if not file_path:
        print("No file selected.")
        return
    
    data = []
    # Use 'latin-1' encoding to avoid UnicodeDecodeError (or use errors='ignore')
    with open(file_path, 'r', encoding='latin-1') as file:
        for line in file:
            line = line.strip()
            if line:  # Skip empty lines
                parts = line.split(',')
                if len(parts) == 2:
                    data.append((parts[0], parts[1]))
                else:
                    #print(f"Skipping line (unexpected format): {line}")
                    pass
    
    # Create a DataFrame with the columns "Time(ms)" and "voltage(V)"
    df = pd.DataFrame(data, columns=["Time(ms)", "voltage(V)"])
    
    # Define the output file path (same directory and base name as the TXT file)
    base_name = os.path.splitext(file_path)[0]
    output_file = base_name + ".xlsx"
    
    # Write the DataFrame to an Excel file
    df.to_excel(output_file, index=False)
    
    print(f"Excel file has been saved as: {output_file}")

if __name__ == "__main__":
    main()
