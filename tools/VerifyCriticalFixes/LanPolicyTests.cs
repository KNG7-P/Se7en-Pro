using Se7enPro.Models;
using Se7enPro.Services;

namespace VerifyCriticalFixes;

internal static class LanPolicyTests
{
    internal static int Run()
    {
        Program.Section("LAN exposure policy");

        var failures = 0;
        void Check(bool ok, string what)
        {
            Program.Check(ok, what);
            if (!ok) failures++;
        }

        static UserSettings With(
            bool allowLan, bool auth, string user, string pass) => new()
        {
            AllowLanConnections = allowLan,
            LanAuthEnabled = auth,
            LanProxyUsername = user,
            LanProxyPassword = pass,
        };

        Check(LanPolicy.CanExpose(With(true, true, "se7en", "s3cret")),
              "LAN on + credentials: exposed");
        Check(!LanPolicy.CanExpose(With(true, false, "se7en", "s3cret")),
              "LAN on but auth disabled: stays on loopback");
        Check(!LanPolicy.CanExpose(With(true, true, "", "s3cret")),
              "LAN on but no username: stays on loopback");
        Check(!LanPolicy.CanExpose(With(true, true, "se7en", "")),
              "LAN on but no password: stays on loopback");
        Check(!LanPolicy.CanExpose(With(true, true, "  ", "s3cret")),
              "LAN on but whitespace username: stays on loopback");
        Check(!LanPolicy.CanExpose(With(false, true, "se7en", "s3cret")),
              "credentials but LAN off: stays on loopback");
        Check(!LanPolicy.CanExpose(new UserSettings()),
              "default settings: stays on loopback");

        var legacy = new UserSettings { AllowLanConnections = true, LanAuthEnabled = true };
        Check(!LanPolicy.CanExpose(legacy),
              "legacy profile without stored credentials: stays on loopback");

        return failures;
    }
}
