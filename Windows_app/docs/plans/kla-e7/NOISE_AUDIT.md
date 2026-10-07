# E7 — Auditoria sintética de ruído e intervalos

160 trajetórias, em oito cenários com 20 sementes cada. Núcleo e critérios de E3 mantidos; nenhuma aquisição experimental usada como verdade ou para ajustar os critérios. O JSON registra configuração, versão do runtime, hash da DLL analisada, sementes, estimativas, intervalos, janelas e recusas individuais.

Verdade analítica: kLa = 72 h⁻¹. Abiótico: Ceq = 100%, recuperação a partir de 10%. Biótico: OUR = 1800 pp/h, C* = 100%, Ceq = 75%; consumo desde 75% até 15% em 120 s, seguido por 300 s de recuperação. Cadência de 2 s. Resposta da sonda desprezível e condições/consumo constantes são hipóteses conhecidas somente nesta simulação.

Ruído gaussiano em pontos percentuais de OD. O cenário correlacionado usa AR(1), rho = 0,8, com desvio padrão estacionário de 0,2 pp. O cenário de outliers soma ±3 pp alternadamente a cada 37 observações, além do ruído de 0,2 pp. Não se removem pontos; a seleção de janela do núcleo permanece auditável.

| Protocolo | Ruído | kLa disponível | Viés (h⁻¹) | Dispersão (h⁻¹) | Cobertura do IC entre estimados |
|---|---|---:|---:|---:|---:|
| Abiótico | Branco, σ=0,1 pp | 14/20 | +0,39 | 0,30 | 78,6% |
| Abiótico | Branco, σ=0,5 pp | 14/20 | +1,88 | 1,78 | 50,0% |
| Abiótico | Correlacionado, σ=0,2 pp | 12/20 | +1,88 | 1,55 | 8,3% |
| Abiótico | Outliers, σ=0,2 pp | 0/20 | — | — | — |
| Biótico | Branco, σ=0,1 pp | 20/20 | +0,17 | 0,39 | 80,0% |
| Biótico | Branco, σ=0,5 pp | 20/20 | +0,51 | 1,68 | 95,0% |
| Biótico | Correlacionado, σ=0,2 pp | 20/20 | +0,34 | 2,11 | 45,0% |
| Biótico | Outliers, σ=0,2 pp | 20/20 | −0,06 | 0,51 | 95,0% |

| Ruído biótico | OUR disponível | Viés (pp/h) | Dispersão (pp/h) | Cobertura do IC entre estimados |
|---|---:|---:|---:|---:|
| Branco, σ=0,1 pp | 20/20 | −0,28 | 1,48 | 95,0% |
| Branco, σ=0,5 pp | 20/20 | −1,42 | 7,39 | 95,0% |
| Correlacionado, σ=0,2 pp | 16/20 | −1,17 | 7,92 | 50,0% |
| Outliers, σ=0,2 pp | 20/20 | +1,53 | 2,96 | 100,0% |

“Disponível” significa estimativa numérica utilizável sob a qualidade declarada, não aceite do operador ou liberação operacional. O JSON separa qualidades e motivos de recusa; o denominador da cobertura exclui estimativas/intervalos indisponíveis. Dispersão é desvio padrão amostral entre estimados. As mesmas sementes pareadas entre cenários não criam experimentos independentes.

Os resultados demonstram que o IC OLS condicional não incorpora toda a incerteza de Ceq, seleção adaptativa e ruído correlacionado. Vinte repetições por cenário são exploratórias; 95% observado em um cenário não comprova cobertura nominal. Não alterar os critérios para conseguir mais aceites. Estudos de incerteza total, sonda real, fisiologia e novos cultivos independentes permanecem necessários.

Na tela, o intervalo principal agora aparece indisponível quando o balanço recusa kLa, mesmo se a regressão candidata tiver calculado um intervalo. O diagnóstico bruto fica preservado. Dois testes passaram, incluindo balanço deliberadamente incompatível com R² alto; evidência em `evidence/kla-e7-inconclusive-interval.trx`.

Reprodução, a partir da raiz do repositório:

```powershell
dotnet run --project Windows_app/tools/KlaCorpusAudit -c Release -- --noise Windows_app/docs/plans/kla-e7/noise-audit.json
```

A execução é offline e não referencia serviços de atuação. A referência ao aplicativo fornece apenas o núcleo de análise.
