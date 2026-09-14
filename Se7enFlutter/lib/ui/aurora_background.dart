import 'dart:math' as math;

import 'package:flutter/material.dart' hide ConnectionState;

import '../core/models/connection_state.dart';
import '../theme/app_colors.dart';

class AuroraBackground extends StatefulWidget {
  const AuroraBackground({super.key, required this.state, this.child});

  final ConnectionState state;
  final Widget? child;

  @override
  State<AuroraBackground> createState() => _AuroraBackgroundState();
}

class _AuroraBackgroundState extends State<AuroraBackground>
    with TickerProviderStateMixin {
  late final AnimationController _motion;
  late final AnimationController _fade;

  late List<Color> _from;
  late List<Color> _to;

  @override
  void initState() {
    super.initState();
    _motion = AnimationController(
      vsync: this,
      duration: const Duration(seconds: 45),
    );
    _fade = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 1100),
      value: 1,
    );
    _from = _paletteFor(widget.state);
    _to = _from;
  }

  @override
  void didUpdateWidget(AuroraBackground old) {
    super.didUpdateWidget(old);
    if (old.state != widget.state) {

      _from = _lerpList(_from, _to, _fade.value);
      _to = _paletteFor(widget.state);
      _fade.forward(from: 0);
    }
  }

  @override
  void dispose() {
    _motion.dispose();
    _fade.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return RepaintBoundary(
      child: AnimatedBuilder(
        animation: Listenable.merge([_motion, _fade]),
        builder: (context, child) {
          final colors = _lerpList(_from, _to, _fade.value);
          return CustomPaint(
            painter: _AuroraPainter(
              t: _motion.value,
              colors: colors,
              baseTop: c.appBg,
              baseBottom: c.appBgDeep,
              dark: c.isDark,
              boost: widget.state == ConnectionState.connected ? 1.18 : 1.0,
            ),
            isComplex: true,
            child: child,
          );
        },
        child: widget.child,
      ),
    );
  }

  static List<Color> _lerpList(List<Color> a, List<Color> b, double t) => [
        for (var i = 0; i < a.length; i++) Color.lerp(a[i], b[i], t)!,
      ];

  static List<Color> _paletteFor(ConnectionState s) => switch (s) {
        ConnectionState.connected => const [
            BrandColors.emerald,
            BrandColors.teal,
            BrandColors.aqua,
            BrandColors.violet,
            BrandColors.emerald,
          ],
        ConnectionState.connecting => const [
            BrandColors.aqua,
            BrandColors.violet,
            BrandColors.amber,
            BrandColors.indigo,
            BrandColors.aqua,
          ],
        ConnectionState.disconnecting => const [
            BrandColors.amber,
            BrandColors.violet,
            BrandColors.indigo,
            BrandColors.teal,
            BrandColors.indigo,
          ],
        ConnectionState.error => const [
            BrandColors.danger,
            BrandColors.pink,
            BrandColors.violet,
            BrandColors.danger,
            BrandColors.indigo,
          ],
        ConnectionState.disconnected => const [
            BrandColors.indigo,
            BrandColors.violet,
            BrandColors.primary,
            BrandColors.aqua,
            BrandColors.indigo,
          ],
      };
}

class _Blob {
  const _Blob(this.base, this.radius, this.fx, this.fy, this.phase, this.drift);
  final Offset base;
  final double radius;
  final double fx;
  final double fy;
  final double phase;
  final double drift;
}

const _blobs = <_Blob>[
  _Blob(Offset(0.16, 0.22), 0.62, 1.0, 0.7, 0.0, 0.07),
  _Blob(Offset(0.84, 0.16), 0.54, 0.8, 1.1, 1.5, 0.06),
  _Blob(Offset(0.52, 0.86), 0.66, 0.6, 0.9, 3.0, 0.08),
  _Blob(Offset(0.92, 0.78), 0.48, 1.2, 0.7, 2.0, 0.06),
  _Blob(Offset(0.08, 0.72), 0.52, 0.9, 1.0, 4.2, 0.07),
];

class _AuroraPainter extends CustomPainter {
  _AuroraPainter({
    required this.t,
    required this.colors,
    required this.baseTop,
    required this.baseBottom,
    required this.dark,
    required this.boost,
  });

  final double t;
  final List<Color> colors;
  final Color baseTop;
  final Color baseBottom;
  final bool dark;
  final double boost;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Offset.zero & size;

    canvas.drawRect(
      rect,
      Paint()
        ..shader = LinearGradient(
          begin: Alignment.topCenter,
          end: Alignment.bottomCenter,
          colors: [baseTop, baseBottom],
        ).createShader(rect),
    );

    final tau = t * 2 * math.pi;
    final long = size.longestSide;
    final blend = dark ? BlendMode.plus : BlendMode.srcOver;
    final baseAlpha = (dark ? 0.40 : 0.15) * boost;

    canvas.saveLayer(rect, Paint());
    for (var i = 0; i < _blobs.length; i++) {
      final b = _blobs[i];
      final cx = (b.base.dx + b.drift * math.sin(tau * b.fx + b.phase)) * size.width;
      final cy = (b.base.dy + b.drift * math.cos(tau * b.fy + b.phase)) * size.height;
      final center = Offset(cx, cy);
      final r = b.radius * long * (0.92 + 0.08 * math.sin(tau * 0.5 + b.phase));
      final color = colors[i % colors.length];
      canvas.drawCircle(
        center,
        r,
        Paint()
          ..blendMode = blend
          ..shader = RadialGradient(
            colors: [
              color.withValues(alpha: baseAlpha),
              color.withValues(alpha: baseAlpha * 0.4),
              color.withValues(alpha: 0.0),
            ],
            stops: const [0.0, 0.45, 1.0],
          ).createShader(Rect.fromCircle(center: center, radius: r)),
      );
    }
    canvas.restore();

    if (dark) {
      canvas.drawRect(
        rect,
        Paint()
          ..shader = RadialGradient(
            center: Alignment.center,
            radius: 1.1,
            colors: [
              Colors.transparent,
              Colors.black.withValues(alpha: 0.28),
            ],
            stops: const [0.62, 1.0],
          ).createShader(rect),
      );
    }
  }

  @override
  bool shouldRepaint(covariant _AuroraPainter old) =>
      old.t != t ||
      old.boost != boost ||
      old.dark != dark ||
      !_sameColors(old.colors, colors);

  static bool _sameColors(List<Color> a, List<Color> b) {
    if (a.length != b.length) return false;
    for (var i = 0; i < a.length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }
}
