# Code Conventions

> Short guide. Formatting and naming are enforced by `.editorconfig` at build time
> (`EnforceCodeStyleInBuild`); the rest is team convention.
>
> **Docs:** [README](README.md) · [Architecture](ARCHITECTURE.md) · [Decisions](DECISIONS.md)

---

## Language

**Code is English. The UI is pt-BR.** ([D-007](DECISIONS.md#d-007--pt-br-ui-english-code))

- Identifiers, comments, XML docs, log messages and commit messages: **English**.
- Every string the operator can see: **pt-BR**, and it lives in a `.resx` — never
  inline in XAML or C#.
- Log messages stay English and are deliberately **not** localised: they must remain
  greppable, and they are read by developers, not operators.

---

## Naming

| Element | Convention | Example |
|---|---|---|
| Types, methods, public properties | `PascalCase` | `ConnectionManager`, `SendAsync` |
| Private fields | `_camelCase` | `_transport`, `_settings` |
| Interfaces | `I` prefix | `ITransport`, `IDialogService` |
| Async methods | `Async` suffix | `ConnectAsync` |
| Constants | `PascalCase` | `BootSettleMilliseconds` |
| ViewModels | `<Screen>ViewModel` | `SynopticViewModel` |
| Wire-format constants | Name them after the **JSON key** | `const string MotorSetpointKey = "motorSetpoint";` |

That last row matters: when a name on the wire is ugly (`v_Flow`, `pHCal`), the C#
constant keeps the ugly name so a `grep` for the JSON key finds the code that emits it.

---

## Layering rules

- **Views have no logic.** No `MessageBox.Show`, no service references, no
  business rules in code-behind. Only visual-tree concerns XAML cannot express.
- **ViewModels never open dialogs** — inject `IDialogService`.
- **Services are always behind an interface** and registered in `App.xaml.cs`.
- **`TecnalHub.Protocol` never references WPF.** Enforced by the project having no
  such reference; keep it that way.
- **Only `ConnectionManager` touches a transport.**
  ([ARCHITECTURE.md](ARCHITECTURE.md#2-threading-model))

---

## The wire

Rules that exist because breaking them breaks real hardware
([PROTOCOL.md](PROTOCOL.md)):

1. **`CultureInfo.InvariantCulture` on every numeric conversion that reaches the wire.**
   Not "usually" — always. A pt-BR machine will otherwise emit `6,98`.
2. **Never change a JSON key, unit, or range** without updating
   [PROTOCOL.md](PROTOCOL.md) and its golden-string test in the same commit.
3. **Timing constants are load-bearing.** The 1.8 s USB boot settle, the DTR/RTS
   pulse, the 0.98x poll factor. Do not tune them because they look arbitrary.
4. **Add the test before the command.** New command goes in
   [PROTOCOL.md §4](PROTOCOL.md#4-golden-strings) first.

---

## Async

- `async`/`await` throughout the service layer. No `.Result`, no `.Wait()`, no
  `async void` except event handlers.
- Every long-running operation takes a `CancellationToken` — including port probing,
  which in v.6 could not be cancelled at all
  ([MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6) item 1).
- `ConfigureAwait(false)` in `TecnalHub.Protocol` (no UI context to return to).

---

## Error handling

- **No silent defaults.** v.6's `except: ph_value = 7` sent setpoint 7 to a reactor
  after a typo and said nothing. Invalid operator input is shown as invalid and the
  command is not sent. ([MIGRATION.md](MIGRATION.md#3-known-defects-carried-in-from-v6) item 11)
- Catch specific exceptions. A bare `catch` needs a comment explaining why swallowing
  is correct.
- Link faults feed the connection state machine — they are not logged and forgotten.

---

## Formatting

UTF-8, CRLF, 4-space indent (2 in `.csproj` / `.xaml` / JSON). One public type per
file, except a `partial` class deliberately split by responsibility.

---

## Comments

Explain **why**, not what. The reader can see what the code does; they cannot see
that 1.8 s is the ESP32 bootloader settle time, or that `v_Flow` is inverted because
the firmware says so. Those are exactly the comments worth writing.
