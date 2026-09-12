"""Quantify LED thermal drift per sampling interval and emit a chart."""
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd

csv = Path(sys.argv[1])
out = Path(sys.argv[2])
df = pd.read_csv(csv)

print(f"{len(df)} samples across {df.phase.nunique()} phases\n")
print(f"{'phase':<16}{'int(ms)':>8}{'duty%':>7}{'n':>6}{'raw0':>8}"
      f"{'raw_end':>9}{'drift%':>8}{'A_err':>8}{'tau_s':>7}")
print("-" * 78)

rows = []
for phase in df.phase.unique():
    d = df[df.phase == phase].reset_index(drop=True)
    if len(d) < 5:
        continue
    interval = int(d.interval_ms.iloc[0])
    duty = float(d.duty_pct.iloc[0])

    # Settled start value: median of the first few samples, so a single
    # outlier at the transition does not define the baseline.
    raw0 = float(d.raw.iloc[:5].median())
    rawe = float(d.raw.iloc[-10:].median())
    drift = 100.0 * (rawe - raw0) / raw0
    # Spurious absorbance this drift produces against a blank taken at raw0.
    a_err = -np.log10(rawe / raw0)

    # Thermal time constant: fit raw(t) = rawe + (raw0-rawe)*exp(-t/tau)
    tau = np.nan
    span = abs(rawe - raw0)
    if span > 30:
        y = (d.raw.values - rawe) / (raw0 - rawe)
        t = d.phase_elapsed_s.values
        ok = y > 0.02
        if ok.sum() > 8:
            slope = np.polyfit(t[ok], np.log(y[ok]), 1)[0]
            if slope < 0:
                tau = -1.0 / slope

    print(f"{phase:<16}{interval:>8}{duty:>7.1f}{len(d):>6}{raw0:>8.0f}"
          f"{rawe:>9.0f}{drift:>+8.2f}{a_err:>+8.4f}"
          f"{tau if not np.isnan(tau) else 0:>7.0f}")
    rows.append(dict(phase=phase, interval=interval, duty=duty, n=len(d),
                     raw0=raw0, rawe=rawe, drift=drift, a_err=a_err, tau=tau))

res = pd.DataFrame(rows)
stress = res[res.phase.str.startswith("stress")].copy()
base = res[res.phase.str.startswith("baseline")]
if len(base):
    stress = pd.concat([base, stress]).sort_values("duty")

print("\n=== stress phases sorted by LED duty ===")
for _, r in stress.iterrows():
    print(f"  duty {r.duty:5.1f}%  interval {r.interval:5.0f} ms  "
          f"drift {r.drift:+6.2f}%  spurious A {r.a_err:+.4f}")

# ---- chart -------------------------------------------------------------
fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(11, 8.5),
                               gridspec_kw={"height_ratios": [2, 1]})
colors = {5000: "#2d6cdf", 2000: "#1f9d55", 1000: "#b8770a", 500: "#c0392b"}
lo, hi = df.raw.min(), df.raw.max()
span = hi - lo
# Headroom above the trace for the phase labels, so they never collide with
# the title or the data.
ax1.set_ylim(lo - 0.06 * span, hi + 0.22 * span)
label_y = hi + 0.04 * span

t_off = 0.0
for phase in df.phase.unique():
    d = df[df.phase == phase]
    if not len(d):
        continue
    t = t_off + d.phase_elapsed_s.values / 60.0
    iv = int(d.interval_ms.iloc[0])
    width = d.phase_elapsed_s.max() / 60.0
    ax1.plot(t, d.raw.values, lw=1.2, color=colors.get(iv, "#666"))
    ax1.axvspan(t_off, t_off + width, color=colors.get(iv, "#666"), alpha=0.07)
    ax1.text(t_off + width / 2, label_y,
             f"{iv/1000:g} s\n{d.duty_pct.iloc[0]}% duty",
             ha="center", va="bottom", fontsize=8,
             color=colors.get(iv, "#666"), fontweight="bold")
    t_off += width

ax1.set_ylabel("raw sensor counts")
ax1.set_xlabel("elapsed (min)")
ax1.set_title("LED thermal drift vs sampling interval — gear locked at "
              "IT 200 ms / PWM 50 %, empty beam",
              fontsize=12, fontweight="bold", pad=26)
ax1.grid(alpha=0.3)

ax2.plot(stress.duty, stress.drift, "o-", color="#c0392b", lw=2, ms=8)
for _, r in stress.iterrows():
    ax2.annotate(f"{r.interval:.0f} ms", (r.duty, r.drift),
                 textcoords="offset points", xytext=(6, 6), fontsize=8)
ax2.axhline(0, color="#888", lw=0.8)
ax2.set_xlabel("LED duty cycle (% of wall time the LED is energised)")
ax2.set_ylabel("raw drift (%)")
ax2.set_title("Drift grows with duty cycle", fontsize=10)
ax2.grid(alpha=0.3)

plt.tight_layout()
plt.savefig(out, dpi=110)
print(f"\nchart -> {out}")
