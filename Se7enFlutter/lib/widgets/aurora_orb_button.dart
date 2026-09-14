import 'dart:math' as math;
import 'package:flutter/material.dart' hide ConnectionState;

import '../core/models/connection_state.dart';

class AuroraOrbButton extends StatefulWidget {
  const AuroraOrbButton({
    super.key,
    required this.state,
    required this.onTap,
    this.percent = 0,
    this.size = 210,
  });

  final ConnectionState state;
  final VoidCallback onTap;
  final int percent;
  final double size;

  @override
  State<AuroraOrbButton> createState() => _AuroraOrbButtonState();
}

class _AuroraOrbButtonState extends State<AuroraOrbButton>
    with TickerProviderStateMixin {
  late final AnimationController _rotation;
  late final AnimationController _pulse;
  late final AnimationController _breathe;
  bool _hover = false;
  bool _pressed = false;

  @override
  void initState() {
    super.initState();
    _rotation = AnimationController(vsync: this, duration: const Duration(seconds: 14));
    _pulse = AnimationController(vsync: this, duration: const Duration(milliseconds: 2000));
    _breathe = AnimationController(vsync: this, duration: const Duration(milliseconds: 3600));
    _syncAnimationSpeed(widget.state);
  }

  void _syncAnimationSpeed(ConnectionState state) {
    switch (state) {
      case ConnectionState.connected:
        _rotation.duration = const Duration(seconds: 14);
        if (!_rotation.isAnimating) _rotation.repeat();
        if (!_pulse.isAnimating) _pulse.repeat();
        if (!_breathe.isAnimating) _breathe.repeat(reverse: true);
        break;
      case ConnectionState.connecting || ConnectionState.disconnecting:
        _rotation.duration = const Duration(seconds: 4);
        if (!_rotation.isAnimating) _rotation.repeat();
        if (_pulse.isAnimating) _pulse.stop();
        _pulse.value = 0.0;
        if (!_breathe.isAnimating) _breathe.repeat(reverse: true);
        break;
      case ConnectionState.error || ConnectionState.disconnected:

        if (_rotation.isAnimating) _rotation.stop();
        _rotation.value = 0.0;
        if (_pulse.isAnimating) _pulse.stop();
        _pulse.value = 0.0;
        if (_breathe.isAnimating) _breathe.stop();
        _breathe.value = 0.0;
        break;
    }
  }

  @override
  void didUpdateWidget(covariant AuroraOrbButton oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.state != widget.state) {
      _syncAnimationSpeed(widget.state);
    }
  }

  @override
  void dispose() {
    _rotation.dispose();
    _pulse.dispose();
    _breathe.dispose();
    super.dispose();
  }

  List<Color> get _orbColors => switch (widget.state) {
        ConnectionState.connected => const [
            Color(0xFF10B981),
            Color(0xFF059669),
            Color(0xFF34D399),
          ],
        ConnectionState.connecting || ConnectionState.disconnecting => const [
            Color(0xFFF59E0B),
            Color(0xFFD97706),
            Color(0xFFFBBF24),
          ],
        ConnectionState.error => const [
            Color(0xFFEF4444),
            Color(0xFFDC2626),
            Color(0xFFF87171),
          ],
        _ => const [
            Color(0xFF8B5CF6),
            Color(0xFF7C3AED),
            Color(0xFFA78BFA),
          ],
      };

  Color get _glowColor => switch (widget.state) {
        ConnectionState.connected => const Color(0xFF10B981),
        ConnectionState.connecting || ConnectionState.disconnecting => const Color(0xFFF59E0B),
        ConnectionState.error => const Color(0xFFEF4444),
        _ => const Color(0xFF8B5CF6),
      };

  @override
  Widget build(BuildContext context) {
    final colors = _orbColors;
    final glow = _glowColor;
    final isConnected = widget.state == ConnectionState.connected;
    final isConnecting = widget.state == ConnectionState.connecting ||
        widget.state == ConnectionState.disconnecting;

    return RepaintBoundary(
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          onTapDown: (_) => setState(() => _pressed = true),
          onTapUp: (_) => setState(() => _pressed = false),
          onTapCancel: () => setState(() => _pressed = false),
          onTap: widget.onTap,
          child: AnimatedScale(
            scale: _pressed ? 0.94 : (_hover ? 1.03 : 1.0),
            duration: const Duration(milliseconds: 160),
            curve: Curves.easeOutBack,
            child: SizedBox(
              width: widget.size,
              height: widget.size,
              child: AnimatedBuilder(
                animation: Listenable.merge([_rotation, _pulse, _breathe]),
                child: _CentralGlassOrb(
                  size: widget.size * 0.60,
                  colors: colors,
                  glowColor: glow,
                  state: widget.state,
                  hover: _hover,
                  breathe: 0.0,
                ),
                builder: (context, glassOrb) {
                  return Stack(
                    alignment: Alignment.center,
                    children: [

                      Opacity(
                        opacity: isConnected
                            ? (0.75 + _breathe.value * 0.25)
                            : (isConnecting ? 0.90 : (_hover ? 0.70 : 0.45)),
                        child: Container(
                          width: widget.size * 0.94,
                          height: widget.size * 0.94,
                          decoration: BoxDecoration(
                            shape: BoxShape.circle,
                            gradient: RadialGradient(
                              colors: [
                                glow.withValues(alpha: 0.42),
                                glow.withValues(alpha: 0.0),
                              ],
                              stops: const [0.42, 1.0],
                            ),
                          ),
                        ),
                      ),

                      CustomPaint(
                        size: Size(widget.size, widget.size),
                        painter: _OrbRingsPainter(
                          colors: colors,
                          glowColor: glow,
                          rotation: _rotation.value,
                          pulse: _pulse.value,
                          breathe: _breathe.value,
                          isConnected: isConnected,
                          isConnecting: isConnecting,
                          percent: widget.percent,
                        ),
                      ),

                      glassOrb!,
                    ],
                  );
                },
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _CentralGlassOrb extends StatelessWidget {
  const _CentralGlassOrb({
    required this.size,
    required this.colors,
    required this.glowColor,
    required this.state,
    required this.hover,
    required this.breathe,
  });

  final double size;
  final List<Color> colors;
  final Color glowColor;
  final ConnectionState state;
  final bool hover;
  final double breathe;

  @override
  Widget build(BuildContext context) {
    final isConnected = state == ConnectionState.connected;
    final isConnecting = state == ConnectionState.connecting;
    final isDisconnecting = state == ConnectionState.disconnecting;
    final isError = state == ConnectionState.error;

    return Container(
      width: size,
      height: size,
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        gradient: RadialGradient(
          center: const Alignment(-0.25, -0.35),
          radius: 0.95,
          colors: [
            Colors.white.withValues(alpha: 0.28),
            colors.first.withValues(alpha: 0.85),
            colors.last.withValues(alpha: 0.95),
            Colors.black.withValues(alpha: 0.65),
          ],
          stops: const [0.0, 0.35, 0.75, 1.0],
        ),
        border: Border.all(
          color: Colors.white.withValues(alpha: hover ? 0.65 : 0.35),
          width: 1.5,
        ),
        boxShadow: [
          BoxShadow(
            color: glowColor.withValues(alpha: hover ? 0.6 : 0.35),
            blurRadius: 28,
            spreadRadius: -2,
          ),
          BoxShadow(
            color: Colors.black.withValues(alpha: 0.5),
            blurRadius: 18,
            offset: const Offset(0, 8),
          ),
        ],
      ),
      child: Stack(
        alignment: Alignment.center,
        children: [

          Positioned(
            top: size * 0.08,
            child: Container(
              width: size * 0.52,
              height: size * 0.22,
              decoration: BoxDecoration(
                borderRadius: BorderRadius.all(Radius.elliptical(size * 0.26, size * 0.11)),
                gradient: LinearGradient(
                  begin: Alignment.topCenter,
                  end: Alignment.bottomCenter,
                  colors: [
                    Colors.white.withValues(alpha: 0.45),
                    Colors.white.withValues(alpha: 0.0),
                  ],
                ),
              ),
            ),
          ),

          Column(
            mainAxisAlignment: MainAxisAlignment.center,
            children: [
              Icon(
                isConnected
                    ? Icons.shield_rounded
                    : (isConnecting
                        ? Icons.sync_rounded
                        : (isError ? Icons.warning_rounded : Icons.power_settings_new_rounded)),
                size: size * 0.36,
                color: Colors.white,
                shadows: [
                  Shadow(
                    color: glowColor.withValues(alpha: 0.8),
                    blurRadius: 12,
                  ),
                ],
              ),
              const SizedBox(height: 4),
              Text(
                isConnected
                    ? 'CONNECTED'
                    : (isConnecting
                        ? 'CONNECTING'
                        : (isDisconnecting
                            ? 'STOPPING'
                            : (isError ? 'RETRY' : 'CONNECT'))),
                style: const TextStyle(
                  color: Colors.white,
                  fontSize: 11,
                  fontWeight: FontWeight.w800,
                  letterSpacing: 1.4,
                  shadows: [
                    Shadow(
                      color: Colors.black54,
                      blurRadius: 4,
                      offset: Offset(0, 1),
                    ),
                  ],
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}

class _OrbRingsPainter extends CustomPainter {
  _OrbRingsPainter({
    required this.colors,
    required this.glowColor,
    required this.rotation,
    required this.pulse,
    required this.breathe,
    required this.isConnected,
    required this.isConnecting,
    required this.percent,
  });

  final List<Color> colors;
  final Color glowColor;
  final double rotation;
  final double pulse;
  final double breathe;
  final bool isConnected;
  final bool isConnecting;
  final int percent;

  @override
  void paint(Canvas canvas, Size size) {
    final center = size.center(Offset.zero);
    final maxRadius = size.width / 2;

    final haloPaint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.0
      ..color = glowColor.withValues(alpha: 0.08 + breathe * 0.08);

    canvas.drawCircle(center, maxRadius - 2, haloPaint);
    canvas.drawCircle(center, maxRadius - 14, haloPaint);

    if (isConnected) {
      final pulseRadius = (maxRadius * 0.65) + (pulse * (maxRadius * 0.35));
      final pulsePaint = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.8 * (1.0 - pulse)
        ..color = glowColor.withValues(alpha: (1.0 - pulse) * 0.65);

      canvas.drawCircle(center, pulseRadius, pulsePaint);
    }

    final angle = rotation * 2 * math.pi;
    final arcRadius = maxRadius - 8;
    final rect = Rect.fromCircle(center: center, radius: arcRadius);

    if (isConnecting) {

      final fastAngle = rotation * 6 * math.pi;
      final sweepPaint = Paint()
        ..style = PaintingStyle.stroke
        ..strokeCap = StrokeCap.round
        ..strokeWidth = 3.0
        ..shader = SweepGradient(
          colors: [
            Colors.transparent,
            glowColor,
            Colors.white,
          ],
          transform: GradientRotation(fastAngle),
        ).createShader(rect);

      canvas.drawArc(rect, fastAngle, math.pi * 0.8, false, sweepPaint);
      canvas.drawArc(rect, fastAngle + math.pi, math.pi * 0.8, false, sweepPaint);

      if (percent > 0) {
        final progPaint = Paint()
          ..style = PaintingStyle.stroke
          ..strokeCap = StrokeCap.round
          ..strokeWidth = 2.5
          ..color = Colors.white.withValues(alpha: 0.85);

        canvas.drawArc(
          Rect.fromCircle(center: center, radius: arcRadius - 6),
          -math.pi / 2,
          2 * math.pi * (percent / 100),
          false,
          progPaint,
        );
      }
    } else {

      final segmentCount = 6;
      final segmentSweep = (2 * math.pi) / segmentCount;
      final notchPaint = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 2.0
        ..strokeCap = StrokeCap.round
        ..color = glowColor.withValues(alpha: isConnected ? 0.7 : 0.3);

      for (var i = 0; i < segmentCount; i++) {
        final startAngle = angle + (i * segmentSweep);
        canvas.drawArc(rect, startAngle, segmentSweep * 0.45, false, notchPaint);
      }
    }
  }

  @override
  bool shouldRepaint(covariant _OrbRingsPainter old) {
    return rotation != old.rotation ||
        pulse != old.pulse ||
        breathe != old.breathe ||
        isConnected != old.isConnected ||
        isConnecting != old.isConnecting ||
        percent != old.percent ||
        glowColor != old.glowColor;
  }
}
