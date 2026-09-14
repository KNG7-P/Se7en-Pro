import 'package:flutter/material.dart' hide ConnectionState;

import '../core/models/connection_state.dart';
import '../theme/app_colors.dart';
import '../theme/app_theme.dart';

class StatusBadge extends StatelessWidget {
  const StatusBadge({super.key, required this.state, required this.text});
  final ConnectionState state;
  final String text;

  Color get _dot => switch (state) {
        ConnectionState.connected => BrandColors.success,
        ConnectionState.connecting => BrandColors.accentCyan,
        ConnectionState.disconnecting => BrandColors.warning,
        ConnectionState.error => BrandColors.danger,
        ConnectionState.disconnected => const Color(0xFF6B6B78),
      };

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final tint = _dot;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
      decoration: BoxDecoration(
        color: tint.withValues(alpha: 0.12),
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: tint.withValues(alpha: 0.4)),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          _PulsingDot(color: tint, animate: state.isBusy),
          const SizedBox(width: 7),
          Text(
            text,
            style: TextStyle(
              fontSize: 12,
              fontWeight: FontWeight.w600,
              color: c.textPrimary,
            ),
          ),
        ],
      ),
    );
  }
}

class _PulsingDot extends StatefulWidget {
  const _PulsingDot({required this.color, required this.animate});
  final Color color;
  final bool animate;

  @override
  State<_PulsingDot> createState() => _PulsingDotState();
}

class _PulsingDotState extends State<_PulsingDot>
    with SingleTickerProviderStateMixin {
  late final AnimationController _ctrl = AnimationController(
    vsync: this,
    duration: const Duration(milliseconds: 900),
  );

  @override
  void initState() {
    super.initState();
    if (widget.animate) _ctrl.repeat(reverse: true);
  }

  @override
  void didUpdateWidget(_PulsingDot old) {
    super.didUpdateWidget(old);
    if (widget.animate && !_ctrl.isAnimating) {
      _ctrl.repeat(reverse: true);
    } else if (!widget.animate && _ctrl.isAnimating) {
      _ctrl.stop();
      _ctrl.value = 1;
    }
  }

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AnimatedBuilder(
      animation: _ctrl,
      builder: (context, _) {
        final o = widget.animate ? 0.4 + _ctrl.value * 0.6 : 1.0;
        return Container(
          width: 7,
          height: 7,
          decoration: BoxDecoration(
            shape: BoxShape.circle,
            color: widget.color.withValues(alpha: o),
            boxShadow: [
              BoxShadow(
                color: widget.color.withValues(alpha: o * 0.6),
                blurRadius: 6,
              ),
            ],
          ),
        );
      },
    );
  }
}

class MonoPill extends StatelessWidget {
  const MonoPill({super.key, required this.text, this.onCopy});
  final String text;
  final VoidCallback? onCopy;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Container(
      height: 34,
      padding: const EdgeInsets.symmetric(horizontal: 10),
      decoration: BoxDecoration(
        color: c.input,
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: c.border),
      ),
      alignment: Alignment.centerLeft,
      child: Text(
        text,
        overflow: TextOverflow.ellipsis,
        style: AppTheme.mono(c.textPrimary, size: 12),
      ),
    );
  }
}
