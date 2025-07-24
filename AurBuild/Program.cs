﻿using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AurBuild;

internal static class Program {
    private const string RepoPath = "/repo/";
    private static readonly Regex PkgBaseRegex = new("^pkgbase = (?<pkgbase>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgVerRegex = new(@"^\s+pkgver = (?<pkgver>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgRelRegex = new(@"^\s+pkgrel = (?<pkgrel>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgNameRegex = new("^pkgname = (?<pkgname>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PkgFilenameRegex = new(@"^.+-(?<pkgver>.+)?-(?<pkgrel>\d+)?-(x86_64|any).pkg.tar.zst$", RegexOptions.Compiled);

    private static async Task<int> Main() {
        try {
            var packageDirs = Directory.EnumerateDirectories(Environment.CurrentDirectory).Except([
                Path.Combine(Environment.CurrentDirectory, ".git"), Path.Combine(Environment.CurrentDirectory, ".github")
            ]);

            foreach (var packageDir in packageDirs) {
                var pkgbase = await ParseSrcinfo(packageDir);
                var needed = CompareVersionAndPrepare(pkgbase);

                if (needed) {
                    Console.WriteLine(pkgbase.Name);
                    await BuildPackage(packageDir, pkgbase);
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
        var pkgNameMatches = PkgNameRegex.Matches(srcInfo);

        if (!baseNameMatch.Success || !baseVerMatch.Success || !baseRelMatch.Success || pkgNameMatches.Count == 0) {
            throw new($"Failed to parse srcinfo for {packageDir}");
        }

        var baseName = baseNameMatch.Groups["pkgbase"].Value;
        var baseVer = baseVerMatch.Groups["pkgver"].Value;
        var baseRel = int.Parse(baseRelMatch.Groups["pkgrel"].Value);
        var pkgs = pkgNameMatches.Select(static m => m.Groups["pkgname"].Value);

        return new(baseName, baseVer, baseRel, pkgs);
    }

    private static bool CompareVersionAndPrepare(PackageBase pkgbase) {
        var firstName = pkgbase.Packages.First();
        var files = Directory.GetFiles(RepoPath, $"{firstName}-*.pkg.tar.zst");

        switch (files.Length) {
            case 0:
                return true;

            case > 1:
                throw new("Too many files.");
        }

        var fm = PkgFilenameRegex.Match(Path.GetFileName(files[0]));

        if (!fm.Success) {
            throw new("Failed.");
        }

        var pkgVer = fm.Groups["pkgver"].Value;
        var pkgRel = int.Parse(fm.Groups["pkgrel"].Value);
        PackageBase existingPb = new(firstName, pkgVer, pkgRel, []);

        if (pkgbase > existingPb) {
            File.Delete(Path.Combine(RepoPath, files[0]));
            File.Delete(Path.Combine(RepoPath, $"{files[0]}.sig"));

            return true;
        }

        return false;
    }

    private static async Task BuildPackage(string packageDir, PackageBase pkgbase) {
        ProcessStartInfo psi1 = new() {
            FileName = "makepkg",
            ArgumentList = {
                "-s",
                "--noconfirm"
            },
            UseShellExecute = false,
            WorkingDirectory = packageDir
        };

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
        }
    }

    private static string GetAndCopyPackageFiles(string source, PackageBase pkgbase, string packageName) {
        DirectoryInfo sourceDir = new(source);
        DirectoryInfo destination = new(RepoPath);
        var files = sourceDir.GetFiles($"{packageName}-{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst");
        var destinationFile = files[0].CopyTo(Path.Combine(destination.FullName, files[0].Name));
        var files2 = sourceDir.GetFiles($"{packageName}-{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst.sig");
        files2[0].CopyTo(Path.Combine(destination.FullName, files2[0].Name));

        return destinationFile.FullName;
    }
}
