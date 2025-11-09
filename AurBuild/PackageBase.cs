using System.Diagnostics;

namespace AurBuild;

internal readonly record struct PackageBase(string Name, string Version, int Release, IEnumerable<string> Packages) : IComparable<PackageBase> {
    public static bool operator <(PackageBase left, PackageBase right) => left.CompareTo(right) < 0;

    public static bool operator >(PackageBase left, PackageBase right) => left.CompareTo(right) > 0;

    public static bool operator <=(PackageBase left, PackageBase right) => left.CompareTo(right) <= 0;

    public static bool operator >=(PackageBase left, PackageBase right) => left.CompareTo(right) >= 0;

    public int CompareTo(PackageBase other) {
        ProcessStartInfo psi = new() {
            FileName = "vercmp",
            ArgumentList = {
                Version,
                other.Version
            },
            UseShellExecute = false,
            RedirectStandardOutput = true
        };

        var p = Process.Start(psi)!;

        p.WaitForExit();

        var result = int.Parse(p.StandardOutput.ReadToEnd());

        return result != 0 ? result : Release.CompareTo(other.Release);
    }
}
