import sys
from pathlib import Path

# Add project root to sys.path
sys.path.insert(0, '.')
import verify_plan_bomba

print('=== ADVERSARIAL TEST 1: Non-existent file ===')
res1 = verify_plan_bomba.verify_plan(Path('non_existent_file.md'))
assert res1 is False, 'Should return False for non-existent file'
print('Test 1 Passed: Returns False for non-existent file.\n')

print('=== ADVERSARIAL TEST 2: Empty file ===')
dummy_empty = Path('.agents/auditor_1/dummy_empty.md')
dummy_empty.write_text('', encoding='utf-8')
try:
    res2 = verify_plan_bomba.verify_plan(dummy_empty)
    assert res2 is False, 'Should return False for empty file'
    print('Test 2 Passed: Returns False for empty file.\n')
finally:
    if dummy_empty.exists():
        dummy_empty.unlink()

print('=== ADVERSARIAL TEST 3: Missing items ===')
dummy_partial = Path('.agents/auditor_1/dummy_partial.md')
dummy_partial.write_text('### Item 1.10.1\nPlano de Ação Técnico:\nAlterar código.', encoding='utf-8')
try:
    res3 = verify_plan_bomba.verify_plan(dummy_partial)
    assert res3 is False, 'Should return False for partial content'
    print('Test 3 Passed: Returns False when items are missing.\n')
finally:
    if dummy_partial.exists():
        dummy_partial.unlink()

print('=== ADVERSARIAL TEST 4: Present item without Action Plan or Justification ===')
dummy_no_plan = Path('.agents/auditor_1/dummy_no_plan.md')
lines = []
for it in verify_plan_bomba.SECTION_110_ITEMS:
    header = '#### ' + it['id'] if it['id'].startswith('D-SEC') else '### Item ' + it['id']
    lines.append(header + '\nTexto descritivo qualquer sem acao ou motivo.\n')
for it in verify_plan_bomba.SECTION_111_ITEMS:
    lines.append('### Item ' + it['id'] + '\nTexto descritivo de ensaio qualquer sem passos.\n')
dummy_no_plan.write_text('\n'.join(lines), encoding='utf-8')
try:
    res4 = verify_plan_bomba.verify_plan(dummy_no_plan)
    assert res4 is False, 'Should return False when plan/justification keywords are missing'
    print('Test 4 Passed: Returns False when plan/justification keywords are missing.\n')
finally:
    if dummy_no_plan.exists():
        dummy_no_plan.unlink()

print('=== ADVERSARIAL TEST 5: Real plan verification ===')
real_plan = Path('IMPLEMENTATION_PLAN_BOMBA.md')
res5 = verify_plan_bomba.verify_plan(real_plan)
assert res5 is True, 'Should return True for authentic complete plan'
print('Test 5 Passed: Returns True for authentic complete plan.\n')

print('ALL 5 ADVERSARIAL INTEGRITY TESTS PASSED EMPIRICALLY!')
