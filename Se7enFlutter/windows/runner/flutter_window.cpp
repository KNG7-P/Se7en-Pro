#include "flutter_window.h"

#include <optional>
#include <windowsx.h>
#include <dwmapi.h>
#pragma comment(lib, "dwmapi.lib")

#include <flutter/standard_method_codec.h>

#include "flutter/generated_plugin_registrant.h"

namespace {

constexpr int kShadowMarginDip = 12;

constexpr int kResizeGripDip = 6;

#ifndef DWMWA_NCRENDERING_POLICY
#define DWMWA_NCRENDERING_POLICY 2
#endif
#ifndef DWMWA_WINDOW_CORNER_PREFERENCE
#define DWMWA_WINDOW_CORNER_PREFERENCE 33
#endif
constexpr DWORD kDwmNcRenderingDisabled = 1;
constexpr DWORD kDwmCornerDoNotRound = 1;

enum AccentState : int {
  kAccentDisabled = 0,
  kAccentEnableTransparentGradient = 2,
};
struct AccentPolicy {
  int state;
  int flags;
  int gradient_color;
  int animation_id;
};
struct WindowCompositionAttribData {
  int attribute;
  PVOID data;
  ULONG size;
};
constexpr int kWcaAccentPolicy = 19;
using SetWindowCompositionAttributeFn =
    BOOL(WINAPI*)(HWND, WindowCompositionAttribData*);

void EnableCompositionTransparency(HWND hwnd) {
  HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
  if (!user32) {
    return;
  }
  auto set_attr = reinterpret_cast<SetWindowCompositionAttributeFn>(
      ::GetProcAddress(user32, "SetWindowCompositionAttribute"));
  if (!set_attr) {
    return;
  }
  AccentPolicy policy = {kAccentEnableTransparentGradient, 2, 0, 0};
  WindowCompositionAttribData data = {kWcaAccentPolicy, &policy,
                                      sizeof(policy)};
  set_attr(hwnd, &data);
}

bool IsWindowMaximized(HWND hwnd) {
  WINDOWPLACEMENT wp = {sizeof(WINDOWPLACEMENT)};
  return ::GetWindowPlacement(hwnd, &wp) && wp.showCmd == SW_MAXIMIZE;
}

}

FlutterWindow::FlutterWindow(const flutter::DartProject& project)
    : project_(project) {}

FlutterWindow::~FlutterWindow() {}

bool FlutterWindow::OnCreate() {
  if (!Win32Window::OnCreate()) {
    return false;
  }

  HWND hwnd = GetHandle();
  if (hwnd) {

    DWORD nc_policy = kDwmNcRenderingDisabled;
    ::DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, &nc_policy,
                            sizeof(nc_policy));

    DWORD corner_pref = kDwmCornerDoNotRound;
    ::DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, &corner_pref,
                            sizeof(corner_pref));

    ::SetClassLongPtrW(hwnd, GCLP_HBRBACKGROUND,
                       reinterpret_cast<LONG_PTR>(::GetStockObject(BLACK_BRUSH)));

    EnableCompositionTransparency(hwnd);

    ::SetWindowPos(hwnd, nullptr, 0, 0, 0, 0,
                   SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
  }

  RECT frame = GetClientArea();

  flutter_controller_ = std::make_unique<flutter::FlutterViewController>(
      frame.right - frame.left, frame.bottom - frame.top, project_);

  if (!flutter_controller_->engine() || !flutter_controller_->view()) {
    return false;
  }
  RegisterPlugins(flutter_controller_->engine());
  SetChildContent(flutter_controller_->view()->GetNativeWindow());

  flutter_controller_->engine()->SetNextFrameCallback([&]() {
    this->Show();
  });

  flutter_controller_->ForceRedraw();

  return true;
}

void FlutterWindow::OnDestroy() {
  if (flutter_controller_) {
    flutter_controller_ = nullptr;
  }

  Win32Window::OnDestroy();
}

LRESULT
FlutterWindow::MessageHandler(HWND hwnd, UINT const message,
                              WPARAM const wparam,
                              LPARAM const lparam) noexcept {

  if (message == WM_NCCALCSIZE) {
    if (wparam == TRUE && IsWindowMaximized(hwnd)) {
      NCCALCSIZE_PARAMS* sz = reinterpret_cast<NCCALCSIZE_PARAMS*>(lparam);
      HMONITOR monitor = ::MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
      MONITORINFO mi = {sizeof(MONITORINFO)};
      if (::GetMonitorInfo(monitor, &mi)) {
        sz->rgrc[0] = mi.rcWork;
      }
    }
    return 0;
  }

  if (message == WM_NCPAINT) {
    return 0;
  }
  if (message == WM_NCACTIVATE) {
    return TRUE;
  }

  if (message == WM_NCHITTEST && !IsWindowMaximized(hwnd)) {
    POINT pt = {GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam)};
    ::ScreenToClient(hwnd, &pt);
    RECT client;
    ::GetClientRect(hwnd, &client);

    const double scale = FlutterDesktopGetDpiForHWND(hwnd) / 96.0;
    const int grip =
        static_cast<int>((kShadowMarginDip + kResizeGripDip) * scale);
    const bool left = pt.x < grip;
    const bool right = pt.x >= client.right - grip;
    const bool top = pt.y < grip;
    const bool bottom = pt.y >= client.bottom - grip;

    if (top && left) return HTTOPLEFT;
    if (top && right) return HTTOPRIGHT;
    if (bottom && left) return HTBOTTOMLEFT;
    if (bottom && right) return HTBOTTOMRIGHT;
    if (left) return HTLEFT;
    if (right) return HTRIGHT;
    if (top) return HTTOP;
    if (bottom) return HTBOTTOM;
  }

  if (flutter_controller_) {
    std::optional<LRESULT> result =
        flutter_controller_->HandleTopLevelWindowProc(hwnd, message, wparam,
                                                      lparam);
    if (result) {
      return *result;
    }
  }

  switch (message) {
    case WM_FONTCHANGE:
      flutter_controller_->engine()->ReloadSystemFonts();
      break;
  }

  return Win32Window::MessageHandler(hwnd, message, wparam, lparam);
}
