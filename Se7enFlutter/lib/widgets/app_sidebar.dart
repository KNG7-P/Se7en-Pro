import 'package:flutter/material.dart';
import 'package:url_launcher/url_launcher.dart';

import '../theme/app_colors.dart';

class NavDestination {
  const NavDestination(this.icon, this.activeIcon, this.label);
  final IconData icon;
  final IconData activeIcon;
  final String label;
}

class AppSidebar extends StatelessWidget {
  const AppSidebar({
    super.key,
    required this.destinations,
    required this.selectedIndex,
    required this.onSelect,
  });

  final List<NavDestination> destinations;
  final int selectedIndex;
  final ValueChanged<int> onSelect;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;

    return Container(
      width: 210,
      decoration: BoxDecoration(
        color: c.sidebarBg.withValues(alpha: 0.8),
        border: Border(right: BorderSide(color: c.border.withValues(alpha: 0.6))),
      ),
      child: Column(
        children: [
          const SizedBox(height: 14),
          for (var i = 0; i < destinations.length; i++)
            _ModernNavItem(
              dest: destinations[i],
              selected: i == selectedIndex,
              onTap: () => onSelect(i),
            ),
          const Spacer(),

          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 8),
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: Material(
                color: Colors.transparent,
                child: InkWell(
                  borderRadius: BorderRadius.circular(12),
                  onTap: () => launchUrl(Uri.parse('https://t.me/King_network7')),
                  child: Container(
                    padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
                    decoration: BoxDecoration(
                      gradient: const LinearGradient(
                        colors: [Color(0xFF229ED9), Color(0xFF167BB0)],
                      ),
                      borderRadius: BorderRadius.circular(12),
                      boxShadow: [
                        BoxShadow(
                          color: BrandColors.telegram.withValues(alpha: 0.3),
                          blurRadius: 10,
                          offset: const Offset(0, 3),
                        ),
                      ],
                    ),
                    child: const Row(
                      children: [
                        Icon(Icons.send_rounded, size: 16, color: Colors.white),
                        SizedBox(width: 8),
                        Expanded(
                          child: Text(
                            '@King_network7',
                            style: TextStyle(
                              fontSize: 11.5,
                              fontWeight: FontWeight.w700,
                              color: Colors.white,
                            ),
                            overflow: TextOverflow.ellipsis,
                          ),
                        ),
                        Icon(Icons.arrow_outward_rounded, size: 13, color: Colors.white70),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
          Padding(
            padding: const EdgeInsets.only(bottom: 12, top: 4),
            child: Text(
              'Se7en Pro • v1.0.4',
              style: TextStyle(fontSize: 10, fontWeight: FontWeight.w600, color: c.textMuted),
            ),
          ),
        ],
      ),
    );
  }
}

class _ModernNavItem extends StatefulWidget {
  const _ModernNavItem({
    required this.dest,
    required this.selected,
    required this.onTap,
  });

  final NavDestination dest;
  final bool selected;
  final VoidCallback onTap;

  @override
  State<_ModernNavItem> createState() => _ModernNavItemState();
}

class _ModernNavItemState extends State<_ModernNavItem> {
  bool _hovered = false;
  bool _pressed = false;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    final isDark = Theme.of(context).brightness == Brightness.dark;
    final isSelected = widget.selected;

    final Color itemBg;
    final Color itemBorder;
    final Color iconColor;
    final Color textColor;
    final FontWeight textWeight;

    final skyBlue = BrandColors.accentCyan;

    if (isSelected) {
      itemBg = isDark
          ? skyBlue.withValues(alpha: 0.20)
          : skyBlue;
      itemBorder = isDark
          ? skyBlue.withValues(alpha: 0.55)
          : skyBlue;
      iconColor = isDark ? skyBlue : Colors.white;
      textColor = Colors.white;
      textWeight = FontWeight.w700;
    } else if (_hovered) {
      itemBg = isDark
          ? Colors.white.withValues(alpha: 0.07)
          : skyBlue.withValues(alpha: 0.08);
      itemBorder = isDark
          ? c.border.withValues(alpha: 0.40)
          : skyBlue.withValues(alpha: 0.30);
      iconColor = skyBlue;
      textColor = isDark ? Colors.white : c.textPrimary;
      textWeight = FontWeight.w600;
    } else {
      itemBg = Colors.transparent;
      itemBorder = Colors.transparent;
      iconColor = c.textSecondary;
      textColor = c.textSecondary;
      textWeight = FontWeight.w500;
    }

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 3),
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hovered = true),
        onExit: (_) => setState(() {
          _hovered = false;
          _pressed = false;
        }),
        child: GestureDetector(
          onTapDown: (_) => setState(() => _pressed = true),
          onTapUp: (_) => setState(() => _pressed = false),
          onTapCancel: () => setState(() => _pressed = false),
          onTap: widget.onTap,
          child: AnimatedScale(
            scale: _pressed ? 0.98 : 1.0,
            duration: const Duration(milliseconds: 100),
            curve: Curves.easeInOut,
            child: AnimatedContainer(
              duration: const Duration(milliseconds: 160),
              height: 44,
              padding: const EdgeInsets.symmetric(horizontal: 12),
              decoration: BoxDecoration(
                color: itemBg,
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: itemBorder),
                boxShadow: isSelected
                    ? [
                        BoxShadow(
                          color: BrandColors.accentCyan.withValues(alpha: isDark ? 0.12 : 0.25),
                          blurRadius: 10,
                          offset: const Offset(0, 2),
                        ),
                      ]
                    : null,
              ),
              child: Row(
                children: [
                  Icon(
                    isSelected ? widget.dest.activeIcon : widget.dest.icon,
                    size: 18,
                    color: iconColor,
                  ),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      widget.dest.label,
                      style: TextStyle(
                        fontSize: 13,
                        fontWeight: textWeight,
                        color: textColor,
                        letterSpacing: -0.1,
                      ),
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
