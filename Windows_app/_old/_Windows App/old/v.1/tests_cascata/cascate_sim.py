import numpy as np
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation

# -----------------------------
# Simulation Parameters
# -----------------------------
dt = 0.1                  # time step (seconds)
total_time = 60          # total simulation time (seconds)
n_steps = int(total_time/dt)

# Process setpoint and initial condition for DO (dissolved oxygen in %)
DO_setpoint = 65.0        # setpoint DO (%)
DO = 60.0                 # initial DO (%)
prev_DO = DO

# Controller (cascade) parameters
# Outer loop gain (converts DO error to a desired rate of change)
K_outer = 0.1

# Inner loop PID parameters (acting on the rate error)
Kp, Ki, Kd = 5.5, 1, 0.1
pid_integral = 0.0
last_inner_error = 0.0

# kLa_setpoint is an intermediate control variable (range 0 to 100)
kLa_min, kLa_max = 0, 100
kLa_setpoint = 50.0  # initial value

# Mapping from kLa_setpoint to controlled variables (rpm and flow Q)
rpm_min, rpm_max = 400, 800     # motor rpm limits
Q_min, Q_max     = 5, 15        # flow rate limits

# Process model: oxygen supply is a function of rpm and Q (normalized average)
k_supply = 2        # maximum oxygen supply rate (in %/sec when control is full)
consumption_rate = 1  # constant oxygen consumption by microorganisms (%/sec)

# -----------------------------
# Data Storage for Animation
# -----------------------------
time_data = []
DO_data = []
rpm_data = []
Q_data = []

current_time = 0.0

# -----------------------------
# Set Up Figure with Two Subplots
# -----------------------------
fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(10, 8), constrained_layout=True)

# Top plot: DO vs Time
line_DO, = ax1.plot([], [], 'b-', label='DO')
ax1.axhline(DO_setpoint, color='r', linestyle='--', label='Setpoint')
ax1.set_xlabel('Time (s)')
ax1.set_ylabel('DO (%)')
ax1.set_title('DO vs Time')
ax1.legend()
ax1.grid(True)

# Bottom plot: rpm and Q vs Time
# Primary y-axis for rpm
line_rpm, = ax2.plot([], [], 'g-', label='RPM')
ax2.set_xlabel('Time (s)')
ax2.set_ylabel('RPM', color='g')
ax2.tick_params(axis='y', labelcolor='g')
ax2.set_title('Controlled Variables vs Time')
ax2.grid(True)

# Secondary y-axis for Q
ax2_sec = ax2.twinx()
line_Q, = ax2_sec.plot([], [], 'm-', label='Flow Q')
ax2_sec.set_ylabel('Flow Q', color='m')
ax2_sec.tick_params(axis='y', labelcolor='m')

# For legends on the bottom plot, combine the handles
lines = [line_rpm, line_Q]
labels = [l.get_label() for l in lines]
ax2.legend(lines, labels, loc='upper left')

# -----------------------------
# Update Function for Animation
# -----------------------------
def update(frame):
    global DO, prev_DO, kLa_setpoint, pid_integral, last_inner_error, current_time

    # --- Outer Loop: Calculate desired rate of change of DO ---
    error = DO_setpoint - DO                # DO error (setpoint - measured)
    desired_dDO_dt = K_outer * error          # desired DO rate (outer loop)

    # --- Measure actual rate of DO change ---
    dDO_dt = (DO - prev_DO) / dt              # derivative of DO

    # --- Inner Loop (PID): Adjust control based on rate error ---
    inner_error = desired_dDO_dt - dDO_dt
    pid_integral += inner_error * dt
    inner_derivative = (inner_error - last_inner_error) / dt
    last_inner_error = inner_error
    pid_output = Kp * inner_error + Ki * pid_integral + Kd * inner_derivative

    # Update the internal control variable (kLa_setpoint)
    kLa_setpoint += pid_output
    # Saturate kLa_setpoint between its limits
    kLa_setpoint = np.clip(kLa_setpoint, kLa_min, kLa_max)

    # --- Map kLa_setpoint to controlled variables ---
    rpm = rpm_min + (kLa_setpoint - kLa_min) / (kLa_max - kLa_min) * (rpm_max - rpm_min)
    Q   = Q_min   + (kLa_setpoint - kLa_min) / (kLa_max - kLa_min) * (Q_max - Q_min)

    # --- Process Model: Update DO ---
    # Oxygen supply rate (normalized average of rpm and Q contribution)
    oxygen_supply_rate = k_supply * (((rpm / rpm_max) + (Q / Q_max)) / 2.0)
    # New DO is updated based on supply minus consumption
    prev_DO = DO
    DO = DO + dt * (oxygen_supply_rate - consumption_rate)

    # Update simulation time and store data
    current_time += dt
    time_data.append(current_time)
    DO_data.append(DO)
    rpm_data.append(rpm)
    Q_data.append(Q)

    # --- Update DO plot ---
    line_DO.set_data(time_data, DO_data)
    ax1.set_xlim(0, current_time + 1)
    ax1.set_ylim(min(DO_data) - 5, max(DO_data) + 5)

    # --- Update rpm and Q plot ---
    line_rpm.set_data(time_data, rpm_data)
    line_Q.set_data(time_data, Q_data)
    ax2.set_xlim(0, current_time + 1)
    ax2.set_ylim(0, rpm_max * 1.1)
    ax2_sec.set_ylim(0, Q_max * 1.1)

    return line_DO, line_rpm, line_Q

# -----------------------------
# Create Animation
# -----------------------------
from matplotlib.animation import FuncAnimation, FFMpegWriter

# Define animation
ani = FuncAnimation(fig, update, frames=n_steps, interval=100, blit=False, repeat=False)
ani.save('animation.gif', writer='imagemagick', fps=30)

plt.show()

