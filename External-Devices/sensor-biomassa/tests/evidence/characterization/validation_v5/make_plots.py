"""
Builds the v5.0 validation plots from the CSVs in data/.

Run after collect_validation_data.py and run_temperature_sweep.py. Any plot
whose input is missing is skipped with a note rather than failing the run, so
this is usable before the temperature sweep has been done.

Design follows the project's data-viz method: categorical hues taken in fixed
slot order from the reference palette (never cycled), one y-axis per plot,
thin marks, solid hairline grid, labels in text ink rather than series color,
and a single-hue sequential ramp for magnitude.
"""
import csv
from collections import defaultdict
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.colors import LinearSegmentedColormap
from matplotlib.patches import Patch
import numpy as np

HERE = Path(__file__).resolve().parent
DATA = HERE / "data"
PLOTS = HERE / "plots"

# --- reference palette, light mode ---------------------------------------
SURFACE     = "#fcfcfb"
INK         = "#0b0b0b"
INK_2       = "#52514e"
MUTED       = "#898781"
GRID        = "#e1e0d9"
BASELINE    = "#c3c2b7"
SERIES_1    = "#2a78d6"   # slot 1, blue
SERIES_2    = "#eb6834"   # slot 2, orange
CRITICAL    = "#d03b3b"   # status: reserved, used only for threshold/failure
# Sequential blue ramp, steps 100..700 (magnitude only, never categorical).
SEQ_BLUE = ["#cde2fb", "#b7d3f6", "#9ec5f4", "#86b6ef", "#6da7ec", "#5598e7",
            "#3987e5", "#2a78d6", "#256abf", "#1c5cab", "#184f95", "#104281",
            "#0d366b"]
CMAP_BLUE = LinearSegmentedColormap.from_list("seq_blue", SEQ_BLUE)

plt.rcParams.update({
    "font.family": ["Segoe UI", "DejaVu Sans", "sans-serif"],
    "font.size": 9,
    "figure.facecolor": SURFACE,
    "axes.facecolor": SURFACE,
    "savefig.facecolor": SURFACE,
    "text.color": INK,
    "axes.labelcolor": INK_2,
    "axes.edgecolor": BASELINE,
    "xtick.color": MUTED,
    "ytick.color": MUTED,
    "axes.titlesize": 11,
    "axes.titleweight": "semibold",
    "axes.grid": True,
    "grid.color": GRID,
    "grid.linewidth": 0.8,
    "grid.linestyle": "-",          # solid hairline; dashed grids read as thresholds
    "axes.axisbelow": True,
    "figure.dpi": 160,
})


def style(ax):
    ax.spines["top"].set_visible(False)
    ax.spines["right"].set_visible(False)
    ax.spines["left"].set_color(BASELINE)
    ax.spines["bottom"].set_color(BASELINE)
    ax.tick_params(length=0)
    return ax


def subtitle(fig, text, y=0.005):
    fig.text(0.5, y, text, ha="center", va="bottom",
             fontsize=7.5, color=MUTED, wrap=True)


def read_csv(path):
    if not path.exists():
        return None
    with path.open(encoding="utf-8") as fh:
        lines = [l for l in fh if not l.lstrip().startswith("#")]
    return list(csv.DictReader(lines))


made, skipped = [], []


# =========================================================================
# 1. The headline: dropouts before and after
# =========================================================================
# Job: show that one class of defect is gone. Two firmwares x four gears, one
# measure -- how far the worst reading fell below the median of its own set.
# Only min and median are used, so the v4.5 side needs nothing that was not
# actually recorded.
def plot_dropouts():
    before = read_csv(DATA / "v45_pulsed_reads_before.csv")
    after = read_csv(DATA / "v50_pulsed_reads.csv")
    if not before or not after:
        skipped.append("dropouts_before_after.png (needs v50_pulsed_reads.csv)")
        return

    b = {}
    for r in before:
        it = int(r["it_nominal_ms"])
        med, mn = float(r["median_raw"]), float(r["min_raw"])
        b[it] = 100.0 * (med - mn) / med

    vals = defaultdict(list)
    for r in after:
        vals[int(r["it_nominal_ms"])].append(float(r["raw"]))
    a = {}
    for it, v in vals.items():
        med = float(np.median(v))
        a[it] = 100.0 * (med - min(v)) / med

    its = sorted(set(b) | set(a))
    x = np.arange(len(its))
    w = 0.36
    gap = 0.02   # surface gap between adjacent bars, not a border

    fig, ax = plt.subplots(figsize=(7.2, 4.0))
    style(ax)
    bars1 = ax.bar(x - w / 2 - gap, [b.get(i, np.nan) for i in its], w,
                   color=SERIES_2, label="v4.5  (waited 2x the nominal IT)")
    bars2 = ax.bar(x + w / 2 + gap, [a.get(i, np.nan) for i in its], w,
                   color=SERIES_1, label="v5.0  (anchors on a real boundary)")

    ax.axhline(2.0, color=CRITICAL, lw=1.2, zorder=1)
    ax.annotate("2% regression-test limit", xy=(len(its) - 0.55, 2.0),
                xytext=(0, 5), textcoords="offset points",
                ha="right", fontsize=7.5, color=CRITICAL)

    for bars in (bars1, bars2):
        for rect in bars:
            h = rect.get_height()
            if np.isnan(h):
                # An absent bar would read as "zero dropouts", which is a claim
                # about data that was never taken. Say so instead.
                ax.annotate("not measured\non v4.5",
                            xy=(rect.get_x() + rect.get_width() / 2, 0),
                            xytext=(0, 8), textcoords="offset points",
                            ha="center", va="bottom", fontsize=7,
                            color=MUTED, style="italic")
                continue
            ax.annotate(f"{h:.2f}%", xy=(rect.get_x() + rect.get_width() / 2, h),
                        xytext=(0, 3), textcoords="offset points",
                        ha="center", fontsize=7.5, color=INK_2)

    ax.set_xticks(x)
    ax.set_xticklabels([f"{i} ms" for i in its])
    # Autoscale ignores NaN bars, so it clips the x-axis to the first bar that
    # has a value -- which silently cropped the "not measured" note off the
    # left edge. Set the limits from the bar geometry instead.
    ax.set_xlim(-(w + gap + 0.24), len(its) - 1 + w + gap + 0.24)
    ax.set_xlabel("nominal integration time")
    ax.set_ylabel("worst reading below the median  (%)")
    ax.set_title("Partly dark conversions are gone")
    ax.legend(frameon=False, loc="upper left", fontsize=8, labelcolor=INK_2)
    ax.set_ylim(0, max(max(b.values()), 2.6) * 1.25)
    fig.tight_layout(rect=[0, 0.05, 1, 1])
    subtitle(fig, "30 single-shot pulsed reads per gear, empty beam, gear locked at PWM 10.5%. "
                  "A missed integration window reads LOW, so the worst downward\ndeviation is the "
                  "defect's signature; symmetric scatter is the instrument noise floor (~0.3%).")
    out = PLOTS / "dropouts_before_after.png"
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)
    made.append(out.name)


# =========================================================================
# 2. The cause: the period is not its label
# =========================================================================
def plot_period_ratio():
    rows = read_csv(DATA / "v46_period_measured.csv")
    if not rows:
        skipped.append("period_vs_label.png")
        return
    by_it = defaultdict(list)
    for r in rows:
        by_it[int(r["it_nominal_ms"])].append(float(r["ratio"]))
    its = sorted(by_it)
    means = [float(np.mean(by_it[i])) for i in its]
    lo = [m - min(by_it[i]) for m, i in zip(means, its)]
    hi = [max(by_it[i]) - m for m, i in zip(means, its)]

    fig, ax = plt.subplots(figsize=(7.2, 4.0))
    style(ax)

    ax.axhline(1.0, color=BASELINE, lw=1.2)
    ax.annotate("what the label claims", xy=(its[0], 1.0), xytext=(0, -12),
                textcoords="offset points", fontsize=7.5, color=MUTED)

    for g, lab, ls in ((1.20, "v5.0 guard  1.20", "-"),
                       (1.15, "v4.6 guard  1.15", "-")):
        ax.axhline(g, color=MUTED if g == 1.15 else CRITICAL, lw=1.0,
                   alpha=0.9 if g == 1.20 else 0.55)
        ax.annotate(lab, xy=(its[-1], g), xytext=(-6, 4),
                    textcoords="offset points", ha="right", fontsize=7.5,
                    color=CRITICAL if g == 1.20 else MUTED)

    ax.errorbar(its, means, yerr=[lo, hi], fmt="o", ms=7, lw=2,
                color=SERIES_1, ecolor=SERIES_1, capsize=4, zorder=5)
    ax.annotate(f"measured  {np.mean(means):.3f}x",
                xy=(its[-1], means[-1]), xytext=(-6, -16),
                textcoords="offset points", ha="right", fontsize=8.5,
                color=INK, fontweight="semibold")

    ax.set_xscale("log")
    ax.set_xticks(its)
    ax.set_xticklabels([str(i) for i in its])
    ax.minorticks_off()
    ax.set_xlabel("nominal integration time  (ms)")
    ax.set_ylabel("real conversion period / its label")
    ax.set_title("The integration time is a label, not a period")
    ax.set_ylim(0.97, 1.26)
    fig.tight_layout(rect=[0, 0.05, 1, 1])
    subtitle(fig, "probe_period times two successive conversion boundaries. Bars span three runs. "
                  "The offset is the same in every slot, so it is a fixed scale\nerror in the "
                  "oscillator, not scatter. The VEML7700 datasheet publishes no tolerance for it.")
    out = PLOTS / "period_vs_label.png"
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)
    made.append(out.name)


# =========================================================================
# 3. Does the period survive a hard LED heating cycle?
# =========================================================================
# The x-axis here is the PHASE, not the temperature, and that is deliberate.
#
# The intended chart was ratio against SoC temperature. The data killed it:
# six minutes at 100% LED duty moved the SoC sensor by 2 C, and it then kept
# RISING through the cooling phase (55.8 -> 56.8). So the proxy does not track
# the optics at all -- it tracks the SoC's own dissipation and the room. Put
# on a temperature x-axis, "recovered" lands to the RIGHT of "hot", and a
# reader would take a 3 C span as a temperature characterisation. It is not
# one.
#
# What was actually controlled is the heating cycle, so that is what the axis
# shows. Temperature is annotated as the readout it is.
#
# Four integration slots would also need four categorical hues on one plot,
# and the palette only validates three slots all-pairs; faceting to one panel
# per slot keeps a single hue throughout.
def plot_temperature():
    rows = read_csv(DATA / "v50_period_vs_temperature.csv")
    if not rows:
        skipped.append("period_vs_temperature.png (run run_temperature_sweep.py)")
        return
    ORDER = ["cold", "hot", "recovered"]
    by_it = defaultdict(dict)
    temps = {}
    for r in rows:
        by_it[int(r["it_nominal_ms"])][r["phase"]] = float(r["ratio"])
        temps[r["phase"]] = float(r["temp_c"])
    its = sorted(by_it)
    guard = float(rows[0]["guard"])
    phases = [p for p in ORDER if p in temps] or sorted(temps)
    allr = [v for d in by_it.values() for v in d.values()]
    span = max(temps.values()) - min(temps.values())

    fig, axes = plt.subplots(1, len(its), figsize=(11.0, 3.8), sharey=True)
    if len(its) == 1:
        axes = [axes]
    xs = np.arange(len(phases))

    for ax, it in zip(axes, its):
        style(ax)
        r = [by_it[it].get(p, np.nan) for p in phases]
        ax.axhline(guard, color=CRITICAL, lw=1.0)
        ax.plot(xs, r, "-o", ms=7, lw=1.8, color=SERIES_1,
                markeredgecolor=SURFACE, markeredgewidth=1.5)
        for xi, rr in zip(xs, r):
            if np.isnan(rr):
                continue
            ax.annotate(f"{rr:.3f}", xy=(xi, rr), xytext=(0, 8),
                        textcoords="offset points", ha="center",
                        fontsize=7.5, color=INK_2)
        ax.set_title(f"IT {it} ms", fontsize=9.5)
        ax.set_xticks(xs)
        ax.set_xticklabels([f"{p}\n{temps[p]:.1f} C" for p in phases],
                           fontsize=8)
        ax.set_xlim(-0.45, len(phases) - 0.55)
    axes[0].set_ylabel("period / label")
    axes[0].set_ylim(min(allr) - 0.02, guard + 0.02)
    axes[-1].annotate(f"guard {guard:.2f}", xy=(len(phases) - 1, guard),
                      xytext=(0, 4), textcoords="offset points", ha="right",
                      fontsize=7.5, color=CRITICAL)

    fig.suptitle("The period held through a hard LED heating cycle "
                 "— but the thermal span is unproven",
                 x=0.5, y=1.0, fontsize=11, fontweight="semibold", ha="center")
    fig.tight_layout(rect=[0, 0.10, 1, 0.96])
    subtitle(fig, "Between 'cold' and 'hot' the LED ran 6 min at 100% duty, which certainly warmed "
                  "the optical assembly. The period did not move. But the only temperature\nthis "
                  "board can report is the ESP32 die (the VEML7700 has no temperature register), it "
                  f"spanned just {span:.1f} C, and it kept RISING through the cooling phase — so it "
                  "never tracked\nthe optics. This shows the period is unaffected by the heating the "
                  "device does to ITSELF. It does NOT establish a temperature coefficient, and does "
                  "not bound\nbehaviour at an ambient this bench never reached. To test that, change "
                  "the room temperature and re-run probe_period.")
    out = PLOTS / "period_vs_temperature.png"
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)
    made.append(out.name)


# =========================================================================
# 4. The sensor itself is linear in integration time
# =========================================================================
def plot_linearity():
    rows = read_csv(DATA / "v50_it_linearity.csv")
    if not rows:
        skipped.append("it_linearity_led_held.png (needs v50_it_linearity.csv)")
        return
    its = [int(r["it_nominal_ms"]) for r in rows]
    med = [float(r["median_raw"]) for r in rows]
    order = np.argsort(its)
    its = list(np.array(its)[order])
    med = list(np.array(med)[order])

    ideal = [med[0] * (i / its[0]) for i in its]

    fig, ax = plt.subplots(figsize=(6.8, 4.4))
    style(ax)
    # The measured line lands on the ideal to within 0.1%, so a hairline
    # reference would simply vanish underneath it and the label would appear to
    # point at nothing. Drawn as a broad band instead: the blue line visibly
    # riding inside it is the finding.
    ax.plot(its, ideal, "-", lw=7, color=GRID, zorder=1,
            solid_capstyle="round")
    ax.annotate("exact proportionality", xy=(its[0], ideal[0]),
                xytext=(10, -16), textcoords="offset points", ha="left",
                fontsize=7.5, color=MUTED)
    ax.plot(its, med, "-o", lw=1.8, ms=7, color=SERIES_1,
            markeredgecolor=SURFACE, markeredgewidth=1.5, zorder=3)

    for i in range(len(its) - 1):
        ratio = med[i + 1] / med[i]
        ax.annotate(f"x{ratio:.2f}",
                    xy=((its[i] * its[i + 1]) ** 0.5,
                        (med[i] * med[i + 1]) ** 0.5),
                    xytext=(8, -6), textcoords="offset points",
                    fontsize=8, color=INK_2)

    ax.set_xscale("log")
    ax.set_yscale("log")
    ax.set_xticks(its)
    ax.set_xticklabels([str(i) for i in its])
    # Log default gives a lone 10^4 tick. Label the actual measurements.
    ax.set_yticks(med)
    ax.set_yticklabels([f"{int(round(v)):,}" for v in med])
    ax.minorticks_off()
    ax.set_xlabel("nominal integration time  (ms)")
    ax.set_ylabel("sensor counts")
    ax.set_title("The sensor is sound; the pulse was the problem")
    fig.tight_layout(rect=[0, 0.06, 1, 1])
    subtitle(fig, "LED held ON continuously, so there is no off-to-on transition and no pulse timing "
                  "involved. Counts double per step, which also confirms the\nALS_IT register codes. "
                  "Any scatter seen with a pulsed LED is therefore the pulse, not the sensor.")
    out = PLOTS / "it_linearity_led_held.png"
    fig.savefig(out, bbox_inches="tight")
    plt.close(fig)
    made.append(out.name)


# =========================================================================
# 5. The blank table, before and after
# =========================================================================
def _grid(rows, key="i0"):
    its = sorted({int(r["it_nominal_ms"]) for r in rows})
    pwms = sorted({float(r["pwm_pct"]) for r in rows})
    g = np.full((len(its), len(pwms)), np.nan)
    for r in rows:
        g[its.index(int(r["it_nominal_ms"])),
          pwms.index(float(r["pwm_pct"]))] = float(r[key])
    return its, pwms, g


def plot_blank_tables():
    stale = read_csv(DATA / "blank_table_stale_pre_v46.csv")
    fresh = read_csv(DATA / "v50_blank_table.csv")
    if not stale or not fresh:
        skipped.append("blank_table_before_after.png (needs v50_blank_table.csv)")
        return

    panels = [("Stale table, written before v4.6", stale),
              ("Measured by v5.0", fresh)]
    fig, axes = plt.subplots(1, 2, figsize=(12.4, 4.8))
    SAT = 65530
    # One scale across both panels. Per-panel scales would make the dead table
    # look like it spans the same range as the good one -- the colour has to be
    # comparable for the comparison to mean anything.
    vmax = max(np.nanmax(np.where(_grid(r)[2] >= SAT, np.nan, _grid(r)[2]))
               for _, r in panels)
    im = None
    for ax, (title, rows) in zip(axes, panels):
        its, pwms, g = _grid(rows)
        shown = np.where(g >= SAT, np.nan, g)
        im = ax.imshow(shown, cmap=CMAP_BLUE, aspect="auto", vmin=0, vmax=vmax)
        ax.grid(False)
        for i in range(len(its)):
            for j in range(len(pwms)):
                v = g[i, j]
                if v >= SAT:
                    ax.add_patch(plt.Rectangle((j - .5, i - .5), 1, 1,
                                               facecolor=GRID, edgecolor=SURFACE,
                                               linewidth=2, hatch="///"))
                    ax.text(j, i, "sat", ha="center", va="center",
                            fontsize=7, color=INK_2)
                    continue
                # Text ink, not series color; flipped for contrast on dark cells.
                frac = v / (vmax or 1)
                ax.text(j, i, f"{int(v)}", ha="center", va="center",
                        fontsize=7.2,
                        color="#ffffff" if frac > 0.55 else INK)
        ax.set_xticks(range(len(pwms)))
        ax.set_xticklabels([f"{p:g}%" for p in pwms])
        ax.set_yticks(range(len(its)))
        ax.set_yticklabels([f"{i} ms" for i in its])
        ax.set_title(title, fontsize=10)
        ax.set_xlabel("LED duty")
        for s in ax.spines.values():
            s.set_visible(False)
    axes[0].set_ylabel("integration time")
    # Explicit geometry. tight_layout and a colorbar attached to a list of axes
    # fight each other, and the loser was the caption -- it landed on top of
    # the x-axis labels. Reserve the bottom band for the caption up front.
    fig.subplots_adjust(left=0.055, right=0.875, top=0.86, bottom=0.28,
                        wspace=0.12)
    # Shared scale legend -- required for a sequential encoding, and it is what
    # tells the reader the two panels are on the same ruler.
    cax = fig.add_axes([0.895, 0.28, 0.011, 0.58])
    cb = fig.colorbar(im, cax=cax)
    cb.set_label("I_0  (sensor counts)", color=INK_2, fontsize=8)
    cb.outline.set_visible(False)
    cb.ax.tick_params(length=0, labelsize=7.5, colors=MUTED)
    fig.legend(handles=[Patch(facecolor=GRID, hatch="///", edgecolor=SURFACE,
                              label="saturated during the sweep")],
               frameon=False, loc="lower left", fontsize=8,
               bbox_to_anchor=(0.055, 0.02), labelcolor=INK_2)
    fig.suptitle("Blanking table (I_0), empty beam", x=0.5, y=0.95,
                 fontsize=11, fontweight="semibold")
    subtitle(fig, "Left: the table found in NVS at the start of the session, written by a pre-v4.6 build "
                  "and carried through three upgrades because nothing checked which\nmeasurement path "
                  "produced it. Only its 100 ms row holds light; the rest is the dark, and cells of 9-109 "
                  "counts still passed as usable gears. Right: same optics,\nmeasured by v5.0 -- every "
                  "row doubles down the columns. v5.0 stamps an epoch on each saved blank so a stale one "
                  "cannot survive an upgrade again.", y=0.055)
    out = PLOTS / "blank_table_before_after.png"
    # No bbox_inches="tight" here: the geometry above is deliberate and tight
    # cropping would re-introduce the collision it was written to avoid.
    fig.savefig(out)
    plt.close(fig)
    made.append(out.name)


if __name__ == "__main__":
    PLOTS.mkdir(parents=True, exist_ok=True)
    plot_dropouts()
    plot_period_ratio()
    plot_temperature()
    plot_linearity()
    plot_blank_tables()

    print(f"\nwrote {len(made)} plot(s) to {PLOTS}")
    for m in made:
        print("  +", m)
    for s in skipped:
        print("  - skipped:", s)
