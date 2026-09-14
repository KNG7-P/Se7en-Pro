import 'package:flutter/material.dart';
import 'package:window_manager/window_manager.dart';

import '../theme/app_colors.dart';
import 'close_confirm_dialog.dart';

class AppTitleBar extends StatelessWidget {
  const AppTitleBar({super.key});

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Container(
      height: 42,
      decoration: BoxDecoration(
        color: c.titleBarBg.withValues(alpha: 0.9),
        border: Border(bottom: BorderSide(color: c.border.withValues(alpha: 0.2))),
      ),
      child: Row(
        children: [
          const SizedBox(width: 16),

          Container(
            width: 26,
            height: 26,
            decoration: BoxDecoration(
              gradient: const LinearGradient(colors: BrandColors.accentGradient),
              borderRadius: BorderRadius.circular(8),
            ),
            child: const Icon(Icons.shield_rounded, size: 16, color: Colors.white),
          ),
          const SizedBox(width: 10),
          Text(
            'Se7en Pro',
            style: TextStyle(
              fontSize: 13.5,
              fontWeight: FontWeight.w800,
              letterSpacing: -0.2,
              color: c.textPrimary,
            ),
          ),

          const Expanded(
            child: DragToMoveArea(
              child: SizedBox.expand(),
            ),
          ),

          const _ModernWindowButtons(),
        ],
      ),
    );
  }
}

class _ModernWindowButtons extends StatelessWidget {
  const _ModernWindowButtons();

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Row(
      children: [
        _WinControlBtn(
          icon: Icons.remove_rounded,
          hoverColor: c.hoverBg,
          iconColor: c.textSecondary,
          onTap: () => windowManager.minimize(),
        ),
        _WinControlBtn(
          icon: Icons.crop_square_rounded,
          hoverColor: c.hoverBg,
          iconColor: c.textSecondary,
          iconSize: 15,
          onTap: () async {
            if (await windowManager.isMaximized()) {
              await windowManager.unmaximize();
            } else {
              await windowManager.maximize();
            }
          },
        ),
        _WinControlBtn(
          icon: Icons.close_rounded,
          hoverColor: BrandColors.danger,
          hoverIconColor: Colors.white,
          iconColor: c.textSecondary,
          iconSize: 18,
          onTap: () => CloseConfirmDialog.show(context),
        ),
      ],
    );
  }
}

class _WinControlBtn extends StatefulWidget {
  const _WinControlBtn({
    required this.icon,
    required this.onTap,
    required this.hoverColor,
    required this.iconColor,
    this.hoverIconColor,
    this.iconSize = 16,
  });

  final IconData icon;
  final VoidCallback onTap;
  final Color hoverColor;
  final Color iconColor;
  final Color? hoverIconColor;
  final double iconSize;

  @override
  State<_WinControlBtn> createState() => _WinControlBtnState();
}

class _WinControlBtnState extends State<_WinControlBtn> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 100),
          width: 44,
          height: 42,
          color: _hover ? widget.hoverColor : Colors.transparent,
          child: Icon(
            widget.icon,
            size: widget.iconSize,
            color: _hover
                ? (widget.hoverIconColor ?? widget.iconColor)
                : widget.iconColor,
          ),
        ),
      ),
    );
  }
}
