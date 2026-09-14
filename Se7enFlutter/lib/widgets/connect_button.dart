import 'dart:math' as math;

import 'package:flutter/material.dart' hide ConnectionState;

import '../../core/models/connection_state.dart';
import '../../theme/app_colors.dart';

class ConnectButton extends StatefulWidget {
  const ConnectButton({
    super.key,
    required this.state,
    required this.onTap,
    this.percent = 0,
    this.size = 200,
  });

  final ConnectionState state;
  final VoidCallback onTap;
  final int percent;
  final double size;

  @override
  State<ConnectButton> createState() => _ConnectButtonState();
}

class _ConnectButtonState extends State<ConnectButton>
    with TickerProviderStateMixin {
  late final AnimationController _spin;
  late final AnimationController _pulse;
  late final AnimationController _breathe;
  bool _hover = false;
  bool _down = false;

  @override
  void initState() {
    super.initState();
    _spin = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 4),
    );
    _pulse = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 2200),
    );
    _breathe = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 3200),
    );
    _syncState(widget.state);
  }

  void _syncState(ConnectionState state) {
    if (state == ConnectionState.connected) {
      if (!_spin.isAnimating) _spin.repeat();
      if (!_pulse.isAnimating) _pulse.repeat();
      if (!_breathe.isAnimating) _breathe.repeat(reverse: true);
    } else if (state == ConnectionState.connecting ||
        state == ConnectionState.disconnecting) {
      if (!_spin.isAnimating) _spin.repeat();
      if (_pulse.isAnimating) _pulse.stop();
      if (!_breathe.isAnimating) _breathe.repeat(reverse: true);
    } else {
      if (_spin.isAnimating) _spin.stop();
      if (_pulse.isAnimating) _pulse.stop();
      if (_breathe.isAnimating) _breathe.stop();
      _spin.value = 0;
      _pulse.value = 0;
      _breathe.value = 0;
    }
  }

  @override
  void didUpdateWidget(covariant ConnectButton oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.state != widget.state) {
      _syncState(widget.state);
    }
  }

  @override
  void dispose() {
    _spin.dispose();
    _pulse.dispose();
    _breathe.dispose();
    super.dispose();
  }

  List<Color> get _stateColors => switch (widget.state) {
        ConnectionState.connected => BrandColors.connectedGradient,
        ConnectionState.connecting => const [
            BrandColors.accentCyan,
            BrandColors.primary,
          ],
        ConnectionState.error => const [
            Color(0xFFF97066),
            BrandColors.danger,
          ],
        _ => BrandColors.accentGradient,
      };

  IconData get _icon => switch (widget.state) {
        ConnectionState.connected => Icons.power_settings_new_rounded,
        ConnectionState.error => Icons.priority_high_rounded,
        _ => Icons.power_settings_new_rounded,
      };

  String get _label => switch (widget.state) {
        ConnectionState.connected => 'Connected',
        ConnectionState.connecting => 'Connecting…',
        ConnectionState.disconnecting => 'Stopping…',
        ConnectionState.error => 'Retry',
        ConnectionState.disconnected => 'Connect',
      };

  @override
  Widget build(BuildContext context) {
    final colors = _stateColors;
    final connecting = widget.state == ConnectionState.connecting ||
        widget.state == ConnectionState.disconnecting;
    final connected = widget.state == ConnectionState.connected;

    return MouseRegion(
      cursor: SystemMouseCursors.click,
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTapDown: (_) => setState(() => _down = true),
        onTapUp: (_) => setState(() => _down = false),
        onTapCancel: () => setState(() => _down = false),
        onTap: widget.onTap,
        child: AnimatedScale(
          scale: _down ? 0.96 : (_hover ? 1.02 : 1.0),
          duration: const Duration(milliseconds: 140),
          curve: Curves.easeOut,
          child: SizedBox(
            width: widget.size,
            height: widget.size,
            child: AnimatedBuilder(
              animation: Listenable.merge([_spin, _pulse, _breathe]),
              builder: (context, _) {
                return CustomPaint(
                  painter: _RingPainter(
                    colors: colors,
                    spin: _spin.value,
                    pulse: _pulse.value,
                    breathe: _breathe.value,
                    connecting: connecting,
                    connected: connected,
                    percent: widget.percent,
                  ),
                  child: Center(
                    child: _Core(
                      size: widget.size * 0.62,
                      colors: colors,
                      icon: _icon,
                      label: _label,
                      hover: _hover,
                    ),
                  ),
                );
              },
            ),
          ),
        ),
      ),
    );
  }
}

class _Core extends StatelessWidget {
  const _Core({
    required this.size,
    required this.colors,
    required this.icon,
    required this.label,
    required this.hover,
  });

  final double size;
  final List<Color> colors;
  final IconData icon;
  final String label;
  final bool hover;

  @override
  Widget build(BuildContext context) {
    return AnimatedContainer(
      duration: const Duration(milliseconds: 260),
      width: size,
      height: size,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        gradient: LinearGradient(
          colors: colors,
          begin: Alignment.topLeft,
          end: Alignment.bottomRight,
        ),
        boxShadow: [
          BoxShadow(
            color: colors.last.withValues(alpha: hover ? 0.55 : 0.40),
            blurRadius: hover ? 42 : 30,
            spreadRadius: hover ? 2 : 0,
          ),
        ],
      ),
      child: Column(
        mainAxisAlignment: MainAxisAlignment.center,
        children: [
          Icon(icon, color: Colors.white, size: size * 0.30),
          const SizedBox(height: 6),
          Text(
            label,
            style: const TextStyle(
              color: Colors.white,
              fontSize: 14,
              fontWeight: FontWeight.w600,
              letterSpacing: 0.2,
            ),
          ),
        ],
      ),
    );
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter({
    required this.colors,
    required this.spin,
    required this.pulse,
    required this.breathe,
    required this.connecting,
    required this.connected,
    required this.percent,
  });

  final List<Color> colors;
  final double spin;
  final double pulse;
  final double breathe;
  final bool connecting;
  final bool connected;
  final int percent;

  @override
  void paint(Canvas canvas, Size size) {
    final center = size.center(Offset.zero);
    final r = size.width / 2;

    final glowOpacity = 0.10 + breathe * 0.10;
    canvas.drawCircle(
      center,
      r - 2,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.2
        ..color = colors.last.withValues(alpha: glowOpacity),
    );

    if (connected) {
      final rippleR = r * (0.62 + pulse * 0.38);
      canvas.drawCircle(
        center,
        rippleR,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..color = colors.last.withValues(alpha: (1 - pulse) * 0.5),
      );
    }

    if (connecting) {
      final sweep = math.pi * 1.1;
      final ringR = r - 10;
      final rect = Rect.fromCircle(center: center, radius: ringR);

      final grad = SweepGradient(
        colors: [
          colors.first.withValues(alpha: 0.0),
          colors.first,
          colors.last,
          colors.last.withValues(alpha: 0.0),
        ],
        stops: const [0.0, 0.35, 0.65, 1.0],
        transform: GradientRotation(spin * 2 * math.pi),
      );

      canvas.drawArc(
        rect,
        spin * 2 * math.pi,
        sweep,
        false,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 3.5
          ..strokeCap = StrokeCap.round
          ..shader = grad.createShader(rect),
      );

      final innerRect = Rect.fromCircle(center: center, radius: ringR - 8);
      canvas.drawArc(
        innerRect,
        -spin * 2 * math.pi,
        math.pi * 0.5,
        false,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..strokeCap = StrokeCap.round
          ..color = colors.first.withValues(alpha: 0.6),
      );

      if (percent > 0) {
        canvas.drawArc(
          Rect.fromCircle(center: center, radius: ringR),
          -math.pi / 2,
          2 * math.pi * (percent / 100.0),
          false,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = 3.5
            ..strokeCap = StrokeCap.round
            ..color = Colors.white.withValues(alpha: 0.85),
        );
      }
    }
  }

  @override
  bool shouldRepaint(covariant _RingPainter old) =>
      old.spin != spin ||
      old.pulse != pulse ||
      old.breathe != breathe ||
      old.connecting != connecting ||
      old.connected != connected ||
      old.percent != percent ||
      old.colors != colors;
}
