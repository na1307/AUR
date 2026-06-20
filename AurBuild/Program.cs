using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AurBuild;

internal static class Program {
    private const string RepoPath = "/mnt/repo/pkgs/";
    private const string AurPkgs = "/mnt/repo/aur-pkgs.json";
    private static readonly Regex PkgBaseRegex = new("^pkgbase = (?<pkgbase>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgVerRegex = new(@"^\s+pkgver = (?<pkgver>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgRelRegex = new(@"^\s+pkgrel = (?<pkgrel>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex EpochRegex = new(@"^\s+epoch = (?<epoch>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgNameRegex = new("^pkgname = (?<pkgname>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgFilenameRegex = new(@"^(?<pkgname>.+)-((?<epoch>\d+):)?(?<pkgver>.+)-(?<pkgrel>\d+)-(x86_64|any)\.pkg\.tar\.zst$", RegexOptions.Compiled);

    private static async Task<int> Main() {
        try {
            var aurBuildDir = Path.Combine(AppContext.BaseDirectory, "ab");

            Directory.CreateDirectory(aurBuildDir);

            var pkgs = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(AurPkgs), AurBuildJsonSerializationContext.Default.AurPkgArray)!;

            foreach (var pkg in pkgs) {
                ProcessStartInfo gitPsi = new() {
                    FileName = "git",
                    ArgumentList = {
                        "clone",
                        $"https://aur.archlinux.org/{pkg.Name}.git",
                        "--depth=1"
                    },
                    UseShellExecute = false,
                    WorkingDirectory = aurBuildDir
                };

                var git = Process.Start(gitPsi)!;

                await git.WaitForExitAsync();

                if (git.ExitCode != 0) {
                    throw new("git clone failed.");
                }

                var packageDir = Path.Combine(aurBuildDir, pkg.Name);

                if (pkg.VcsPkg) {
                    ProcessStartInfo psiu = new() {
                        FileName = "makepkg",
                        ArgumentList = {
                            "-cdo"
                        },
                        UseShellExecute = false,
                        WorkingDirectory = packageDir
                    };

                    var pu = Process.Start(psiu)!;

                    await pu.WaitForExitAsync();

                    if (pu.ExitCode != 0) {
                        throw new("Failed to update version.");
                    }
                }

                var pkgbase = await ParseSrcinfo(packageDir);
                var needed = CompareVersionAndPrepare(pkgbase);

                if (needed) {
                    Console.WriteLine($"Building {pkgbase.Name}");
                    await BuildPackage(packageDir, pkgbase, pkg.Install);
                } else {
                    Console.WriteLine($"Skipping {pkgbase.Name}");
                }
            }

            return 0;
        } catch (Exception e) {
            Console.WriteLine(e);

            return 1;
        }
    }

    private static async Task<PackageBase> ParseSrcinfo(string packageDir) {
        ProcessStartInfo psi = new() {
            FileName = "makepkg",
            Arguments = "--printsrcinfo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            WorkingDirectory = packageDir
        };

        var p = Process.Start(psi)!;

        await p.WaitForExitAsync();

        var srcInfo = await p.StandardOutput.ReadToEndAsync();
        var baseNameMatch = PkgBaseRegex.Match(srcInfo);
        var baseVerMatch = PkgVerRegex.Match(srcInfo);
        var baseRelMatch = PkgRelRegex.Match(srcInfo);
        var baseEpochMatch = EpochRegex.Match(srcInfo);
        var pkgNameMatches = PkgNameRegex.Matches(srcInfo);

        if (!baseNameMatch.Success || !baseVerMatch.Success || !baseRelMatch.Success || pkgNameMatches.Count == 0) {
            throw new($"Failed to parse srcinfo for {packageDir}");
        }

        var baseName = baseNameMatch.Groups["pkgbase"].Value;
        var baseVer = baseVerMatch.Groups["pkgver"].Value;
        var baseRel = int.Parse(baseRelMatch.Groups["pkgrel"].Value);
        var baseEpoch = baseEpochMatch.Success ? int.Parse(baseEpochMatch.Groups["epoch"].Value) : (int?)null;
        var pkgs = pkgNameMatches.Select(static m => m.Groups["pkgname"].Value);

        return new(baseName, baseVer, baseRel, baseEpoch, pkgs);
    }

    private static bool CompareVersionAndPrepare(PackageBase pkgbase) {
        var existingFiles = Directory.GetFiles(RepoPath, "*.pkg.tar.zst");
        var pkgFiles = pkgbase.Packages.Select(p => existingFiles.SingleOrDefault(f => ParsePackageFile(f).Name == p)).ToArray();

        if (pkgFiles.Any(f => f is null)) {
            return true;
        }

        var existingPb = ParsePackageFile(pkgFiles[0]!);

        if (pkgbase <= existingPb) {
            return false;
        }

        foreach (var file in pkgFiles) {
            File.Delete(file!);

            var sig = file + ".sig";

            if (File.Exists(sig)) {
                File.Delete(sig);
            }
        }

        return true;
    }

    private static PackageBase ParsePackageFile(string file) {
        var fileName = Path.GetFileName(file);
        var match = PkgFilenameRegex.Match(fileName);

        if (!match.Success) {
            throw new($"Invalid package filename: {fileName}");
        }

        return new PackageBase(
            match.Groups["pkgname"].Value,
            match.Groups["pkgver"].Value,
            int.Parse(match.Groups["pkgrel"].Value),
            match.Groups["epoch"].Success ? int.Parse(match.Groups["epoch"].Value) : null,
            []
        );
    }

    private static async Task BuildPackage(string packageDir, PackageBase pkgbase, bool needInstall) {
        var patchesDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "patches");
        var pkgbuildPatch = $"{pkgbase.Name}_pkgbuild.patch";
        var sourcePatch = $"{pkgbase.Name}_source.patch";

        if (File.Exists(Path.Combine(patchesDir, pkgbuildPatch)) && File.Exists(Path.Combine(patchesDir, sourcePatch))) {
            ProcessStartInfo patchPsi = new() {
                FileName = "patch",
                ArgumentList = {
                    "-p1",
                    "-i",
                    Path.Combine(patchesDir, pkgbuildPatch)
                },
                UseShellExecute = false,
                WorkingDirectory = packageDir
            };

            var pPatch = Process.Start(patchPsi)!;

            await pPatch.WaitForExitAsync();

            if (pPatch.ExitCode != 0) {
                throw new("Failed to patch package.");
            }

            File.Copy(Path.Combine(patchesDir, sourcePatch), $"{packageDir}/{sourcePatch}");
        }

        ProcessStartInfo psi1 = new() {
            FileName = "makepkg",
            ArgumentList = {
                "-s",
                "--noconfirm",
                "--skippgpcheck"
            },
            UseShellExecute = false,
            WorkingDirectory = packageDir
        };

        if (needInstall) {
            psi1.ArgumentList.Add("-i");
        }

        var p1 = Process.Start(psi1)!;

        await p1.WaitForExitAsync();

        if (p1.ExitCode != 0) {
            throw new("Failed to build package.");
        }

        foreach (var package in pkgbase.Packages) {
            var fileName = GetAndCopyPackageFiles(packageDir, pkgbase, package);

            ProcessStartInfo psi2 = new() {
                FileName = "repo-add",
                ArgumentList = {
                    Path.Combine(RepoPath, "bluehill.db.tar.zst"),
                    fileName
                },
                UseShellExecute = false,
                WorkingDirectory = RepoPath
            };

            var p2 = Process.Start(psi2)!;

            await p2.WaitForExitAsync();

            if (p2.ExitCode != 0) {
                throw new("Failed to add package.");
            }
        }
    }

    private static string GetAndCopyPackageFiles(string source, PackageBase pkgbase, string packageName) {
        DirectoryInfo sourceDir = new(source);
        DirectoryInfo destination = new(RepoPath);

        var files = sourceDir.GetFiles($"{packageName}-{(pkgbase.Epoch is not null ? $"{pkgbase.Epoch.Value}:" : string.Empty)}{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst");

        if (files.Length == 0) {
            throw new($"No files: {packageName}-{(pkgbase.Epoch is not null ? $"{pkgbase.Epoch.Value}:" : string.Empty)}{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst");
        }

        var destinationFile = files[0].CopyTo(Path.Combine(destination.FullName, files[0].Name));

        var files2 = sourceDir.GetFiles($"{packageName}-{(pkgbase.Epoch is not null ? $"{pkgbase.Epoch.Value}:" : string.Empty)}{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst.sig");

        if (files2.Length == 0) {
            throw new($"No files: {packageName}-{(pkgbase.Epoch is not null ? $"{pkgbase.Epoch.Value}:" : string.Empty)}{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst.sig");
        }

        files2[0].CopyTo(Path.Combine(destination.FullName, files2[0].Name));

        return destinationFile.FullName;
    }
}
