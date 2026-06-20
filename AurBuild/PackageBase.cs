using System.Diagnostics;

namespace AurBuild;

internal readonly record struct PackageBase(string Name, string Version, int Release, int? Epoch, IEnumerable<string> Packages) : IComparable<PackageBase> {
    public static bool operator <(PackageBase left, PackageBase right) => left.CompareTo(right) < 0;

    public static bool operator >(PackageBase left, PackageBase right) => left.CompareTo(right) > 0;

    public static bool operator <=(PackageBase left, PackageBase right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PackageBase left, PackageBase right) => left.CompareTo(right) >= 0;

    public int CompareTo(PackageBase other) {
        ProcessStartInfo psi = new() {
            FileName = "vercmp",
            ArgumentList = {
                $"{(Epoch is not null ? $"{Epoch.Value}:" : string.Empty)}{Version}-{Release}",
                $"{(other.Epoch is not null ? $"{other.Epoch.Value}:" : string.Empty)}{other.Version}-{other.Release}"
            },
            UseShellExecute = false,
            RedirectStandardOutput = true
        };

        var p = Process.Start(psi)!;

        p.WaitForExit();

        return int.Parse(p.StandardOutput.ReadToEnd());
    }
}
