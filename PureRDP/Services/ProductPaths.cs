using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PureRDP.Services;

/// <summary>
/// Centralizes the product data-directory name used by the current installation.
/// </summary>
internal static class ProductPaths
{
    public const string CurrentDataFolderName = "PureRDP";
    public static string PrimaryDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CurrentDataFolderName);

    // Mirrors existing persistence fallbacks; reporting must not create folders
    // or change where those services read and write.
    public static IReadOnlyList<string> GetDataDirectories() => new[]
    {
        PrimaryDataDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CurrentDataFolderName),
        Path.Combine(AppContext.BaseDirectory, "data", CurrentDataFolderName),
        Path.Combine(Path.GetTempPath(), CurrentDataFolderName, CurrentDataFolderName),
    }.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> DiagnosticFiles => new[]
    {
        Path.Combine(Path.GetTempPath(), "PureRDP-display.log"),
        Path.Combine(Path.GetTempPath(), "PureRDP-console.log"),
    };
}
