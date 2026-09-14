
enum ConnectionState {
  disconnected,
  connecting,
  connected,
  disconnecting,
  error;
}

extension ConnectionStateX on ConnectionState {
  String get token => switch (this) {
        ConnectionState.disconnected => 'disconnected',
        ConnectionState.connecting => 'connecting',
        ConnectionState.connected => 'connected',
        ConnectionState.disconnecting => 'disconnecting',
        ConnectionState.error => 'error',
      };

  static ConnectionState parse(String? token) => switch (token?.trim().toLowerCase()) {
        'connecting' => ConnectionState.connecting,
        'connected' => ConnectionState.connected,
        'disconnecting' => ConnectionState.disconnecting,
        'error' => ConnectionState.error,
        _ => ConnectionState.disconnected,
      };

  bool get isBusy =>
      this == ConnectionState.connecting || this == ConnectionState.disconnecting;

  bool get isConnected => this == ConnectionState.connected;
}
