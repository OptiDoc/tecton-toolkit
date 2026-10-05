using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pack;

/// <summary>Ошибка разбора или проверки описания расширения.</summary>
internal sealed class PackException(string message) : Exception(message);

/// <summary>Описание расширения (manifest.json) — часть 1 стандарта (версии 1.2 и 1.3).</summary>
internal sealed class Description
{
    private const int MaxBytes = 65536;

    private static readonly string[] TopFields =
    {
        "manifest_version", "id", "section", "name", "summary", "version", "core_min",
        "category", "availability", "publisher", "icon", "kind", "entrypoint", "install",
        "requires", "dependencies", "conflicts", "references", "license_features",
        "commands", "ui", "package",
    };

    private static readonly string[] NewIn13 =
    {
        "summary", "category", "availability", "publisher", "icon",
        "kind", "entrypoint", "install",
    };

    private static readonly string[] Platforms = { "revit", "autocad", "civil3d", "desktop" };
    private static readonly string[] Categories =
    {
        "formats", "stamps", "numbering", "text", "layers", "layouts", "tables", "tools",
    };

    private static readonly string[] Availabilities = { "base", "planned" };
    private static readonly string[] Targets =
    {
        "autocad_extension", "revit_addin", "block_library", "desktop",
    };

    private static readonly string[] FeaturePrefixes = { "section:", "module:", "panel:", "feature:" };
    private static readonly string[] RequireKeys = { "platforms", "platform_min", "capabilities" };
    private static readonly string[] UiKeys = { "tab", "panel", "order" };
    private static readonly string[] PackageKeys = { "url", "sha256", "size", "signature" };
    private static readonly string[] EntryKeys = { "assembly", "type" };
    private static readonly string[] InstallKeys = { "target", "subpath" };
    private static readonly string[] CommandKeys = { "id", "title", "file", "method" };

    private static readonly Regex LocaleKey =
        new(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,10})*$", RegexOptions.Compiled);

    private static readonly Regex IdPattern =
        new(@"^[A-Z0-9][A-Z0-9-]{2,31}$", RegexOptions.Compiled);

    private static readonly Regex CommandIdPattern =
        new(@"^[a-z][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    private static readonly Regex SemVerPattern =
        new(@"^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$", RegexOptions.Compiled);

    private static readonly Regex CoreMinPattern =
        new(@"^(?:\d{4}\.\d{1,3}|\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?)$", RegexOptions.Compiled);

    private static readonly Regex Hex64Lower = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex SignaturePattern =
        new(@"^ed25519:[A-Za-z0-9+/]+={0,2}$", RegexOptions.Compiled);

    private static readonly Regex ConflictPattern =
        new(@"^[A-Z0-9][A-Z0-9-]{2,31}@(?:<|<=|=|\^)\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$",
            RegexOptions.Compiled);

    private static readonly Regex CapabilityPattern =
        new(@"^[a-z][a-z0-9_]{1,63}$", RegexOptions.Compiled);

    public string ManifestVersion { get; private set; } = "";
    public string Id { get; private set; } = "";
    public string Section { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string CoreMin { get; private set; } = "";
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

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > MaxBytes)
        {
            throw new PackException($"описание: {bytes.Length} байт больше предела {MaxBytes}");
        }

        var text = System.Text.Encoding.UTF8.GetString(bytes);
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
            if (d.ManifestVersion is not ("1.2" or "1.3"))
            {
                throw new PackException($"{source}: manifest_version должен быть 1.2 или 1.3");
            }

            RejectUnknown(doc.RootElement, TopFields, "", source);
            if (d.ManifestVersion == "1.2")
            {
                foreach (var f in NewIn13)
                {
                    if (doc.RootElement.TryGetProperty(f, out _))
                    {
                        throw new PackException(
                            $"{source}: поле «{f}» появилось в 1.3, в описании 1.2 оно запрещено");
                    }
                }
            }

            d.Id = RequireString(doc.RootElement, "id", source);
            if (!IdPattern.IsMatch(d.Id))
            {
                throw new PackException($"{source}: id «{d.Id}» не соответствует ^[A-Z0-9][A-Z0-9-]{{2,31}}$");
            }

            d.Section = RequireString(doc.RootElement, "section", source);
            if (!Regex.IsMatch(d.Section, @"^[A-Z]{2,4}$"))
            {
                throw new PackException($"{source}: section «{d.Section}» не соответствует ^[A-Z]{{2,4}}$");
            }

            RequireLocale(doc.RootElement, "name", source, 0);
            d.Version = RequireString(doc.RootElement, "version", source);
            if (d.Version.Length > 64 || !SemVerPattern.IsMatch(d.Version))
            {
                throw new PackException($"{source}: version «{d.Version}» не является версией X.Y.Z");
            }

            d.CoreMin = RequireString(doc.RootElement, "core_min", source);
            if (d.CoreMin.Length > 32 || !CoreMinPattern.IsMatch(d.CoreMin))
            {
                throw new PackException($"{source}: core_min «{d.CoreMin}» не является версией ядра");
            }

            ParseRequires(doc.RootElement, source);
            ParseOptionalIdArray(doc.RootElement, "dependencies", source, allowSelf: false);
            ParseOptionalIdArray(doc.RootElement, "references", source, allowSelf: true);
            ParseConflicts(doc.RootElement, source);
            ParseLicenseFeatures(doc.RootElement, source);

            if (d.ManifestVersion == "1.3")
            {
                RequireLocale(doc.RootElement, "summary", source, 300);
                d.Kind = RequireString(doc.RootElement, "kind", source);
                if (d.Kind is not ("code" or "data"))
                {
                    throw new PackException($"{source}: kind должен быть code или data");
                }

                var category = RequireString(doc.RootElement, "category", source);
                if (Array.IndexOf(Categories, category) < 0)
                {
                    throw new PackException($"{source}: неизвестный category «{category}»");
                }

                var availability = RequireString(doc.RootElement, "availability", source);
                if (Array.IndexOf(Availabilities, availability) < 0)
                {
                    throw new PackException($"{source}: неизвестный availability «{availability}»");
                }

                var publisher = RequireString(doc.RootElement, "publisher", source);
                if (publisher.Length > 64)
                {
                    throw new PackException($"{source}: publisher длиннее 64 знаков");
                }

                if (doc.RootElement.TryGetProperty("icon", out _))
                {
                    var iconText = RequireString(doc.RootElement, "icon", source);
                    if (iconText.Length > 64 ||
                        !(iconText.StartsWith("preset:", StringComparison.Ordinal) ||
                          iconText.StartsWith("assets/", StringComparison.Ordinal)))
                    {
                        throw new PackException($"{source}: icon «{iconText}» не preset:<имя> и не assets/…");
                    }
                }
            }
            else
            {
                d.Kind = "code";
            }

            if (doc.RootElement.TryGetProperty("entrypoint", out var entry))
            {
                RejectUnknown(entry, EntryKeys, ".entrypoint", source);
                d.EntrypointAssembly = RequireString(entry, "assembly", source);
                d.EntrypointType = RequireString(entry, "type", source);
            }
            else if (d.Kind == "code" && d.ManifestVersion == "1.3")
            {
                throw new PackException($"{source}: для kind=code обязателен блок entrypoint");
            }

            if (doc.RootElement.TryGetProperty("install", out var install))
            {
                RejectUnknown(install, InstallKeys, ".install", source);
                d.InstallTarget = OptionalString(install, "target") ?? d.InstallTarget;
                d.InstallSubpath = OptionalString(install, "subpath") ?? "";
            }

            if (d.InstallSubpath.Length == 0)
            {
                d.InstallSubpath = $"{d.Slug}/{d.Version}";
            }

            ValidateSubpath(d.InstallSubpath, source);
            if (Array.IndexOf(Targets, d.InstallTarget) < 0)
            {
                throw new PackException($"{source}: неизвестный install.target «{d.InstallTarget}»");
            }

            ParseUi(doc.RootElement, source);
            ParseCommands(d, doc.RootElement, source);
            ParsePackage(d, doc.RootElement, source);

            return d;
        }
    }

    /// <summary>Вывод method из id команды: open_layout_manager → OpenLayoutManager.</summary>
    internal static string DeriveMethod(string commandId)
    {
        var parts = commandId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var result = string.Concat(parts.Select(Capitalize));
        return result;

        static string Capitalize(string s) =>
            char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static void ParseRequires(JsonElement root, string source)
    {
        if (!root.TryGetProperty("requires", out var requires) ||
            requires.ValueKind != JsonValueKind.Object)
        {
            throw new PackException($"{source}: обязателен объект «requires»");
        }

        RejectUnknown(requires, RequireKeys, ".requires", source);
        if (!requires.TryGetProperty("platforms", out var platforms) ||
            platforms.ValueKind != JsonValueKind.Array ||
            platforms.GetArrayLength() == 0)
        {
            throw new PackException($"{source}: requires.platforms должен быть непустым массивом");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in platforms.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.String ||
                Array.IndexOf(Platforms, p.GetString()) < 0 || !seen.Add(p.GetString()!))
            {
                throw new PackException(
                    $"{source}: requires.platforms: недопустимое или повторяющееся значение");
            }
        }

        if (requires.TryGetProperty("platform_min", out var mins) &&
            mins.ValueKind == JsonValueKind.Object)
        {
            foreach (var m in mins.EnumerateObject())
            {
                if (!seen.Contains(m.Name) || m.Value.ValueKind != JsonValueKind.String ||
                    !CoreMinPattern.IsMatch(m.Value.GetString()!))
                {
                    throw new PackException(
                        $"{source}: requires.platform_min «{m.Name}»: не платформа или не версия");
                }
            }
        }

        if (requires.TryGetProperty("capabilities", out var caps) &&
            caps.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in caps.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.String || !CapabilityPattern.IsMatch(c.GetString()!))
                {
                    throw new PackException($"{source}: requires.capabilities: недопустимое значение");
                }
            }
        }
    }

    private static void ParseOptionalIdArray(
        JsonElement root, string name, string source, bool allowSelf)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new PackException($"{source}: «{name}» должен быть массивом");
        }

        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String || !IdPattern.IsMatch(v.GetString()!))
            {
                throw new PackException($"{source}: «{name}»: недопустимый идентификатор");
            }

            if (!allowSelf && string.Equals(v.GetString(), RequireString(root, "id", source),
                    StringComparison.Ordinal))
            {
                throw new PackException($"{source}: «{name}» ссылается на само себя");
            }
        }
    }

    private static void ParseConflicts(JsonElement root, string source)
    {
        if (!root.TryGetProperty("conflicts", out var arr) ||
            arr.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new PackException($"{source}: «conflicts» должен быть массивом");
        }

        foreach (var v in arr.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.String || !ConflictPattern.IsMatch(v.GetString()!))
            {
                throw new PackException(
                    $"{source}: «conflicts»: запись «{v}» не вида ID@<оператор><версия>");
            }
        }
    }

    private static void ParseLicenseFeatures(JsonElement root, string source)
    {
        if (!root.TryGetProperty("license_features", out var arr) ||
            arr.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (arr.ValueKind != JsonValueKind.Array)
        {
            throw new PackException($"{source}: «license_features» должен быть массивом");
        }

        foreach (var v in arr.EnumerateArray())
        {
            var text = v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var known = FeaturePrefixes.Any(
                p => text.StartsWith(p, StringComparison.Ordinal) && text.Length > p.Length);
            if (!known)
            {
                throw new PackException(
                    $"{source}: «license_features»: значение «{text}» не из section:/module:/panel:/feature:");
            }
        }
    }

    private static void ParseUi(JsonElement root, string source)
    {
        if (!root.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Object)
        {
            throw new PackException($"{source}: обязателен объект «ui»");
        }

        RejectUnknown(ui, UiKeys, ".ui", source);
        RequireString(ui, "tab", source);
        RequireString(ui, "panel", source);
        if (!ui.TryGetProperty("order", out var order) || order.ValueKind != JsonValueKind.Number ||
            !order.TryGetInt64(out var o) || o < 0)
        {
            throw new PackException($"{source}: ui.order должен быть целым не меньше 0");
        }
    }

    private static void ParseCommands(Description d, JsonElement root, string source)
    {
        if (!root.TryGetProperty("commands", out var commands) ||
            commands.ValueKind == JsonValueKind.Null)
        {
            commands = default;
        }

        if (commands.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in commands.EnumerateArray())
            {
                RejectUnknown(c, CommandKeys, "commands[]", source);
                var id = RequireString(c, "id", source);
                if (!CommandIdPattern.IsMatch(id))
                {
                    throw new PackException(
                        $"{source}: id команды «{id}» нарушает ^[a-z][a-z0-9_]{{1,63}}$");
                }

                if (d.Commands.Any(x => x.Id == id))
                {
                    throw new PackException($"{source}: дубль команды «{id}»");
                }

                RequireLocale(c, "title", source, 0);

                var file = OptionalString(c, "file");
                var method = OptionalString(c, "method");
                if (d.ManifestVersion == "1.2" && (file != null || method != null))
                {
                    throw new PackException(
                        $"{source}: поля file/method команды появились в 1.3, в 1.2 они запрещены");
                }

                d.Commands.Add(new CommandInfo(id, file ?? d.EntrypointAssembly,
                    method ?? DeriveMethod(id)));
            }
        }

        if (d.Kind == "code" && d.Commands.Count == 0)
        {
            throw new PackException($"{source}: для kind=code нужна хотя бы одна команда");
        }

        if (d.ManifestVersion == "1.3")
        {
            foreach (var c in d.Commands)
            {
                if (c.File.Length == 0)
                {
                    throw new PackException(
                        $"{source}: у команды «{c.Id}» нет file и в описании нет entrypoint.assembly");
                }
            }
        }
    }

    private static void ParsePackage(Description d, JsonElement root, string source)
    {
        if (!root.TryGetProperty("package", out var pkg) || pkg.ValueKind != JsonValueKind.Object)
        {
            throw new PackException($"{source}: обязателен объект «package»");
        }

        RejectUnknown(pkg, PackageKeys, ".package", source);
        d.Package.Url = OptionalString(pkg, "url") ?? "";
        if (d.Package.Url.Length > 0 &&
            !(d.Package.Url.StartsWith("https://", StringComparison.Ordinal) ||
              d.Package.Url.StartsWith("s3://", StringComparison.Ordinal)))
        {
            throw new PackException($"{source}: package.url должен быть https:// или s3://");
        }

        if (Regex.IsMatch(d.Package.Url, @"^https://[^/@]*@"))
        {
            throw new PackException($"{source}: package.url не должен содержать «пользователь@»");
        }

        d.Package.Sha256 = OptionalString(pkg, "sha256") ?? "";
        if (d.Package.Sha256.Length > 0 && !Hex64Lower.IsMatch(d.Package.Sha256))
        {
            throw new PackException($"{source}: package.sha256 должен быть 64 знаками a-f0-9");
        }

        d.Package.Signature = OptionalString(pkg, "signature") ?? "";
        if (d.Package.Signature.Length > 0)
        {
            if (!SignaturePattern.IsMatch(d.Package.Signature))
            {
                throw new PackException($"{source}: package.signature должен быть ed25519:<base64>");
            }

            var decoded = Convert.FromBase64String(d.Package.Signature["ed25519:".Length..]);
            if (decoded.Length != 64)
            {
                throw new PackException($"{source}: package.signature должна быть длиной 64 байта");
            }
        }

        if (pkg.TryGetProperty("size", out var size))
        {
            if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var s) || s < 0)
            {
                throw new PackException($"{source}: package.size должен быть целым не меньше 0");
            }

            var limit = d.Kind == "code" ? 8L * 1024 * 1024 : 256L * 1024;
            if (s > limit)
            {
                throw new PackException($"{source}: package.size больше предела {limit} для класса {d.Kind}");
            }

            d.Package.Size = s;
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

    /// <summary>Проверяет объект локалей: объект обязателен, ключ <c>ru</c> обязателен и непуст.</summary>
    private static void RequireLocale(JsonElement obj, string name, string source, int maxLen)
    {
        if (!obj.TryGetProperty(name, out var loc) || loc.ValueKind == JsonValueKind.Null)
        {
            throw new PackException($"{source}: обязателен объект «{name}»");
        }

        if (loc.ValueKind != JsonValueKind.Object)
        {
            throw new PackException($"{source}: «{name}» должен быть объектом локалей");
        }

        foreach (var p in loc.EnumerateObject())
        {
            if (!LocaleKey.IsMatch(p.Name) || p.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(p.Value.GetString()))
            {
                throw new PackException($"{source}: «{name}.{p.Name}» должно быть непустой строкой");
            }

            if (maxLen > 0 && p.Value.GetString()!.Length > maxLen)
            {
                throw new PackException($"{source}: «{name}.{p.Name}» длиннее {maxLen} знаков");
            }
        }

        if (!loc.TryGetProperty("ru", out var ru) || ru.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(ru.GetString()))
        {
            throw new PackException($"{source}: в «{name}» обязателен непустой ключ «ru»");
        }
    }

    private static void RejectUnknown(JsonElement obj, string[] allowed, string context, string source)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (Array.IndexOf(allowed, p.Name) < 0)
            {
                var full = context.Length == 0 ? p.Name : $"{context}.{p.Name}";
                throw new PackException($"{source}: неизвестное поле «{full}»");
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
