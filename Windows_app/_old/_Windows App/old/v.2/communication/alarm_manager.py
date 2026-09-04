# alarm_manager.py
import threading
import time

try:
    from playsound import playsound
except ImportError:
    def playsound(sound_file):
        print(f"Playing sound: {sound_file}")
        time.sleep(1)

class AlarmThread(threading.Thread):
    """
    Thread that repeatedly plays an alarm sound until stopped.
    """
    def __init__(self, sound_file, stop_event, mute_flag):
        super().__init__(daemon=True)
        self.sound_file = sound_file
        self.stop_event = stop_event
        self.mute_flag = mute_flag

    def run(self):
        while not self.stop_event.is_set():
            if not self.mute_flag.is_set():
                try:
                    playsound(self.sound_file)
                except Exception as e:
                    print(f"Erro ao reproduzir som: {e}")
            time.sleep(0.5)
