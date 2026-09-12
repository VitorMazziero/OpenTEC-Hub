import json
import pandas as pd
import tkinter as tk
from tkinter import filedialog
import os

def main():
    # Set up Tkinter and hide the main window
    root = tk.Tk()
    root.withdraw()
    
    # Open the file selector dialog for .txt files
    file_path = filedialog.askopenfilename(
        title="Select a TXT file",
        filetypes=(("Text Files", "*.txt"), ("All Files", "*.*"))
    )
    
    if not file_path:
        print("No file selected. Exiting.")
        return

    data = []
    # Open and process the file line by line
    with open(file_path, 'r') as file:
        for line in file:
            line = line.strip()
            if not line:
                continue
            try:
                # Try to load the line as JSON
                record = json.loads(line)
                if isinstance(record, dict):
                    data.append(record)
            except json.JSONDecodeError:
                # Skip lines that are not valid JSON objects
                continue

    if not data:
        print("No valid JSON lines found in the file.")
        return

    # Create a DataFrame from the list of dictionaries
    df = pd.DataFrame(data)
    
    # Generate the Excel file name by replacing the .txt extension with .xlsx
    base_name = os.path.splitext(file_path)[0]
    excel_file = base_name + '.xlsx'
    
    # Write the DataFrame to an Excel file without the index column
    df.to_excel(excel_file, index=False)
    
    print(f"Excel file saved as: {excel_file}")

if __name__ == '__main__':
    main()
