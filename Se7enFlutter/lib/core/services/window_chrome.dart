import 'package:flutter/material.dart';
import 'package:window_manager/window_manager.dart';

import '../../theme/app_colors.dart';

class WindowChrome extends StatefulWidget {
  const WindowChrome({super.key, required this.child});

  final Widget child;

  static const double shadowMargin = 12;

  static const double radius = 12;

  @override
  State<WindowChrome> createState() => _WindowChromeState();
}

class _WindowChromeState extends State<WindowChrome> with WindowListener {
  bool _edgeToEdge = false;

  @override
  void initState() {
    super.initState();
    windowManager.addListener(this);
    _refresh();
  }

  @override
  void dispose() {
    windowManager.removeListener(this);
    super.dispose();
  }

  Future<void> _refresh() async {
    bool edgeToEdge;
    try {
      final results = await Future.wait([
        windowManager.isMaximized(),
        windowManager.isFullScreen(),
      ]);
      edgeToEdge = results[0] || results[1];
    } catch (_) {
      return;
    }
    if (mounted && edgeToEdge != _edgeToEdge) {
      setState(() => _edgeToEdge = edgeToEdge);
    }
  }

  @override
  void onWindowMaximize() => _refresh();
  @override
  void onWindowUnmaximize() => _refresh();
  @override
  void onWindowRestore() => _refresh();
  @override
  void onWindowEnterFullScreen() => _refresh();
  @override
  void onWindowLeaveFullScreen() => _refresh();

  @override
  void onWindowResized() => _refresh();

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isDark = Theme.of(context).brightness == Brightness.dark;

    if (_edgeToEdge) {
      return ColoredBox(color: c.appBg, child: widget.child);
    }

    final r = BorderRadius.circular(WindowChrome.radius);
    return Padding(
      padding: const EdgeInsets.all(WindowChrome.shadowMargin),
      child: DecoratedBox(
        decoration: BoxDecoration(
          borderRadius: r,
          boxShadow: [

            BoxShadow(
              color: Colors.black.withValues(alpha: isDark ? 0.55 : 0.22),
              blurRadius: 6,
              offset: const Offset(0, 2),
            ),
            BoxShadow(
              color: Colors.black.withValues(alpha: isDark ? 0.45 : 0.16),
              blurRadius: WindowChrome.shadowMargin,
              spreadRadius: -2,
              offset: const Offset(0, 4),
            ),
          ],
        ),
        child: ClipRRect(
          borderRadius: r,
          clipBehavior: Clip.antiAlias,
          child: DecoratedBox(

            position: DecorationPosition.foreground,
            decoration: BoxDecoration(
              borderRadius: r,
              border: Border.all(
                color: isDark
                    ? Colors.white.withValues(alpha: 0.10)
                    : Colors.black.withValues(alpha: 0.12),
                width: 1,
              ),
            ),
            child: ColoredBox(color: c.appBg, child: widget.child),
          ),
        ),
      ),
    );
  }
}
