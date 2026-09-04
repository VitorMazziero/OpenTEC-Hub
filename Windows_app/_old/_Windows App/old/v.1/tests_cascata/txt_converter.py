#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
This script reads a tab‐separated text file containing sensor data,
ignores abrupt changes by substituting the last valid value when a new reading 
exceeds allowed range or threshold difference, and writes the valid data 
to an Excel spreadsheet. It also creates scatter line charts (one per sensor variable)
showing the variation versus time.

Additionally, instead of using the original time values, the script computes 
the mean delta t from the "Time (s)" column and replaces that column with a new, 
equally spaced time series starting at mean_delta_t.
 
Set the input and output file names (and paths) in the variables below.
"""

import os
import pandas as pd
from xlsxwriter.utility import xl_col_to_name

# >>> SET THESE VARIABLES <<<
INPUT_TXT_FILE = "cultivo_13.txt"     # Path to your input text file
OUTPUT_EXCEL_FILE = "cultivo_13.xlsx"   # Desired output Excel file name (or path)
# <<< SET THESE VARIABLES >>>

def process_file(txt_file, validations, columns):
    valid_rows = []
    # Dictionary to store the last valid value for each column.
    last_valid = {col: None for col in columns}
    
    with open(txt_file, "r") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split("\t")
            if len(parts) != len(columns):
                print(f"Skipping line (wrong number of columns): {line}")
                continue
            try:
                values = [float(p) for p in parts]
            except Exception as e:
                print(f"Skipping line (conversion error): {line}\nError: {e}")
                continue
            
            # Create a copy; we may modify values if a reading is too abrupt.
            new_values = values[:]
            skip_line = False
            for i, col in enumerate(columns):
                # Skip validation for "Time (s)"
                if col == "Time (s)":
                    continue
                rule = validations.get(col)
                if rule is None:
                    last_valid[col] = new_values[i]
                    continue

                val = new_values[i]
                # Check if value is within the allowed range.
                if not (rule["min"] <= val <= rule["max"]):
                    if last_valid[col] is not None:
                        #print(f"{col}: value {val} out-of-range; using last valid value {last_valid[col]}")
                        new_values[i] = last_valid[col]
                        val = last_valid[col]
                    else:
                        #print(f"{col}: value {val} out-of-range and no previous valid value; skipping line: {line}")
                        skip_line = True
                        break

                # Check that the change is not too abrupt.
                if last_valid[col] is not None and rule["threshold"] is not None:
                    if abs(val - last_valid[col]) > rule["threshold"]:
                        #print(f"{col}: abrupt change (new: {val}, last: {last_valid[col]}); using last valid value")
                        new_values[i] = last_valid[col]
                        val = last_valid[col]
                # Update the last valid value.
                last_valid[col] = new_values[i]
            if skip_line:
                continue
            valid_rows.append(new_values)
    return valid_rows

def txt_to_excel(txt_file, excel_file):
    # Define the column names.
    columns = [
        "Time (s)",
        "Temperature (°C)",
        "Motor (rpm)",
        "pH",
        "Antifoam",
        "Pressure",
        "Oxygen",
        "Flowmeter",
        "Distance"
    ]
    # Define validation rules for each sensor column.
    validations = {
        "Temperature (°C)": {"min": 0, "max": 70, "threshold": 5},
        "Motor (rpm)":       {"min": 100, "max": 1000, "threshold": 200},
        "pH":                {"min": 0, "max": 14, "threshold": 0.5},
        "Antifoam":          {"min": -1, "max": 999, "threshold": 1000},  # -1 indicates "off"
        "Pressure":          {"min": -1, "max": 500, "threshold": 100},    # -1 indicates inactive
        "Oxygen":            {"min": 0, "max": 5000, "threshold": 500},
        "Flowmeter":         {"min": 0, "max": 100, "threshold": 5},
        "Distance":          {"min": 0, "max": 1000, "threshold": 50},
    }
    
    rows = process_file(txt_file, validations, columns)
    if not rows:
        print("No valid data found.")
        return

    df = pd.DataFrame(rows, columns=columns)
    
    # Instead of using the original "Time (s)" values,
    # compute the mean delta t from the original time column and
    # replace it with an equally spaced time series.
    if len(df) > 1:
        dt_series = df["Time (s)"].diff().dropna()
        mean_dt = dt_series.mean()
        # New time series: first value = mean_dt, second = 2*mean_dt, etc.
        new_time = [mean_dt * i for i in range(1, len(df) + 1)]
        df["Time (s)"] = new_time
    else:
        df["Time (s)"] = [0]
    
    # Use the XlsxWriter engine so we can create charts.
    writer = pd.ExcelWriter(excel_file, engine="xlsxwriter")
    df.to_excel(writer, sheet_name="Data", index=False)
    
    workbook  = writer.book
    data_sheet = writer.sheets["Data"]
    
    # Create a new worksheet for charts.
    charts_sheet = workbook.add_worksheet("Charts")
    
    # Get the number of data rows (including header in row 0, so data starts at row 1).
    nrows = len(df) + 1
    
    # For each sensor variable (skip "Time (s)"), create a scatter line chart.
    chart_height = 15  # approximate number of rows reserved per chart
    for i, col in enumerate(columns):
        if col == "Time (s)":
            continue
        col_index = columns.index(col)
        chart = workbook.add_chart({'type': 'scatter', 'subtype': 'straight_with_markers'})
        
        chart.add_series({
            'name':       [data_sheet.name, 0, col_index],
            'categories': [data_sheet.name, 1, 0, nrows-1, 0],
            'values':     [data_sheet.name, 1, col_index, nrows-1, col_index],
        })
        
        chart.set_title({'name': f"{col} vs Time"})
        chart.set_x_axis({'name': "Time (s)"})
        chart.set_y_axis({'name': col})
        chart.set_style(10)
        
        row_position = (i-1) * chart_height  # i-1 because we skipped "Time (s)"
        charts_sheet.insert_chart(row_position, 0, chart, {'x_scale': 1.5, 'y_scale': 1.5})
    
    writer.close()
    print(f"Excel file saved as: {excel_file}")

if __name__ == '__main__':
    txt_to_excel(INPUT_TXT_FILE, OUTPUT_EXCEL_FILE)
