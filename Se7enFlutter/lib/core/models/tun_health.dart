import 'tunnel_status.dart';

class TunHealth {
  const TunHealth._({
    required this.wanted,
    required this.active,
    required this.failed,
    required this.busy,
    required this.detail,
  });

  final bool wanted;

  final bool active;

  final bool failed;

  final bool busy;

  final String detail;

  factory TunHealth.of(
    TunnelStatus? status,
    bool systemWideTunneling,
    String idleDetail,
  ) {
    final wanted = systemWideTunneling && (status?.isAdmin ?? false);
    final state = status?.tunStatusText ?? 'Off';
    final failed = state == 'Error';
    final busy = state == 'Starting' || state == 'Stopping';
    final active = status?.tunActive ?? false;
    final error = status?.tunLastError ?? '';

    final String detail;
    if (!wanted) {
      detail = idleDetail;
    } else if (failed) {
      detail = error.isEmpty ? 'The TUN adapter failed to start' : error;
    } else if (active) {
      detail = 'Wintun adapter up — carrying all system traffic';
    } else if (state == 'Starting') {
      detail = 'Creating the Wintun adapter…';
    } else if (state == 'Stopping') {
      detail = 'Tearing the Wintun adapter down…';
    } else {
      detail = 'Waiting for the tunnel to connect';
    }

    return TunHealth._(
      wanted: wanted,
      active: active,
      failed: failed,
      busy: busy,
      detail: detail,
    );
  }

  String? get errorTooltip => failed && detail.isNotEmpty ? detail : null;
}
