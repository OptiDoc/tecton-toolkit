using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pack;

/// <summary>Детерминированная упаковка архива расширения (стандарт, часть 2).</summary>
internal static class Packer
{
    private static readonly string[] AllowedRoots = { "plugin", "blocks", "i18n", "assets" };
    private static readonly string[] ForbiddenExtensions =
        { ".exe", ".com", ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".msi", ".scr" };

    private static readonly DateTime StampTime = new(1980, 1, 1, 0, 0, 0);

    /// <summary>Собирает архив: файлы из <paramref name="source"/> + сгенерированный extension.json.</summary>
    public static (string Sha256, long Size) Build(Description d, string source, string output)
    {
        if (!Directory.Exists(source))
        {
            throw new PackException($"каталог содержимого не найден: {source}");
        }

        var files = Collect(source);
        if (files.Count == 0)
        {
            throw new PackException("каталог содержимого пуст");
        }

        CheckRules(files, d);
        var manifest = BuildExtensionJson(d, files);

        var entries = new List<(string Path, byte[] Bytes)>();
        foreach (var (rel, fullPath) in files)
        {
            entries.Add((rel, File.ReadAllBytes(fullPath)));
        }

        entries.Add(("extension.json", manifest));
        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var stream = File.Create(output))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (rel, bytes) in entries)
            {
                var entry = zip.CreateEntry(rel, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(StampTime, TimeSpan.Zero);
                using var target = entry.Open();
                target.Write(bytes, 0, bytes.Length);
            }
        }

        var zipBytes = File.ReadAllBytes(output);
        return (Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant(), zipBytes.LongLength);
    }

    /// <summary>Файлы содержимого: разрешённые корни, порядок — возрастание пути.</summary>
    internal static List<(string Rel, string Full)> Collect(string source)
    {
        var result = new List<(string, string)>();
        foreach (var full in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, full).Replace('\\', '/');
            var root = rel.Split('/')[0];
            if (Array.IndexOf(AllowedRoots, root) < 0)
            {
                throw new PackException(
                    $"файл «{rel}» вне разрешённых корней ({string.Join(", ", AllowedRoots)})");
            }

            if (Path.GetFileName(rel) == "extension.json" && !rel.Contains('/'))
            {
                throw new PackException("extension.json генерируется инструментом и не берётся из каталога");
            }

            result.Add((rel, full));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    private static void CheckRules(List<(string Rel, string Full)> files, Description d)
    {
        var limit = d.Kind == "code" ? 8L * 1024 * 1024 : 256L * 1024;
        foreach (var (rel, _) in files)
        {
            if (!Regex.IsMatch(rel, @"^[A-Za-z0-9._\-/]+$"))
            {
                throw new PackException($"путь «{rel}» содержит символы вне [A-Za-z0-9._-] и «/»");
            }

            if (rel.Contains("..", StringComparison.Ordinal))
            {
                throw new PackException($"путь «{rel}» содержит «..»");
            }

            if (rel.Split('/').Length - 1 > 4)
            {
                throw new PackException($"путь «{rel}» глубже 4 уровней");
            }

            var ext = Path.GetExtension(rel).ToLowerInvariant();
            if (Array.IndexOf(ForbiddenExtensions, ext) >= 0)
            {
                throw new PackException($"запрещённое расширение файла: «{rel}»");
            }
        }

        if (files.Count + 1 > 256)
        {
            throw new PackException($"в архиве больше 256 элементов: {files.Count + 1}");
        }

        var total = files.Sum(f => new FileInfo(f.Full).Length);
        if (total > limit)
        {
            throw new PackException(
                $"содержимое {total} байт превышает предел {limit} для класса {d.Kind}");
        }

        if (d.Kind == "code" && !files.Any(f => f.Rel.StartsWith("plugin/", StringComparison.Ordinal)))
        {
            throw new PackException("для kind=code в содержимом нет каталога plugin/");
        }

        if (d.Kind == "data" && !files.Any(f => f.Rel.StartsWith("blocks/", StringComparison.Ordinal)))
        {
            throw new PackException("для kind=data в содержимом нет каталога blocks/");
        }

        foreach (var c in d.Commands)
        {
            if (files.All(f => f.Rel != c.File))
            {
                throw new PackException($"файл команды «{c.File}» отсутствует в содержимом");
            }
        }

        if (d.Kind == "code" && files.All(f => f.Rel != d.EntrypointAssembly))
        {
            throw new PackException($"entrypoint.assembly «{d.EntrypointAssembly}» отсутствует в содержимом");
        }
    }

    private static byte[] BuildExtensionJson(Description d, List<(string Rel, string Full)> files)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("extension_version", "1.0");
            writer.WriteString("id", d.Id);
            writer.WriteString("kind", d.Kind);
            if (d.Kind == "code")
            {
                writer.WriteStartObject("entrypoint");
                writer.WriteString("assembly", d.EntrypointAssembly);
                writer.WriteString("type", d.EntrypointType);
                writer.WriteEndObject();
            }

            writer.WriteStartObject("install");
            writer.WriteString("target", d.InstallTarget);
            writer.WriteString("subpath", d.InstallSubpath);
            writer.WriteEndObject();

            writer.WriteStartArray("files");
            foreach (var (rel, full) in files)
            {
                var bytes = File.ReadAllBytes(full);
                writer.WriteStartObject();
                writer.WriteString("path", rel);
                writer.WriteString("sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                writer.WriteNumber("size", bytes.LongLength);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("commands");
            foreach (var c in d.Commands)
            {
                writer.WriteStartObject();
                writer.WriteString("id", c.Id);
                writer.WriteString("file", c.File);
                writer.WriteString("method", c.Method);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Заполняет package.sha256/size/signature в копии описания (выпуск по тегу).</summary>
    public static void Stamp(string descriptionPath, string sha256, long size, string signature)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(descriptionPath));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            void MergeInto(Utf8JsonWriter w, JsonElement obj)
            {
                w.WriteStartObject();
                foreach (var p in obj.EnumerateObject())
                {
                    if (p.NameEquals("package"))
                    {
                        w.WriteStartObject("package");
                        w.WriteString("url", p.Value.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                            ? u.GetString()
                            : "");
                        w.WriteString("sha256", sha256);
                        w.WriteNumber("size", size);
                        w.WriteString("signature", signature);
                        w.WriteEndObject();
                    }
                    else
                    {
                        p.WriteTo(w);
                    }
                }

                w.WriteEndObject();
            }

            MergeInto(writer, doc.RootElement);
        }

        File.WriteAllBytes(descriptionPath, stream.ToArray());
    }

    internal static string Sha256File(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    internal static string ReadUtf8(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (text.StartsWith("﻿", StringComparison.Ordinal))
        {
            throw new PackException("файл содержит BOM, требуется UTF-8 без BOM");
        }

        return text;
    }
}
