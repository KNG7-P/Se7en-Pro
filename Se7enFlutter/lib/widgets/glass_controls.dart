import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../theme/app_colors.dart';

class GlassCard extends StatelessWidget {
  const GlassCard({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.borderRadius = 16,
    this.glowColor,
    this.glowOpacity = 0.0,
    this.fillColor,
    this.clipBehavior = Clip.antiAlias,
  });

  final Widget child;
  final EdgeInsetsGeometry padding;
  final double borderRadius;
  final Color? glowColor;
  final double glowOpacity;
  final Color? fillColor;
  final Clip clipBehavior;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final r = BorderRadius.circular(borderRadius);

    return Container(
      clipBehavior: clipBehavior,
      decoration: BoxDecoration(
        borderRadius: r,
        color: fillColor ?? c.glass,
        border: Border.all(
          color: c.glassBorder,
          width: 1,
        ),
        boxShadow: glowColor != null && glowOpacity > 0
            ? [
                BoxShadow(
                  color: glowColor!.withValues(alpha: glowOpacity),
                  blurRadius: 24,
                  spreadRadius: -4,
                ),
              ]
            : null,
      ),
      child: Padding(
        padding: padding,
        child: child,
      ),
    );
  }
}

class GlassSegmentedControl<T> extends StatelessWidget {
  const GlassSegmentedControl({
    super.key,
    required this.segments,
    required this.selected,
    required this.onChanged,
  });

  final Map<T, String> segments;
  final T selected;
  final ValueChanged<T> onChanged;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Container(
      padding: const EdgeInsets.all(4),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.6),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: c.border.withValues(alpha: 0.5)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (final entry in segments.entries) ...[
            _SegmentPill(
              title: entry.value,
              isSelected: entry.key == selected,
              onTap: () => onChanged(entry.key),
            ),
          ],
        ],
      ),
    );
  }
}

class _SegmentPill extends StatelessWidget {
  const _SegmentPill({
    required this.title,
    required this.isSelected,
    required this.onTap,
  });

  final String title;
  final bool isSelected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return GestureDetector(
      onTap: onTap,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 180),
        curve: Curves.easeOut,
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 7),
        decoration: BoxDecoration(
          color: isSelected
              ? (c.isDark
                  ? BrandColors.accentCyan.withValues(alpha: 0.22)
                  : BrandColors.primary.withValues(alpha: 0.12))
              : Colors.transparent,
          borderRadius: BorderRadius.circular(9),
          border: isSelected
              ? Border.all(
                  color: c.isDark
                      ? BrandColors.accentCyan.withValues(alpha: 0.4)
                      : BrandColors.primary.withValues(alpha: 0.3),
                )
              : null,
        ),
        child: Text(
          title,
          style: TextStyle(
            fontSize: 12,
            fontWeight: isSelected ? FontWeight.w700 : FontWeight.w500,
            color: isSelected
                ? (c.isDark ? BrandColors.accentCyan : BrandColors.primary)
                : c.textSecondary,
          ),
        ),
      ),
    );
  }
}

class GlassSwitchRow extends StatelessWidget {
  const GlassSwitchRow({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.onChanged,
    this.icon,
    this.iconColor,
  });

  final String label;
  final String? description;
  final bool value;
  final ValueChanged<bool> onChanged;
  final IconData? icon;
  final Color? iconColor;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return InkWell(
      onTap: () => onChanged(!value),
      borderRadius: BorderRadius.circular(8),
      hoverColor: c.isDark ? Colors.white.withValues(alpha: 0.03) : Colors.black.withValues(alpha: 0.02),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 4),
        child: Row(
          children: [
            if (icon != null) ...[
              Container(
                padding: const EdgeInsets.all(7),
                decoration: BoxDecoration(
                  color: (iconColor ?? BrandColors.accentCyan).withValues(alpha: 0.12),
                  borderRadius: BorderRadius.circular(8),
                ),
                child: Icon(icon, size: 16, color: iconColor ?? BrandColors.accentCyan),
              ),
              const SizedBox(width: 12),
            ],
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    label,
                    style: TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                      color: c.textPrimary,
                    ),
                  ),
                  if (description != null && description!.isNotEmpty) ...[
                    const SizedBox(height: 2),
                    Text(
                      description!,
                      style: TextStyle(fontSize: 11, color: c.textMuted),
                    ),
                  ],
                ],
              ),
            ),
            const SizedBox(width: 12),
            Transform.scale(
              scale: 0.85,
              child: IgnorePointer(
                child: Switch(
                  value: value,
                  onChanged: (_) {},
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class GlassDropdownRow<T> extends StatefulWidget {
  const GlassDropdownRow({
    super.key,
    required this.label,
    this.description,
    required this.value,
    required this.items,
    required this.onChanged,
    this.icon,
    this.iconColor,
  });

  final String label;
  final String? description;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T> onChanged;
  final IconData? icon;
  final Color? iconColor;

  @override
  State<GlassDropdownRow<T>> createState() => _GlassDropdownRowState<T>();
}

class _GlassDropdownRowState<T> extends State<GlassDropdownRow<T>> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 9),
      child: Row(
        children: [
          if (widget.icon != null) ...[
            Container(
              padding: const EdgeInsets.all(7),
              decoration: BoxDecoration(
                color: (widget.iconColor ?? BrandColors.accentCyan).withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(8),
              ),
              child: Icon(widget.icon, size: 16, color: widget.iconColor ?? BrandColors.accentCyan),
            ),
            const SizedBox(width: 12),
          ],
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  widget.label,
                  style: TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w600,
                    color: c.textPrimary,
                  ),
                ),
                if (widget.description != null) ...[
                  const SizedBox(height: 2),
                  Text(
                    widget.description!,
                    style: TextStyle(fontSize: 11, color: c.textMuted),
                  ),
                ],
              ],
            ),
          ),
          const SizedBox(width: 12),
          MouseRegion(
            cursor: SystemMouseCursors.click,
            onEnter: (_) => setState(() => _hovered = true),
            onExit: (_) => setState(() => _hovered = false),
            child: ClipRRect(
              borderRadius: BorderRadius.circular(10),
              child: Material(
                color: Colors.transparent,
                child: ModernPopupMenuButton<T>(
                  onSelected: widget.onChanged,
                  itemBuilder: (context) => [
                    for (final entry in widget.items.entries)
                      PopupMenuItem<T>(
                        value: entry.key,
                        padding: EdgeInsets.zero,
                        height: 38,
                        child: ModernPopupHoverTile(
                          child: Row(
                            children: [
                              Expanded(
                                child: Text(
                                  entry.value,
                                  style: TextStyle(
                                    fontSize: 12.5,
                                    fontWeight: entry.key == widget.value
                                        ? FontWeight.w700
                                        : FontWeight.w500,
                                    color: entry.key == widget.value
                                        ? BrandColors.accentCyan
                                        : c.textPrimary,
                                  ),
                                ),
                              ),
                              if (entry.key == widget.value) ...[
                                const SizedBox(width: 10),
                                const Icon(Icons.check_rounded, size: 16, color: BrandColors.accentCyan),
                              ],
                            ],
                          ),
                        ),
                      ),
                  ],
                  child: AnimatedContainer(
                    duration: const Duration(milliseconds: 140),
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                    decoration: BoxDecoration(
                      color: _hovered
                          ? c.cardElevated.withValues(alpha: 0.9)
                          : c.input.withValues(alpha: 0.65),
                      borderRadius: BorderRadius.circular(10),
                      border: Border.all(
                        color: _hovered
                            ? BrandColors.accentCyan.withValues(alpha: 0.55)
                            : c.border.withValues(alpha: 0.6),
                      ),
                    ),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        ConstrainedBox(
                          constraints: const BoxConstraints(maxWidth: 240),
                          child: Text(
                            widget.items[widget.value] ?? widget.value.toString(),
                            style: TextStyle(
                              fontSize: 12.5,
                              fontWeight: FontWeight.w600,
                              color: c.textPrimary,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        const SizedBox(width: 8),
                        Icon(Icons.unfold_more_rounded, size: 16, color: c.textSecondary),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class GlassTextField extends StatefulWidget {
  const GlassTextField({
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
    this.icon,
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
  final IconData? icon;

  @override
  State<GlassTextField> createState() => _GlassTextFieldState();
}

class _GlassTextFieldState extends State<GlassTextField> {
  late final TextEditingController _ctrl =
      TextEditingController(text: widget.value);
  bool _obscured = true;

  @override
  void didUpdateWidget(GlassTextField old) {
    super.didUpdateWidget(old);
    if (widget.value != old.value && widget.value != _ctrl.text) {
      final sel = _ctrl.selection;
      final wasFocused = sel.isValid;
      _ctrl.text = widget.value;
      if (wasFocused) {
        try {
          final end = _ctrl.text.length;
          final base = sel.baseOffset.clamp(0, end);
          final extent = sel.extentOffset.clamp(0, end);
          _ctrl.selection = sel.copyWith(baseOffset: base, extentOffset: extent);
        } catch (_) { _ctrl.selection = TextSelection.collapsed(offset: _ctrl.text.length); }
      }
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

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 9),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              if (widget.icon != null) ...[
                Icon(widget.icon, size: 14, color: BrandColors.accentCyan),
                const SizedBox(width: 6),
              ],
              Text(
                widget.label,
                style: TextStyle(
                  fontSize: 12.5,
                  fontWeight: FontWeight.w600,
                  color: c.textPrimary,
                ),
              ),
            ],
          ),
          if (widget.description != null) ...[
            const SizedBox(height: 3),
            Text(
              widget.description!,
              style: TextStyle(fontSize: 11, color: c.textMuted),
            ),
          ],
          const SizedBox(height: 7),
          SizedBox(
            height: widget.maxLines == 1 ? 42 : null,
            child: TextField(
              controller: _ctrl,
              onChanged: widget.onChanged,
              maxLines: widget.obscure ? 1 : widget.maxLines,
              obscureText: widget.obscure && _obscured,
              keyboardType: widget.keyboardType,
              inputFormatters: widget.inputFormatters,
              textAlignVertical: TextAlignVertical.center,
              style: TextStyle(fontSize: 12.5, color: c.textPrimary),
              decoration: InputDecoration(
                hintText: widget.hint,
                hintStyle: TextStyle(fontSize: 12, color: c.textMuted),
                isDense: true,
                filled: true,
                fillColor: c.input.withValues(alpha: 0.65),
                contentPadding: const EdgeInsets.symmetric(
                  horizontal: 12,
                  vertical: 12,
                ),
                suffixIcon: widget.obscure
                    ? IconButton(
                        icon: Icon(
                          _obscured
                              ? Icons.visibility_off_rounded
                              : Icons.visibility_rounded,
                          size: 16,
                          color: c.textSecondary,
                        ),
                        onPressed: () => setState(() => _obscured = !_obscured),
                      )
                    : null,
                border: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                ),
                enabledBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide: BorderSide(color: c.border.withValues(alpha: 0.6)),
                ),
                focusedBorder: OutlineInputBorder(
                  borderRadius: BorderRadius.circular(9),
                  borderSide: const BorderSide(
                      color: BrandColors.accentCyan, width: 1.4),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class ModernPopupMenuButton<T> extends StatelessWidget {
  const ModernPopupMenuButton({
    super.key,
    required this.itemBuilder,
    this.initialValue,
    this.onSelected,
    this.child,
    this.tooltip,
    this.enabled = true,
    this.constraints,
  });

  final PopupMenuItemBuilder<T> itemBuilder;
  final T? initialValue;
  final PopupMenuItemSelected<T>? onSelected;
  final Widget? child;
  final String? tooltip;
  final bool enabled;
  final BoxConstraints? constraints;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Theme(
      data: Theme.of(context).copyWith(
        hoverColor: Colors.transparent,
        splashColor: Colors.transparent,
        highlightColor: Colors.transparent,
      ),
      child: PopupMenuButton<T>(
        tooltip: tooltip,
        enabled: enabled,
        initialValue: initialValue,
        constraints: constraints,
        color: c.cardElevated,
        elevation: 10,
        borderRadius: BorderRadius.circular(12),
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(14),
          side: BorderSide(color: c.border.withValues(alpha: 0.6)),
        ),
        menuPadding: const EdgeInsets.all(6),
        position: PopupMenuPosition.under,
        onSelected: onSelected,
        itemBuilder: itemBuilder,
        child: child,
      ),
    );
  }
}

class ModernPopupHoverTile extends StatefulWidget {
  const ModernPopupHoverTile({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
    this.borderRadius = 8,
  });

  final Widget child;
  final EdgeInsetsGeometry padding;
  final double borderRadius;

  @override
  State<ModernPopupHoverTile> createState() => _ModernPopupHoverTileState();
}

class _ModernPopupHoverTileState extends State<ModernPopupHoverTile> {
  bool _hovered = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return MouseRegion(
      onEnter: (_) => setState(() => _hovered = true),
      onExit: (_) => setState(() => _hovered = false),
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 120),
        padding: widget.padding,
        decoration: BoxDecoration(
          color: _hovered
              ? (c.isDark ? Colors.white.withValues(alpha: 0.08) : BrandColors.accentCyan.withValues(alpha: 0.12))
              : Colors.transparent,
          borderRadius: BorderRadius.circular(widget.borderRadius),
        ),
        child: widget.child,
      ),
    );
  }
}

