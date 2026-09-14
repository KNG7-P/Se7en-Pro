import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/app_colors.dart';
import 'app_card.dart';

class SettingsSection extends StatelessWidget {
  const SettingsSection({
    super.key,
    required this.title,
    this.icon,
    this.iconColor,
    required this.children,
  });

  final String title;
  final IconData? icon;
  final Color? iconColor;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final rows = <Widget>[];
    for (var i = 0; i < children.length; i++) {
      rows.add(children[i]);
      if (i != children.length - 1) {
        rows.add(Divider(height: 1, thickness: 1, color: c.border));
      }
    }
    return AppCard(
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 6),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SectionTitle(title, icon: icon, iconColor: iconColor),
          const SizedBox(height: 6),
          ...rows,
        ],
      ),
    );
  }
}

class SettingRow extends StatelessWidget {
  const SettingRow({
    super.key,
    required this.label,
    this.description,
    required this.trailing,
    this.enabled = true,
  });

  final String label;
  final String? description;
  final Widget trailing;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Opacity(
      opacity: enabled ? 1 : 0.45,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: FontWeight.w500,
                        color: c.textPrimary,
                      )),
                  if (description != null) ...[
                    const SizedBox(height: 2),
                    Text(description!,
                        style: TextStyle(fontSize: 11, color: c.textMuted)),
                  ],
                ],
              ),
            ),
            const SizedBox(width: 14),
            IgnorePointer(ignoring: !enabled, child: trailing),
          ],
        ),
      ),
    );
  }
}

class SettingSwitch extends StatelessWidget {
  const SettingSwitch({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.onChanged,
    this.enabled = true,
  });

  final String label;
  final String? description;
  final bool value;
  final ValueChanged<bool> onChanged;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    return SettingRow(
      label: label,
      description: description,
      enabled: enabled,
      trailing: Switch(value: value, onChanged: enabled ? onChanged : null),
    );
  }
}

class SettingDropdown<T> extends StatelessWidget {
  const SettingDropdown({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.items,
    required this.onChanged,
    this.enabled = true,
  });

  final String label;
  final String? description;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T> onChanged;
  final bool enabled;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return SettingRow(
      label: label,
      description: description,
      enabled: enabled,
      trailing: Container(
        constraints: const BoxConstraints(minWidth: 130, maxWidth: 220),
        padding: const EdgeInsets.symmetric(horizontal: 12),
        decoration: BoxDecoration(
          color: c.input,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: c.border),
        ),
        child: DropdownButtonHideUnderline(
          child: DropdownButton<T>(
            value: value,
            isDense: true,
            isExpanded: true,
            borderRadius: BorderRadius.circular(10),
            dropdownColor: c.cardElevated,
            icon: Icon(Icons.expand_more, size: 18, color: c.textSecondary),
            style: TextStyle(fontSize: 12.5, color: c.textPrimary),
            onChanged: enabled ? (v) => v == null ? null : onChanged(v) : null,
            items: [
              for (final e in items.entries)
                DropdownMenuItem(value: e.key, child: Text(e.value)),
            ],
          ),
        ),
      ),
    );
  }
}

class SettingTextField extends StatefulWidget {
  const SettingTextField({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.onChanged,
    this.hint,
    this.maxLines = 1,
    this.obscure = false,
    this.keyboardType,
    this.inputFormatters,
    this.enabled = true,
  });

  final String label;
  final String? description;
  final String value;
  final ValueChanged<String> onChanged;
  final String? hint;
  final int maxLines;
  final bool obscure;
  final TextInputType? keyboardType;
  final List<TextInputFormatter>? inputFormatters;
  final bool enabled;

  @override
  State<SettingTextField> createState() => _SettingTextFieldState();
}

class _SettingTextFieldState extends State<SettingTextField> {
  late final TextEditingController _ctrl =
      TextEditingController(text: widget.value);
  bool _obscured = true;

  @override
  void didUpdateWidget(SettingTextField old) {
    super.didUpdateWidget(old);
    if (widget.value != _ctrl.text && !_ctrl.selection.isValid) {
      _ctrl.text = widget.value;
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Opacity(
      opacity: widget.enabled ? 1 : 0.45,
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 11),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(widget.label,
                style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                    color: c.textPrimary)),
            if (widget.description != null) ...[
              const SizedBox(height: 2),
              Text(widget.description!,
                  style: TextStyle(fontSize: 11, color: c.textMuted)),
            ],
            const SizedBox(height: 8),
            TextField(
              controller: _ctrl,
              enabled: widget.enabled,
              onChanged: widget.onChanged,
              maxLines: widget.obscure ? 1 : widget.maxLines,
              obscureText: widget.obscure && _obscured,
              keyboardType: widget.keyboardType,
              inputFormatters: widget.inputFormatters,
              style: TextStyle(fontSize: 12.5, color: c.textPrimary),
              decoration: InputDecoration(
                hintText: widget.hint,
                isDense: true,
                filled: true,
                fillColor: c.input,
                contentPadding:
                    const EdgeInsets.symmetric(horizontal: 12, vertical: 11),
                suffixIcon: widget.obscure
                    ? IconButton(
                        icon: Icon(
                            _obscured
                                ? Icons.visibility_off_rounded
                                : Icons.visibility_rounded,
                            size: 17,
                            color: c.textSecondary),
                        onPressed: () => setState(() => _obscured = !_obscured),
                      )
                    : null,
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide: BorderSide(color: c.border),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide: BorderSide(color: c.border),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide:
                      const BorderSide(color: BrandColors.accentCyan, width: 1.4),
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class SettingPortField extends StatefulWidget {
  const SettingPortField({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.onChanged,
    this.enabled = true,
  });

  final String label;
  final String? description;
  final int value;
  final ValueChanged<int> onChanged;
  final bool enabled;

  @override
  State<SettingPortField> createState() => _SettingPortFieldState();
}

class _SettingPortFieldState extends State<SettingPortField> {
  late final TextEditingController _ctrl =
      TextEditingController(text: widget.value == 0 ? '' : '${widget.value}');

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return SettingRow(
      label: widget.label,
      description: widget.description,
      enabled: widget.enabled,
      trailing: SizedBox(
        width: 100,
        child: TextField(
          controller: _ctrl,
          enabled: widget.enabled,
          keyboardType: TextInputType.number,
          inputFormatters: [
            FilteringTextInputFormatter.digitsOnly,
            LengthLimitingTextInputFormatter(5),
          ],
          onChanged: (v) => widget.onChanged(int.tryParse(v) ?? 0),
          style: TextStyle(fontSize: 12.5, color: c.textPrimary),
          decoration: InputDecoration(
            hintText: 'auto',
            isDense: true,
            filled: true,
            fillColor: c.input,
            contentPadding:
                const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
            border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: c.border),
            ),
            enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(8),
              borderSide: BorderSide(color: c.border),
            ),
          ),
        ),
      ),
    );
  }
}
