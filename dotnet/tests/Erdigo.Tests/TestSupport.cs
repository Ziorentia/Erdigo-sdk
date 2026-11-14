using System;
using System.IO;
using System.Text.Json;
using Erdigo.Core;
using Xunit;

namespace Erdigo.Tests;

/// <summary>Loads vector files from the shared spec folder.</summary>
internal static class VectorFiles
{
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "spec", "vectors")))
            {
                directory = directory.Parent;
            }
            return directory?.FullName ?? throw new InvalidOperationException("the repository's spec folder was not found");
        }
    }

    public static JsonElement Load(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static byte[] Bytes(string? text) =>
        Hex.Decode(text ?? throw new ArgumentNullException(nameof(text))) ?? throw new FormatException("not hexadecimal");

    public static byte[] Bytes(JsonElement element, string name) => Bytes(element.GetProperty(name).GetString());

    public static byte[]? OptionalBytes(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? Bytes(value.GetString()) : null;
}

/// <summary>A test that runs only on Windows.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

/// <summary>A test that runs only where files have Unix permissions.</summary>
public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix permissions only";
        }
    }
}
