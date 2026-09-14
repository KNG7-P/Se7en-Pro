import 'dart:ui';

import 'package:flutter/material.dart';

import '../theme/app_colors.dart';

class GlassPanel extends StatelessWidget {
  const GlassPanel({
    super.key,
    required this.child,
    this.padding = const EdgeInsets.all(16),
    this.radius = 22,
    this.blur = 24,
    this.strong = false,
    this.fill,
    this.borderColor,
    this.showBorder = true,
    this.sheen = true,
    this.glowColor,
    this.glowOpacity = 0.0,
    this.width,
    this.height,
  });

  final Widget child;
  final EdgeInsetsGeometry padding;
  final double radius;
  final double blur;

  final bool strong;
  final Color? fill;
  final Color? borderColor;
  final bool showBorder;
  final bool sheen;

  final Color? glowColor;
  final double glowOpacity;

  final double? width;
  final double? height;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final r = BorderRadius.circular(radius);
    final fillColor = fill ?? (strong ? c.glassStrong : c.glass);

    Widget panel = ClipRRect(
      borderRadius: r,
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: blur, sigmaY: blur),
        child: Container(
          width: width,
          height: height,
          padding: padding,
          decoration: BoxDecoration(
            color: fillColor,
            borderRadius: r,
            border: showBorder
                ? Border.all(color: borderColor ?? c.glassBorder, width: 1)
                : null,
            gradient: sheen
                ? LinearGradient(
                    begin: Alignment.topLeft,
                    end: Alignment.bottomRight,
                    colors: [
                      Colors.white.withValues(alpha: c.isDark ? 0.06 : 0.35),
                      Colors.white.withValues(alpha: 0.0),
                    ],
                    stops: const [0.0, 0.55],
                  )
                : null,
          ),
          child: child,
        ),
      ),
    );

    if (glowColor != null && glowOpacity > 0) {
      panel = DecoratedBox(
        decoration: BoxDecoration(
          borderRadius: r,
          boxShadow: [
            BoxShadow(
              color: glowColor!.withValues(alpha: glowOpacity),
              blurRadius: 48,
              spreadRadius: -6,
            ),
          ],
        ),
        child: panel,
      );
    }
    return panel;
  }
}

class GlassTappable extends StatefulWidget {
  const GlassTappable({
    super.key,
    required this.child,
    this.onTap,
    this.padding = const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
    this.radius = 16,
    this.blur = 18,
    this.strong = false,
    this.selected = false,
    this.accent,
  });

  final Widget child;
  final VoidCallback? onTap;
  final EdgeInsetsGeometry padding;
  final double radius;
  final double blur;
  final bool strong;
  final bool selected;
  final Color? accent;

  @override
  State<GlassTappable> createState() => _GlassTappableState();
}

class _GlassTappableState extends State<GlassTappable> {
  bool _hover = false;
  bool _down = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final accent = widget.accent ?? BrandColors.aqua;
    final border = widget.selected
        ? accent.withValues(alpha: 0.55)
        : (_hover ? c.borderHover : c.glassBorder);
    final fill = widget.selected
        ? accent.withValues(alpha: c.isDark ? 0.16 : 0.14)
        : (_hover
            ? (c.isDark ? c.glassStrong : c.glass)
            : (widget.strong ? c.glassStrong : c.glass));

    return MouseRegion(
      cursor: widget.onTap == null
          ? SystemMouseCursors.basic
          : SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTapDown: (_) => setState(() => _down = true),
        onTapUp: (_) => setState(() => _down = false),
        onTapCancel: () => setState(() => _down = false),
        onTap: widget.onTap,
        child: AnimatedScale(
          scale: _down ? 0.97 : 1.0,
          duration: const Duration(milliseconds: 120),
          child: GlassPanel(
            radius: widget.radius,
            blur: widget.blur,
            padding: widget.padding,
            fill: fill,
            borderColor: border,
            sheen: !widget.selected,
            glowColor: widget.selected ? accent : null,
            glowOpacity: widget.selected ? 0.22 : 0.0,
            child: widget.child,
          ),
        ),
      ),
    );
  }
}
