using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pack;

/// <summary>Ошибка разбора или проверки описания расширения.</summary>
internal sealed class PackException(string message) : Exception(message);

/// <summary>Описание расширения (manifest.json) — часть 1 стандарта (версии 1.2 и 1.3).</summary>
internal sealed class Description
{
    public string ManifestVersion { get; private set; } = "";
    public string Id { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Kind { get; private set; } = "code";
    public string EntrypointAssembly { get; private set; } = "";
    public string EntrypointType { get; private set; } = "";
    public string InstallTarget { get; private set; } = "autocad_extension";
    public string InstallSubpath { get; private set; } = "";
    public List<CommandInfo> Commands { get; } = new();
    public PackageInfo Package { get; } = new();

    /// <summary>Слаг: id в нижнем регистре без хвостового «-NNN» (стандарт, §0.1).</summary>
    public string Slug => SlugOf(Id);

    public static string SlugOf(string id)
    {
        var lowered = id.ToLowerInvariant();
        return Regex.Replace(lowered, @"-\d+$", "");
    }

    public static Description Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new PackException($"описание не найдено: {path}");
        }

        var text = File.ReadAllText(path);
        if (text.StartsWith("﻿", StringComparison.Ordinal))
        {
            throw new PackException("описание: обнаружен BOM, требуется UTF-8 без BOM");
        }

        return Parse(text, path);
    }

    public static Description Parse(string json, string source)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new PackException($"{source}: не валидный JSON ({ex.Message})");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new PackException($"{source}: корень должен быть объектом");
            }

            var d = new Description();
            d.ManifestVersion = RequireString(doc.RootElement, "manifest_version", source);
            if (d.ManifestVersion != "1.2" && d.ManifestVersion != "1.3")
            {
                throw new PackException($"{source}: manifest_version должен быть 1.2 или 1.3");
            }

            d.Id = RequireString(doc.RootElement, "id", source);
            if (!Regex.IsMatch(d.Id, @"^[A-Z0-9][A-Z0-9-]{2,31}$"))
            {
                throw new PackException($"{source}: id «{d.Id}» не соответствует ^[A-Z0-9][A-Z0-9-]{{2,31}}$");
            }

            d.Version = RequireString(doc.RootElement, "version", source);
            if (!Regex.IsMatch(d.Version, @"^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$"))
            {
                throw new PackException($"{source}: version «{d.Version}» не является версией вида X.Y.Z");
            }

            d.Kind = OptionalString(doc.RootElement, "kind") ?? "code";
            if (d.Kind is not ("code" or "data"))
            {
                throw new PackException($"{source}: kind должен быть code или data");
            }

            if (doc.RootElement.TryGetProperty("entrypoint", out var entry))
            {
                d.EntrypointAssembly = RequireString(entry, "assembly", source);
                d.EntrypointType = RequireString(entry, "type", source);
            }
            else if (d.Kind == "code")
            {
                throw new PackException($"{source}: для kind=code обязателен блок entrypoint");
            }

            if (doc.RootElement.TryGetProperty("install", out var install))
            {
                d.InstallTarget = OptionalString(install, "target") ?? d.InstallTarget;
                d.InstallSubpath = OptionalString(install, "subpath") ?? "";
            }

            if (d.InstallSubpath.Length == 0)
            {
                d.InstallSubpath = $"{d.Slug}/{d.Version}";
            }

            ValidateSubpath(d.InstallSubpath, source);
            if (d.InstallTarget is not ("autocad_extension" or "revit_addin" or "block_library" or "desktop"))
            {
                throw new PackException($"{source}: неизвестный install.target «{d.InstallTarget}»");
            }

            if (doc.RootElement.TryGetProperty("commands", out var commands) &&
                commands.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in commands.EnumerateArray())
                {
                    var id = RequireString(c, "id", source);
                    if (!Regex.IsMatch(id, @"^[a-z][a-z0-9_]{1,63}$"))
                    {
                        throw new PackException($"{source}: id команды «{id}» нарушает ^[a-z][a-z0-9_]{{1,63}}$");
                    }

                    if (d.Commands.Any(x => x.Id == id))
                    {
                        throw new PackException($"{source}: дубль команды «{id}»");
                    }

                    d.Commands.Add(new CommandInfo(id, RequireString(c, "file", source),
                        RequireString(c, "method", source)));
                }
            }

            if (d.Kind == "code" && d.Commands.Count == 0)
            {
                throw new PackException($"{source}: для kind=code нужна хотя бы одна команда");
            }

            if (doc.RootElement.TryGetProperty("package", out var pkg))
            {
                d.Package.Url = OptionalString(pkg, "url") ?? "";
                d.Package.Sha256 = OptionalString(pkg, "sha256") ?? "";
                d.Package.Signature = OptionalString(pkg, "signature") ?? "";
                if (pkg.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number)
                {
                    d.Package.Size = size.GetInt64();
                }
            }

            return d;
        }
    }

    private static void ValidateSubpath(string subpath, string source)
    {
        var parts = subpath.Split('/');
        if (parts.Length > 4)
        {
            throw new PackException($"{source}: install.subpath глубже 4 уровней");
        }

        foreach (var p in parts)
        {
            if (p.Length == 0 || p is "." or ".." || !Regex.IsMatch(p, @"^[A-Za-z0-9._-]+$"))
            {
                throw new PackException($"{source}: install.subpath «{subpath}» содержит запрещённый элемент");
            }
        }
    }

    private static string RequireString(JsonElement obj, string name, string source)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) ||
            v.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))
        {
            throw new PackException($"{source}: обязательно строковое поле «{name}»");
        }

        return v.GetString()!;
    }

    private static string? OptionalString(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) &&
            v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        return null;
    }
}

internal sealed record CommandInfo(string Id, string File, string Method);

internal sealed class PackageInfo
{
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Signature { get; set; } = "";
}
