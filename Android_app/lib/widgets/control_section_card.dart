import 'package:flutter/material.dart';

/// One control per card: the header always shows what is running (title, a one-line
/// [status] and the on/off switch); tapping it opens the setpoint form and its
/// Apply button. Collapsed by default so the Controls tab reads as a short list.
/// [header] stays visible when collapsed, for choices that must always be in view
/// (the control route of temperature and agitation).
class ControlSectionCard extends StatefulWidget {
  final String title;
  final String? status;
  final IconData icon;
  final Color accentColor;
  final bool? isEnabled;
  final ValueChanged<bool>? onToggle;
  final List<Widget> children;
  final VoidCallback? onApply;
  final String applyButtonLabel;
  final bool isBusy;
  final bool initiallyExpanded;
  final Widget? header;

  const ControlSectionCard({
    super.key,
    required this.title,
    this.status,
    required this.icon,
    required this.accentColor,
    this.isEnabled,
    this.onToggle,
    required this.children,
    this.onApply,
    this.applyButtonLabel = "Aplicar",
    this.isBusy = false,
    this.initiallyExpanded = false,
    this.header,
  });

  @override
  State<ControlSectionCard> createState() => _ControlSectionCardState();
}

class _ControlSectionCardState extends State<ControlSectionCard> {
  late bool _expanded = widget.initiallyExpanded;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final active = widget.isEnabled ?? true;
    final accent = widget.accentColor;

    return Card(
      clipBehavior: Clip.antiAlias,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.fromLTRB(14, 12, 8, 12),
              child: Row(
                children: [
                  Container(
                    width: 40,
                    height: 40,
                    decoration: BoxDecoration(
                      color: accent.withValues(alpha: active ? 0.16 : 0.07),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Icon(widget.icon, size: 22, color: active ? accent : theme.disabledColor),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(
                          widget.title,
                          style: theme.textTheme.titleMedium?.copyWith(fontWeight: FontWeight.w600),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                        if (widget.status != null)
                          Text(
                            widget.status!,
                            style: theme.textTheme.bodySmall?.copyWith(
                              color: active ? accent : theme.colorScheme.outline,
                              fontWeight: FontWeight.w500,
                            ),
                            maxLines: 2,
                            overflow: TextOverflow.ellipsis,
                          ),
                      ],
                    ),
                  ),
                  if (widget.isEnabled != null)
                    Switch(
                      value: widget.isEnabled!,
                      onChanged: widget.isBusy ? null : widget.onToggle,
                      activeThumbColor: accent,
                      materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
                    ),
                  Icon(
                    _expanded ? Icons.expand_less : Icons.expand_more,
                    color: theme.colorScheme.outline,
                  ),
                ],
              ),
            ),
          ),
          if (widget.header != null)
            Padding(
              padding: const EdgeInsets.fromLTRB(14, 0, 14, 12),
              child: widget.header,
            ),
          AnimatedCrossFade(
            firstChild: const SizedBox(width: double.infinity),
            secondChild: Padding(
              padding: const EdgeInsets.fromLTRB(14, 0, 14, 14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  Divider(height: 1, color: theme.colorScheme.outlineVariant.withValues(alpha: 0.6)),
                  const SizedBox(height: 14),
                  ...widget.children,
                  if (widget.onApply != null) ...[
                    const SizedBox(height: 14),
                    FilledButton.icon(
                      style: FilledButton.styleFrom(
                        backgroundColor: accent,
                        foregroundColor: Colors.white,
                        minimumSize: const Size.fromHeight(44),
                      ),
                      onPressed: widget.isBusy ? null : widget.onApply,
                      icon: widget.isBusy
                          ? const SizedBox(
                              width: 16,
                              height: 16,
                              child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white),
                            )
                          : const Icon(Icons.send, size: 18),
                      label: Text(widget.applyButtonLabel),
                    ),
                  ],
                ],
              ),
            ),
            crossFadeState: _expanded ? CrossFadeState.showSecond : CrossFadeState.showFirst,
            duration: const Duration(milliseconds: 180),
          ),
        ],
      ),
    );
  }
}

/// Two fields side by side, the common layout of the setpoint forms.
class FieldRow extends StatelessWidget {
  final List<Widget> children;
  const FieldRow(this.children, {super.key});

  @override
  Widget build(BuildContext context) {
    final spaced = <Widget>[];
    for (var i = 0; i < children.length; i++) {
      if (i > 0) spaced.add(const SizedBox(width: 10));
      spaced.add(Expanded(child: children[i]));
    }
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: spaced),
    );
  }
}

/// Numeric text field with label, unit suffix and optional helper text.
class NumberField extends StatelessWidget {
  final TextEditingController controller;
  final String label;
  final String? unit;
  final String? helper;
  final bool enabled;
  final bool decimal;
  final ValueChanged<String>? onChanged;

  const NumberField({
    super.key,
    required this.controller,
    required this.label,
    this.unit,
    this.helper,
    this.enabled = true,
    this.decimal = true,
    this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: controller,
      enabled: enabled,
      onChanged: onChanged,
      keyboardType: TextInputType.numberWithOptions(decimal: decimal),
      decoration: InputDecoration(
        labelText: label,
        suffixText: unit,
        helperText: helper,
        helperMaxLines: 2,
      ),
    );
  }
}

/// Parses user input accepting a decimal comma. Null for empty or invalid text.
double? parseNumber(String text) => double.tryParse(text.trim().replaceAll(',', '.'));
int? parseInt(String text) {
  final v = parseNumber(text);
  return v?.round();
}

/// Short help text under a form.
class HelpText extends StatelessWidget {
  final String text;
  final bool warning;
  const HelpText(this.text, {super.key, this.warning = false});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = warning ? Colors.orange.shade800 : theme.colorScheme.onSurfaceVariant;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(warning ? Icons.warning_amber_rounded : Icons.info_outline, size: 16, color: color),
          const SizedBox(width: 6),
          Expanded(child: Text(text, style: theme.textTheme.bodySmall?.copyWith(color: color))),
        ],
      ),
    );
  }
}
