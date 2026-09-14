import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../core/i18n/app_strings.dart';
import '../theme/app_colors.dart';
import '../widgets/glass_controls.dart';

class AboutPage extends ConsumerWidget {
  const AboutPage({super.key});

  static const _version = '1.0.4';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final c = Theme.of(context).extension<AppColors>()!;
    final str = ref.watch(stringsProvider);
    return ListView(
      padding: const EdgeInsets.fromLTRB(24, 6, 24, 24),
      children: [

        GlassCard(
          borderRadius: 16,
          padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 28),
          child: Column(
            children: [
              Container(
                width: 76,
                height: 76,
                decoration: BoxDecoration(
                  gradient: const LinearGradient(
                    colors: BrandColors.accentGradient,
                    begin: Alignment.topLeft,
                    end: Alignment.bottomRight,
                  ),
                  borderRadius: BorderRadius.circular(22),
                  boxShadow: [
                    BoxShadow(
                      color: BrandColors.primary.withValues(alpha: 0.45),
                      blurRadius: 30,
                      offset: const Offset(0, 8),
                    ),
                  ],
                ),
                child: const Icon(Icons.shield_rounded, size: 42, color: Colors.white),
              ),
              const SizedBox(height: 16),
              Text(
                'Se7en Pro',
                style: TextStyle(
                  fontSize: 24,
                  fontWeight: FontWeight.w900,
                  letterSpacing: -0.3,
                  color: c.textPrimary,
                ),
              ),
              const SizedBox(height: 4),
              Container(
                padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
                decoration: BoxDecoration(
                  color: BrandColors.accentCyan.withValues(alpha: 0.15),
                  borderRadius: BorderRadius.circular(20),
                  border: Border.all(color: BrandColors.accentCyan.withValues(alpha: 0.3)),
                ),
                child: Text(
                  'v$_version • ${str.windowsEdition}',
                  style: const TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.w700,
                    color: BrandColors.accentCyan,
                  ),
                ),
              ),
              const SizedBox(height: 14),
              Text(
                str.aboutTagline,
                textAlign: TextAlign.center,
                style: TextStyle(fontSize: 12.5, height: 1.5, color: c.textSecondary),
              ),
            ],
          ),
        ),
        const SizedBox(height: 14),

        GlassCard(
          borderRadius: 16,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.code_rounded, size: 16, color: BrandColors.accentCyan),
                  const SizedBox(width: 8),
                  Text(
                    'Community & Source Code',
                    style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: c.textPrimary),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              _ModernLinkTile(
                icon: Icons.code_rounded,
                color: BrandColors.accentCyan,
                title: 'Se7en Pro Repository',
                subtitle: 'github.com/KNG7-P/Se7en-Pro',
                url: 'https://github.com/KNG7-P/Se7en-Pro',
              ),
              const SizedBox(height: 8),
              _ModernLinkTile(
                icon: Icons.send_rounded,
                color: BrandColors.telegram,
                title: str.telegramChannel,
                subtitle: 't.me/King_network7 · News & Updates',
                url: 'https://t.me/King_network7',
              ),
              const SizedBox(height: 8),
              _ModernLinkTile(
                icon: Icons.terminal_rounded,
                color: BrandColors.accentPurple,
                title: 'Psiphon Tunnel Core',
                subtitle: 'github.com/Psiphon-Labs/psiphon-tunnel-core',
                url: 'https://github.com/Psiphon-Labs/psiphon-tunnel-core',
              ),
              const SizedBox(height: 8),
              _ModernLinkTile(
                icon: Icons.language_rounded,
                color: BrandColors.emerald,
                title: 'Psiphon Official Website',
                subtitle: 'psiphon.ca',
                url: 'https://psiphon.ca/',
              ),
            ],
          ),
        ),
        const SizedBox(height: 14),

        GlassCard(
          borderRadius: 16,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.help_outline_rounded, size: 16, color: BrandColors.accentCyan),
                  const SizedBox(width: 8),
                  Text(
                    'FAQ & Privacy Policies',
                    style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: c.textPrimary),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              const _ModernLinkTile(
                icon: Icons.quiz_rounded,
                color: BrandColors.primary,
                title: 'Frequently Asked Questions',
                subtitle: 'Troubleshooting & usage FAQ',
                url: 'https://s3.amazonaws.com/psiphon/web/mjr4-p23r-puwl/faq.html',
              ),
              const SizedBox(height: 8),
              const _ModernLinkTile(
                icon: Icons.privacy_tip_rounded,
                color: BrandColors.warning,
                title: 'Privacy Policy & Security',
                subtitle: 'Information collected & terms',
                url: 'https://s3.amazonaws.com/psiphon/web/mjr4-p23r-puwl/privacy.html#information-collected',
              ),
            ],
          ),
        ),
        const SizedBox(height: 14),

        GlassCard(
          borderRadius: 16,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.memory_rounded, size: 16, color: BrandColors.accentCyan),
                  const SizedBox(width: 8),
                  Text(
                    str.aboutTechStack,
                    style: TextStyle(fontSize: 13, fontWeight: FontWeight.w700, color: c.textPrimary),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              const Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  _TechBadge('Psiphon Go Core'),
                  _TechBadge('Cloudflare WARP'),
                  _TechBadge('MASQUE'),
                  _TechBadge('WireGuard Protocol'),
                  _TechBadge('Tor Onion Routing'),
                  _TechBadge('SHARD Protocol / Xray'),
                  _TechBadge('Flutter Desktop Engine'),
                  _TechBadge('Riverpod State Architecture'),
                ],
              ),
            ],
          ),
        ),
      ],
    );
  }
}

class _ModernLinkTile extends StatelessWidget {
  const _ModernLinkTile({
    required this.icon,
    required this.color,
    required this.title,
    required this.subtitle,
    required this.url,
  });

  final IconData icon;
  final Color color;
  final String title;
  final String subtitle;
  final String url;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Material(
      color: Colors.transparent,
      child: InkWell(
        borderRadius: BorderRadius.circular(12),
        onTap: () => launchUrl(Uri.parse(url)),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 11),
          decoration: BoxDecoration(
            color: c.input.withValues(alpha: 0.5),
            borderRadius: BorderRadius.circular(12),
            border: Border.all(color: c.border.withValues(alpha: 0.5)),
          ),
          child: Row(
            children: [
              Container(
                width: 36,
                height: 36,
                decoration: BoxDecoration(
                  color: color.withValues(alpha: 0.16),
                  borderRadius: BorderRadius.circular(10),
                ),
                child: Icon(icon, size: 18, color: color),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      title,
                      style: TextStyle(fontSize: 13, fontWeight: FontWeight.w600, color: c.textPrimary),
                    ),
                    const SizedBox(height: 1),
                    Text(
                      subtitle,
                      style: TextStyle(fontSize: 11, color: c.textMuted),
                    ),
                  ],
                ),
              ),
              Icon(Icons.open_in_new_rounded, size: 16, color: c.textSecondary),
            ],
          ),
        ),
      ),
    );
  }
}

class _TechBadge extends StatelessWidget {
  const _TechBadge(this.text);
  final String text;

  @override
  Widget build(BuildContext context) {
    final c = Theme.of(context).extension<AppColors>()!;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      decoration: BoxDecoration(
        color: c.input.withValues(alpha: 0.7),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: c.border.withValues(alpha: 0.6)),
      ),
      child: Text(
        text,
        style: TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, color: c.textSecondary),
      ),
    );
  }
}
