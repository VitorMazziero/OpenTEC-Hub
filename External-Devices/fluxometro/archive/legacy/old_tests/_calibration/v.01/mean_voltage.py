import json
import os

def calculate_mean_voltage(file_path):
    voltages = []
    with open(file_path, 'r') as file:
        for line in file:
            data = json.loads(line)
            voltages.append(data['flow_voltage'])
    
    mean_voltage = sum(voltages) / len(voltages) if voltages else 0
    return mean_voltage

def main():
    folder_path = '.'  # Assuming the files are in the current directory
    files = [
        '0.txt', '0.25.txt', '0.5.txt', '1.txt', '1.5.txt', 
        '2.txt', '2.5.txt', '5.txt', '7.5.txt', '8.5.txt', '9.5.txt'
    ]
    
    results = []

    for file_name in files:
        file_path = os.path.join(folder_path, file_name)
        if os.path.exists(file_path):
            mean_voltage = calculate_mean_voltage(file_path)
            results.append((file_name, mean_voltage))
        else:
            print(f"File {file_name} does not exist.")

    print("File Name\tMean Voltage")
    print("---------------------------")
    for file_name, mean_voltage in results:
        print(f"{file_name}\t{mean_voltage:.6f}")

if __name__ == "__main__":
    main()
