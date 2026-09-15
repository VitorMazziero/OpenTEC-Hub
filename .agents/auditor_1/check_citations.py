import re
from pathlib import Path

content = Path('IMPLEMENTATION_PLAN_BOMBA.md').read_text(encoding='utf-8')

pattern = r'([A-Za-z0-9_\-\.\/]+?\.(?:h|cpp|dart|cs|md|ino|json|py|yaml|properties))(?::(\d+)(?:-(\d+))?)?'
matches = re.findall(pattern, content)

print(f'Total citations found: {len(matches)}')
unique_citations = set(matches)
print(f'Unique citations: {len(unique_citations)}')

file_status = {}
line_checks = []

for file_path, start_line, end_line in unique_citations:
    p = Path(file_path)
    exists = p.exists()
    file_status[file_path] = exists
    if exists and start_line:
        lines = p.read_text(encoding='utf-8', errors='ignore').splitlines()
        s = int(start_line)
        e = int(end_line) if end_line else s
        valid_range = (1 <= s <= len(lines)) and (1 <= e <= len(lines))
        line_checks.append((file_path, s, e, valid_range, len(lines)))

missing_files = [f for f, exists in file_status.items() if not exists]
print(f'Files checked: {len(file_status)}')
print(f'Existing files: {len(file_status) - len(missing_files)}')
print(f'Missing files: {len(missing_files)}')

if missing_files:
    print('\nMissing files:')
    for mf in missing_files:
        print(f'  - {mf}')
else:
    print('\nALL cited file paths exist in the workspace!')

invalid_lines = [lc for lc in line_checks if not lc[3]]
print(f'\nLine range citations checked: {len(line_checks)}')
print(f'Valid line ranges: {len(line_checks) - len(invalid_lines)}')
print(f'Invalid line ranges: {len(invalid_lines)}')

if invalid_lines:
    print('\nInvalid line ranges:')
    for il in invalid_lines:
        print(f'  - {il[0]}:{il[1]}-{il[2]} (Total lines in file: {il[4]})')
else:
    print('ALL line number ranges are within valid file bounds!')
