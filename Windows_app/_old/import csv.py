import csv
import tkinter as tk
from tkinter import filedialog
import os

def convert_txt_to_csv():
    # Initialize Tkinter root
    root = tk.Tk()
    root.withdraw()  # Hide the main window

    # Ask user to select the input TXT file
    input_path = filedialog.askopenfilename(
        title="Select TXT file",
        filetypes=[("Text files", "*.txt"), ("All files", "*.*")]
    )

    if not input_path:
        print("No input file selected.")
        return

    # Ask user to select output CSV file path
    default_output_name = os.path.splitext(os.path.basename(input_path))[0] + ".csv"
    output_path = filedialog.asksaveasfilename(
        title="Save CSV file as",
        defaultextension=".csv",
        initialfile=default_output_name,
        filetypes=[("CSV files", "*.csv"), ("All files", "*.*")]
    )

    if not output_path:
        print("No output file selected.")
        return

    # Configure input and output delimiters
    delimiter_in = '\t'   # Adjust this if input file uses a different delimiter
    delimiter_out = ';'

    # Convert the file
    try:
        with open(input_path, 'r', encoding='utf-8') as txt_file:
            reader = csv.reader(txt_file, delimiter=delimiter_in)
            with open(output_path, 'w', newline='', encoding='utf-8') as csv_file:
                writer = csv.writer(csv_file, delimiter=delimiter_out)
                for row in reader:
                    cleaned_row = [field.strip() for field in row]
                    writer.writerow(cleaned_row)
        print(f"Conversion completed. File saved as: {output_path}")
    except Exception as e:
        print(f"Error during conversion: {e}")

if __name__ == "__main__":
    convert_txt_to_csv()
