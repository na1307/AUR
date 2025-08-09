using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AurBuild;

// Entry point and helper routines for building and updating an Arch Linux repository
internal static class Program {
    // Destination repository path where built packages and database live
    private const string RepoPath = "/repo/";

    // Regex to parse pkgbase from makepkg --printsrcinfo output
    private static readonly Regex PkgBaseRegex = new("^pkgbase = (?<pkgbase>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Regex to parse pkgver (version) from srcinfo
    private static readonly Regex PkgVerRegex = new(@"^\s+pkgver = (?<pkgver>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Regex to parse pkgrel (release) from srcinfo
    private static readonly Regex PkgRelRegex = new(@"^\s+pkgrel = (?<pkgrel>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Regex to parse each pkgname from srcinfo (there can be multiple subpackages)
    private static readonly Regex PkgNameRegex = new("^pkgname = (?<pkgname>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    // Regex to parse version and release from existing repo package filenames
    // Example: foo-1.2.3-1-x86_64.pkg.tar.zst
    private static readonly Regex PkgFilenameRegex = new(@"^.+-(?<pkgver>.+)?-(?<pkgrel>\d+)?-(x86_64|any).pkg.tar.zst$", RegexOptions.Compiled);

    // Main workflow: iterate package directories, decide if build is needed, build and add to repo
    private static async Task<int> Main() {
        try {
            // Enumerate subdirectories excluding .git; each directory represents a package
            var packageDirs = Directory.EnumerateDirectories(Environment.CurrentDirectory).Where(d => !d.StartsWith(".git"));

            foreach (var packageDir in packageDirs) {
                // Extract metadata from PKGBUILD via makepkg --printsrcinfo
                var pkgbase = await ParseSrcinfo(packageDir);
                // Check existing repo version and prepare (delete old files if upgrade is needed)
                var needed = CompareVersionAndPrepare(pkgbase);

                if (needed) {
                    Console.WriteLine(pkgbase.Name);
                    // Build and add the package(s) to repository
                    await BuildPackage(packageDir, pkgbase);
                } else {
                    Console.WriteLine($"Skipping {pkgbase.Name}");
                }
            }

            return 0;
        } catch (Exception e) {
            // Log and return non-zero on failure
            Console.WriteLine(e);

            return 1;
        }
    }

    // Runs makepkg --printsrcinfo and parses pkgbase, pkgver, pkgrel and pkgname(s)
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

        // Capture the srcinfo text for regex parsing
        var srcInfo = await p.StandardOutput.ReadToEndAsync();
        var baseNameMatch = PkgBaseRegex.Match(srcInfo);
        var baseVerMatch = PkgVerRegex.Match(srcInfo);
        var baseRelMatch = PkgRelRegex.Match(srcInfo);
        var pkgNameMatches = PkgNameRegex.Matches(srcInfo);

        // Validation: ensure we found required fields and at least one package
        if (!baseNameMatch.Success || !baseVerMatch.Success || !baseRelMatch.Success || pkgNameMatches.Count == 0) {
            throw new($"Failed to parse srcinfo for {packageDir}");
        }

        var baseName = baseNameMatch.Groups["pkgbase"].Value;
        var baseVer = baseVerMatch.Groups["pkgver"].Value;
        var baseRel = int.Parse(baseRelMatch.Groups["pkgrel"].Value);
        // Map all matched pkgname entries to a list
        var pkgs = pkgNameMatches.Select(static m => m.Groups["pkgname"].Value);

        // Construct a PackageBase model that implements comparison and exposes fields
        return new(baseName, baseVer, baseRel, pkgs);
    }

    // Compares the desired package version with what's currently in the repo.
    // If upgrade is needed, delete old package files to prepare for new ones.
    private static bool CompareVersionAndPrepare(PackageBase pkgbase) {
        // Determine an existing package by using the first package name
        var firstName = pkgbase.Packages.First();
        var files = Directory.GetFiles(RepoPath, $"{firstName}-*.pkg.tar.zst");

        switch (files.Length) {
            case 0:
                // No existing package found; build is needed
                return true;

            case > 1:
                // Ambiguous repo state: multiple versions found
                throw new("Too many files.");
        }

        // Parse the version and release from the existing package file name
        var fm = PkgFilenameRegex.Match(Path.GetFileName(files[0]));

        if (!fm.Success) {
            throw new("Failed.");
        }

        var pkgVer = fm.Groups["pkgver"].Value;
        var pkgRel = int.Parse(fm.Groups["pkgrel"].Value);
        // Represent the existing package as a PackageBase for comparison
        PackageBase existingPb = new(firstName, pkgVer, pkgRel, []);

        // If the source version is newer, remove all matching existing packages and their signatures
        if (pkgbase > existingPb) {
            foreach (var pkg in pkgbase.Packages) {
                // Delete existing package and its .sig file to avoid repo-add conflicts
                File.Delete(Path.Combine(RepoPath, Directory.GetFiles(RepoPath, $"{pkg}-*.pkg.tar.zst")[0]));
                File.Delete(Path.Combine(RepoPath, Directory.GetFiles(RepoPath, $"{pkg}-*.pkg.tar.zst.sig")[0]));
            }

            return true;
        }

        // Existing version is up-to-date or newer
        return false;
    }

    // Builds the package(s) with makepkg and adds them to the repository database using repo-add
    private static async Task BuildPackage(string packageDir, PackageBase pkgbase) {
        // Step 1: build packages (and dependencies) non-interactively
        ProcessStartInfo psi1 = new() {
            FileName = "makepkg",
            ArgumentList = {
                "-s",           // install dependencies
                "--noconfirm"   // don't prompt
            },
            UseShellExecute = false,
            WorkingDirectory = packageDir
        };

        var p1 = Process.Start(psi1)!;

        await p1.WaitForExitAsync();

        if (p1.ExitCode != 0) {
            throw new("Failed to build package.");
        }

        // Step 2: copy artifacts to the repo and update repo database
        foreach (var package in pkgbase.Packages) {
            var fileName = GetAndCopyPackageFiles(packageDir, pkgbase, package);

            ProcessStartInfo psi2 = new() {
                FileName = "repo-add",
                ArgumentList = {
                    Path.Combine(RepoPath, "bluehill.db.tar.zst"), // repo database file
                    fileName                                       // path to package file to add
                },
                UseShellExecute = false,
                WorkingDirectory = RepoPath
            };

            var p2 = Process.Start(psi2)!;

            await p2.WaitForExitAsync();
        }
    }

    // Copies built package and signature files to the repo directory and returns the package file path
    private static string GetAndCopyPackageFiles(string source, PackageBase pkgbase, string packageName) {
        DirectoryInfo sourceDir = new(source);
        DirectoryInfo destination = new(RepoPath);

        // Match the built package file, e.g., name-version-release-arch.pkg.tar.zst
        var files = sourceDir.GetFiles($"{packageName}-{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst");
        var destinationFile = files[0].CopyTo(Path.Combine(destination.FullName, files[0].Name));

        // Also copy the corresponding signature file
        var files2 = sourceDir.GetFiles($"{packageName}-{pkgbase.Version}-{pkgbase.Release}-*.pkg.tar.zst.sig");
        files2[0].CopyTo(Path.Combine(destination.FullName, files2[0].Name));

        return destinationFile.FullName;
    }
}
