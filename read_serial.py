import serial, time
s=serial.Serial('COM5', 115200, timeout=2)
s.dtr=False
s.rts=False
time.sleep(1)
for i in range(5):
  print(s.readline())

