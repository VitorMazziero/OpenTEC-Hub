import math
import random
import time

# ==================================
# FIRMWARE SETTINGS (from C++ code v3)
# ==================================
LOW_THRESHOLD_RAW = 6000
HIGH_THRESHOLD_RAW = 60000
OPTIMAL_TARGET_RAW = 40000 # The "perfect" reading to aim for
SATURATION_RAW = 65530

IT_LEVEL_COUNT = 4
g_itDelays = [100, 200, 400, 800] # ms

PWM_LEVEL_COUNT = 8
g_pwmSettings = [5.0, 10.0, 15.0, 20.0, 25.0, 50.0, 75.0, 100.0] # %

# The 2D Blanking Calibration Table
g_blankValues = [[0 for _ in range(PWM_LEVEL_COUNT)] for _ in range(IT_LEVEL_COUNT)]

# Firmware state variables
g_currentItIndex = 0
g_currentPwmIndex = 0

# ==================================
# "HARDWARE" SIMULATION
# ==================================

def get_true_blank_I0(it_idx, pwm_idx):
    """
    Simulates the "true" I_0 reading for clear media.
    We'll add non-linearity to the PWM brightness (pwm**0.9)
    and scale it to get realistic numbers.
    
    *** THIS IS THE FIX: This function now returns the THEORETICAL
    *** light level, even if it's > 65535. The sensor
    *** itself will clamp this, which is simulated in run_blanking_simulation
    *** and simulate_sensor_read.
    """
    pwm_pct = g_pwmSettings[pwm_idx]
    it_ms = g_itDelays[it_idx]
    
    # Model non-linear LED brightness
    # (100% is not 10x brighter than 10%)
    base_light = (pwm_pct ** 0.9) * 40.0 
    
    # Model integration time (mostly linear)
    # Adjusted scaling factor to give a good range with new PWM levels
    true_I0 = base_light * (it_ms / 100.0) * 12.0
        
    return int(true_I0) # Return the unclamped theoretical value

def run_blanking_simulation():
    """
    Simulates the 'blank' command.
    Fills the g_blankValues table with "noisy" sensor readings
    of the clear media. This is the map the firmware will use.
    """
    print("--- Simulating Blanking Routine (v3) ---")
    for i in range(IT_LEVEL_COUNT):
        isSaturated = False
        print(f"Setting IT: {g_itDelays[i]}ms")
        for j in range(PWM_LEVEL_COUNT):
            if isSaturated:
                reading = 65535
            else:
                true_I0 = get_true_blank_I0(i, j)
                if true_I0 >= SATURATION_RAW:
                    isSaturated = True
                    # The sensor hardware clamps the reading
                    reading = 65535
                else:
                    # Add sensor noise
                    reading = true_I0 + random.randint(-10, 10)
            
            g_blankValues[i][j] = reading
            # Added formatting for alignment
            print(f"  PWM {g_pwmSettings[j]:5.1f}%: RAW = {reading}")
            
    print("--- Blanking Complete ---")


def simulate_sensor_read(true_absorbance, it_idx, pwm_idx):
    """
    Simulates the sensor taking a single reading.
    It uses the "true" blank value and Beer-Lambert law
    to calculate the "true" signal, then adds noise.
    """
    # Get the "true" theoretical blank value for this gear
    true_I0 = get_true_blank_I0(it_idx, pwm_idx)
    
    # Beer-Lambert Law: I = I_0 * 10^(-A)
    true_signal = true_I0 * (10 ** (-true_absorbance))
    
    # Add sensor noise
    noisy_signal = true_signal + random.randint(-10, 10)
    
    # Clamp to physical limits (This is what the sensor hardware does)
    if noisy_signal < 0: noisy_signal = 0
    if noisy_signal > 65535: noisy_signal = 65535
        
    return int(noisy_signal)

# ==================================
# "FIRMWARE" LOGIC (from C++ code v3)
# ==================================

def findAndSetBestNewGear_sim(true_absorbance):
    """
    Simulates the 'SEARCHING' state.
    Finds the best *more sensitive* gear and sets it globally.
    This now mirrors the correct nested-loop logic.
    """
    global g_currentItIndex, g_currentPwmIndex
    
    print("      SEARCHING: Reading low, finding optimal new gear...")
    bestGearIT = -1
    bestGearPWM = -1
    bestScore = -1000000 # A very low starting score

    # Loop through all IT levels, starting from the current one
    for it in range(g_currentItIndex, IT_LEVEL_COUNT):
        isSaturatedThisIT = False
        
        # Determine the starting PWM index for this IT level
        startPwm = 0
        if it == g_currentItIndex:
            # If we're on the same IT level, start at the *next* PWM level
            startPwm = g_currentPwmIndex + 1
        
        if startPwm >= PWM_LEVEL_COUNT:
            # We were at the last PWM, so skip straight to the next IT
            continue

        for pwm in range(startPwm, PWM_LEVEL_COUNT):
            # Simulate taking a test reading
            newReading = simulate_sensor_read(true_absorbance, it, pwm)
            print(f"      Testing IT {g_itDelays[it]}ms, PWM {g_pwmSettings[pwm]:5.1f}%: RAW = {newReading}")

            if newReading >= SATURATION_RAW:
                # This gear is saturated.
                print("      Saturated. Skipping rest of this IT level.")
                isSaturatedThisIT = True
                break # Break from the *inner* (PWM) loop
            
            if newReading > LOW_THRESHOLD_RAW:
                # This is a valid candidate. Let's "score" it.
                score = -abs(OPTIMAL_TARGET_RAW - newReading)
                if score > bestScore:
                    bestScore = score
                    bestGearIT = it
                    bestGearPWM = pwm
        
        if isSaturatedThisIT and bestGearIT == -1:
            # If we saturated this IT level and *still* haven't found
            # a single valid gear, continue to the next IT level.
            continue
            
    if bestGearIT != -1:
        # We found a new, better gear!
        g_currentItIndex = bestGearIT
        g_currentPwmIndex = bestGearPWM
        print(f"      JUMPING to new gear: IT {g_itDelays[g_currentItIndex]}ms, PWM {g_pwmSettings[g_currentPwmIndex]:5.1f}%")
    else:
        # We are at max sensitivity
        print("      At max sensitivity, but reading is still low.")
        g_currentItIndex = IT_LEVEL_COUNT - 1
        g_currentPwmIndex = PWM_LEVEL_COUNT - 1

def run_decrease_sensitivity_logic():
    """
    Simulates the simple "decrease sensitivity" logic.
    """
    global g_currentItIndex, g_currentPwmIndex
    
    print("      SHIFTING DOWN: Reading high...")
    if g_currentPwmIndex > 0:
        g_currentPwmIndex -= 1
    elif g_currentItIndex > 0:
        g_currentItIndex -= 1
        g_currentPwmIndex = PWM_LEVEL_COUNT - 1 # Set to max PWM (now index 7)
    # else: At min sensitivity
    return True

def calculate_absorbance(raw_reading, it_idx, pwm_idx):
    """
    Calculates absorbance using the blanking map.
    """
    current_I0 = g_blankValues[it_idx][pwm_idx]
    current_I = raw_reading
    
    if current_I0 == 0 or current_I0 == 65535:
        return -99.0 # Error
        
    if current_I > current_I0:
        current_I = current_I0 # Cap (Absorbance = 0)
        
    if current_I <= 0:
        return 9.9 # Effectively infinite absorbance
        
    return -math.log10(float(current_I) / float(current_I0))

# ==================================
# MAIN SIMULATION
# ==================================

# --- 1. Run Blanking ---
run_blanking_simulation()

print("\n--- Starting Cultivation Simulation (v3 Firmware) ---")
print("Time(h) | True Abs | Reported Abs | Raw | Gear (IT/PWM)")
print("-" * 65)

# --- 2. Initialize Bioreactor ---
# Start at Abs = 0.05
# Growth rate mu = 0.1 (doubling time ~6.9h)
A_initial = 0.05
mu = 0.1 

# Start firmware in lowest gear
g_currentItIndex = 0
g_currentPwmIndex = 0

# --- 3. Run Simulation ---
for hour in range(0, 49): # Simulate for 48 hours
    
    # "REAL WORLD": Calculate true absorbance
    true_A = A_initial * math.exp(mu * hour)
    
    # "FIRMWARE": Take a reading in the current gear
    raw = simulate_sensor_read(true_A, g_currentItIndex, g_currentPwmIndex)
    
    # "FIRMWARE": Run auto-ranging logic
    if raw < LOW_THRESHOLD_RAW and raw > 0:
        # Reading is too low. Call the smart search.
        findAndSetBestNewGear_sim(true_A)
        # Re-read with the *new* gear chosen by the search function
        raw = simulate_sensor_read(true_A, g_currentItIndex, g_currentPwmIndex)
        
    elif raw > HIGH_THRESHOLD_RAW:
        # Reading is too high. Call the simple "down-shift".
        run_decrease_sensitivity_logic()
        # Re-read with the new (lower) gear
        raw = simulate_sensor_read(true_A, g_currentItIndex, g_currentPwmIndex)
    
    # "FM_FIRMWARE": Calculate final value with the final gear
    reported_A = calculate_absorbance(raw, g_currentItIndex, g_currentPwmIndex)
    
    # Report
    gear_str = f"{g_itDelays[g_currentItIndex]}ms / {g_pwmSettings[g_currentPwmIndex]:5.1f}%"
    print(f"{hour:6.0f} |  {true_A:7.3f} | {reported_A:12.3f} | {raw:5d} | {gear_str}")
    
    time.sleep(0.1) # Slow down for readability

print("-" * 65)
print("Simulation complete.")
