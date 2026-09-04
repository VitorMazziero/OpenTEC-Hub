# kLa cross-language fixture generator

This development-only tool executes the paper project's Python/SciPy functions unchanged
and records the numerical oracle used by `KlaMappingScientificTests`. It is not called by
the application and does not make Python a production dependency.

```powershell
python tools\kla-reference\generate_fixture.py `
  --paper-script "D:\path\to\04_Cascata_kLa\analysis\1_kla_mapping_gradient\gradient_path_score.py" `
  --output tests\fixtures\kla-reference-scipy.json `
  --processes 4
```

The candidate rows are independent and are evaluated in worker processes. Parallelism
changes only execution order; the saved score grid is restored to the paper script's
row/column order before its maximum and SHA-256 are calculated. The fixture records a
portable source filename and content hash; elapsed time, worker count and machine-local
paths are deliberately excluded so reruns do not create metadata-only diffs.

On machines whose NumPy/SciPy installation uses a threaded BLAS, set
`OPENBLAS_NUM_THREADS=1`, `OMP_NUM_THREADS=1` and `MKL_NUM_THREADS=1` for this command.
Worker-level parallelism is already sufficient and avoids nested oversubscription.
