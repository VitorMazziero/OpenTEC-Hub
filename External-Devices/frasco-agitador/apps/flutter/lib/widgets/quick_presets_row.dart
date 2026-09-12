import 'package:flutter/material.dart';
import '../services/agitator_service.dart';
import '../theme/app_theme.dart';

class QuickPresetsRow extends StatelessWidget {
  final AgitatorService service;

  const QuickPresetsRow({super.key, required this.service});

  void _showDirectInputDialog(BuildContext context) {
    final controller = TextEditingController(text: service.targetDuty.toStringAsFixed(1));

    showDialog(
      context: context,
      builder: (ctx) {
        return AlertDialog(
          backgroundColor: AppTheme.darkCardElevated,
          title: const Text('Definir Velocidade Direta (%)', style: TextStyle(fontSize: 18)),
          content: TextField(
            controller: controller,
            autofocus: true,
            keyboardType: const TextInputType.numberWithOptions(decimal: true),
            textAlign: TextAlign.center,
            style: const TextStyle(fontSize: 24, fontWeight: FontWeight.bold),
            decoration: InputDecoration(
              suffixText: '%',
              hintText: '0.0 a 100.0',
              border: OutlineInputBorder(borderRadius: BorderRadius.circular(12)),
            ),
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.of(ctx).pop(),
              child: const Text('Cancelar'),
            ),
            ElevatedButton(
              style: ElevatedButton.styleFrom(
                backgroundColor: AppTheme.primaryCyan,
                foregroundColor: Colors.black,
              ),
              onPressed: () {
                final val = double.tryParse(controller.text.replaceAll(',', '.'));
                if (val != null) {
                  service.setDuty(val);
                }
                Navigator.of(ctx).pop();
              },
              child: const Text('Aplicar'),
            ),
          ],
        );
      },
    );
  }

  @override
  Widget build(BuildContext context) {
    const presets = [0.0, 25.0, 50.0, 75.0, 100.0];

    return Card(
      child: Padding(
        padding: const EdgeInsets.all(16.0),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: [
                const Text(
                  'Ajuste Rápido de Velocidade',
                  style: TextStyle(fontWeight: FontWeight.w600, fontSize: 15),
                ),
                IconButton(
                  tooltip: 'Digitar valor exato',
                  icon: const Icon(Icons.edit, size: 20, color: AppTheme.primaryCyan),
                  onPressed: () => _showDirectInputDialog(context),
                ),
              ],
            ),
            const SizedBox(height: 12),

            // Chips de Presets (0%, 25%, 50%, 75%, 100%)
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceBetween,
              children: presets.map((preset) {
                final isSelected = (service.targetDuty - preset).abs() < 0.1;
                return ActionChip(
                  label: Text('${preset.toInt()}%'),
                  backgroundColor: isSelected
                      ? AppTheme.primaryCyan.withValues(alpha: 0.25)
                      : Colors.white.withValues(alpha: 0.05),
                  side: BorderSide(
                    color: isSelected ? AppTheme.primaryCyan : Colors.white.withValues(alpha: 0.1),
                  ),
                  labelStyle: TextStyle(
                    color: isSelected ? AppTheme.primaryCyan : Colors.white,
                    fontWeight: isSelected ? FontWeight.bold : FontWeight.normal,
                    fontSize: 12,
                  ),
                  onPressed: () => service.setDuty(preset),
                );
              }).toList(),
            ),
            const Divider(height: 24, color: Colors.white12),

            // Botões de incremento e decremento fino
            Row(
              mainAxisAlignment: MainAxisAlignment.spaceEvenly,
              children: [
                _buildStepBtn(context, '-5%', () => service.adjustDuty(-5.0)),
                _buildStepBtn(context, '-1%', () => service.adjustDuty(-1.0)),
                _buildStepBtn(context, '-0.1%', () => service.adjustDuty(-0.1)),
                _buildStepBtn(context, '+0.1%', () => service.adjustDuty(0.1)),
                _buildStepBtn(context, '+1%', () => service.adjustDuty(1.0)),
                _buildStepBtn(context, '+5%', () => service.adjustDuty(5.0)),
              ],
            ),
          ],
        ),
      ),
    );
  }

  Widget _buildStepBtn(BuildContext context, String label, VoidCallback onTap) {
    return OutlinedButton(
      style: OutlinedButton.styleFrom(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
        minimumSize: const Size(44, 36),
        side: BorderSide(color: Colors.white.withValues(alpha: 0.15)),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      ),
      onPressed: onTap,
      child: Text(
        label,
        style: const TextStyle(fontSize: 11, color: Colors.white, fontWeight: FontWeight.w500),
      ),
    );
  }
}
