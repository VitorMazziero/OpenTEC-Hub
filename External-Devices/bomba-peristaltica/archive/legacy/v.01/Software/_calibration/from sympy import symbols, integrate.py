from sympy import symbols, integrate, exp

# Define symbols
t_min, t_sec, b0, b1, Pump_slope, Pump_intercept = symbols('t_min t_sec b0 b1 Pump_slope Pump_intercept')

# Define stepperSpeed_mL as a function of t_min
# stepperSpeed_mL = b0 
stepperSpeed_mL = b0 + b1 * (t_min)
# stepperSpeed_mL = b0 * exp(b1 * (t_sec)) 

# Define stepperSpeed_raw based on stepperSpeed_mL
stepperSpeed_raw = (stepperSpeed_mL) / Pump_slope - Pump_intercept

# Calculate the indefinite integral of stepperSpeed_raw with respect to t_min
integral_stepperSpeed_raw = integrate(stepperSpeed_raw, t_min)

print(integral_stepperSpeed_raw)