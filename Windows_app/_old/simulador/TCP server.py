import socket

# Set up a TCP server
HOST = "0.0.0.0"  # Listen on all network interfaces
PORT = 23         # Use the same port as the app

# Create a socket
server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
server_socket.bind((HOST, PORT))
server_socket.listen(5)  # Allow up to 5 clients

print(f"TCP Server is running on {HOST}:{PORT}")

while True:
    client_socket, client_address = server_socket.accept()
    print(f"Connection from {client_address}")

    client_socket.sendall(b"Hello from the TCP Server!\n")  # Send test data

    while True:
        try:
            data = client_socket.recv(1024)  # Receive data (max 1024 bytes)
            if not data:
                break  # If no data, close connection

            try:
                # Attempt to decode UTF-8 text
                decoded_data = data.decode("utf-8").strip()
            except UnicodeDecodeError:
                # If decoding fails, show raw bytes
                decoded_data = f"Received binary data: {data.hex()}"

            print(f"Received: {decoded_data}")

            # Respond to client
            client_socket.sendall(b"Data received!\n")

        except ConnectionResetError:
            print(f"Connection lost from {client_address}")
            break

    client_socket.close()
    print(f"Connection closed: {client_address}")
