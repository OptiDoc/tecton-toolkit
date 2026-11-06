using System.IO.Compression;
using System.Text.Json;

namespace Pack;

/// <summary>Проверка архива против описания (стандарт, §2.6, шаги 3–9).</summary>
internal static class Verify
{
    private static readonly string[] ForbiddenExtensions =
        { ".exe", ".com", ".bat", ".cmd", ".ps1", ".sh", ".vbs", ".msi", ".scr" };

    public static string Check(Description d, string archivePath, string? publicKeyBase64)
    {
        if (!File.Exists(archivePath))
        {
            throw new PackException($"архив не найден: {archivePath}");
        }

        var sha256 = Packer.Sha256File(archivePath);
        var size = new FileInfo(archivePath).Length;
        var expectedName = $"{d.Slug}-{d.Version}.zip";
        if (!string.Equals(Path.GetFileName(archivePath), expectedName, StringComparison.Ordinal))
        {
            throw new PackException($"имя архива «{Path.GetFileName(archivePath)}» ≠ «{expectedName}»");
        }

        if (d.Package.Sha256.Length > 0 && d.Package.Sha256 != sha256)
        {
            throw new PackException($"sha256 архива {sha256} ≠ package.sha256 {d.Package.Sha256}");
        }

        if (d.Package.Size > 0 && d.Package.Size != size)
        {
            throw new PackException($"размер архива {size} ≠ package.size {d.Package.Size}");
        }

        if (publicKeyBase64 != null && d.Package.Signature.Length > 0)
        {
            var message = $"{d.Id}|{d.Version}|{sha256}";
            if (!SignTool.Verify(publicKeyBase64, message, d.Package.Signature))
            {
                throw new PackException("подпись архива не прошла проверку");
            }
        }

        var limit = d.Kind == "code" ? 8L * 1024 * 1024 : 256L * 1024;
        using var zip = ZipFile.OpenRead(archivePath);

        var seen = new List<(string Path, ZipArchiveEntry Entry)>();
        string? previous = null;
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
            {
                continue; // явно созданный каталог в архиве
            }

            var path = entry.FullName;
            if (previous != null && string.CompareOrdinal(path, previous) <= 0)
            {
                throw new PackException($"записи идут не по возрастанию пути: «{path}» после «{previous}»");
            }

            previous = path;
            seen.Add((path, entry));
        }

        if (seen.Count == 0)
        {
            throw new PackException("архив пуст");
        }

        if (seen.Count > 256)
        {
            throw new PackException($"в архиве больше 256 элементов: {seen.Count}");
        }

        long total = 0;
        foreach (var (path, entry) in seen)
        {
            if (path == "extension.json")
            {
                continue;
            }

            ValidatePath(path);
            if (entry.LastWriteTime.DateTime != new DateTime(1980, 1, 1))
            {
                throw new PackException($"у «{path}» время записи ≠ 1980-01-01T00:00:00");
            }

            if (entry.CompressedLength != entry.Length)
            {
                throw new PackException($"у «{path}» включено сжатие, требуется store");
            }

            total += entry.Length;
        }

        if (total > limit)
        {
            throw new PackException($"содержимое {total} байт превышает предел {limit} для класса {d.Kind}");
        }

        var extension = ReadExtension(zip);
        if (extension == null)
        {
            throw new PackException("в архиве нет extension.json");
        }

        using (extension)
        {
            CheckExtensionJson(d, extension.RootElement, seen);
        }

        // TECTON-122 (STANDARDS §2.7): гейт иконок ленты — до возврата:
        // неполный набор assets/ (две темы × два размера) = отказ.
        CheckIconMatrix(seen);

        return sha256;
    }

    private static void ValidatePath(string path)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9._\-/]+$"))
        {
            throw new PackException($"путь «{path}» содержит запрещённые символы");
        }

        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new PackException($"путь «{path}» содержит «..»");
        }

        if (path.Split('/').Length - 1 > 4)
        {
            throw new PackException($"путь «{path}» глубже 4 уровней");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(ForbiddenExtensions, ext) >= 0)
        {
            throw new PackException($"запрещённое расширение: «{path}»");
        }
    }

    /// <summary>
    /// TECTON-122 (STANDARDS §2.7): гейт иконок ленты в архиве расширения —
    /// каждое семейство assets/*.png обязано нести ПОЛНЫЙ набор: две темы
    /// (базовая + «.dark»-побратим) × два размера (-16 малый слот + -32
    /// большой; большая кнопка рендерит LargeImage 32 + Image 16-фолбэк) ×
    /// PNG с габаритами по суффиксу (IHDR); .xaml-побратимы семейства — либо
    /// у всех комбинаций, либо ни у одной. Иконок в assets/ может не быть
    /// вовсе (кнопка без иконки, fail-soft на стороне CAD) — тогда гейт молчит.
    /// Живой дефект-источник: в светлой теме ленты кнопки носили иконки
    /// тёмной — набор терялся при сборке пакета молча.
    /// </summary>
    private static void CheckIconMatrix(List<(string Path, ZipArchiveEntry Entry)> seen)
    {
        var icons = seen
            .Where(s => s.Path.StartsWith("assets/", StringComparison.Ordinal)
                && s.Path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (icons.Count == 0)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in icons)
        {
            names.Add(path);
        }

        var problems = new List<string>();
        foreach (var (path, entry) in icons)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var theme = "";
            if (stem.EndsWith(".dark", StringComparison.Ordinal))
            {
                theme = ".dark";
                stem = stem.Substring(0, stem.Length - 5);
            }

            string family;
            int size;
            if (stem.EndsWith("-16", StringComparison.Ordinal))
            {
                family = stem.Substring(0, stem.Length - 3);
                size = 16;
            }
            else if (stem.EndsWith("-32", StringComparison.Ordinal))
            {
                family = stem.Substring(0, stem.Length - 3);
                size = 32;
            }
            else
            {
                problems.Add($"{path}: имя вне конвенции «семейство-16|-32[.dark].png»");
                continue;
            }

            var peerTheme = theme.Length == 0 ? ".dark" : "";
            if (!names.Contains($"assets/{family}-{size}{peerTheme}.png"))
            {
                problems.Add($"{path}: нет темы-побратима assets/{family}-{size}{peerTheme}.png (нужны ОБЕ темы)");
            }

            var peerSize = size == 16 ? 32 : 16;
            if (!names.Contains($"assets/{family}-{peerSize}{theme}.png"))
            {
                problems.Add($"{path}: нет размера-побратима assets/{family}-{peerSize}{theme}.png (большая кнопка: 32 + 16-фолбэк)");
            }

            var (width, height) = PngDimensions(entry);
            if (width != size || height != size)
            {
                problems.Add($"{path}: габариты {width}x{height} != {size}x{size}");
            }
        }

        var xamls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in seen)
        {
            if (path.StartsWith("assets/", StringComparison.Ordinal)
                && path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                xamls.Add(path);
            }
        }

        var familyPng = new Dictionary<string, int>();
        var familyXaml = new Dictionary<string, int>();
        foreach (var (path, _) in icons)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var theme = "";
            if (stem.EndsWith(".dark", StringComparison.Ordinal))
            {
                theme = ".dark";
                stem = stem.Substring(0, stem.Length - 5);
            }

            var family = stem.EndsWith("-16", StringComparison.Ordinal)
                || stem.EndsWith("-32", StringComparison.Ordinal)
                    ? stem.Substring(0, stem.Length - 3)
                    : stem;
            familyPng[family] = familyPng.TryGetValue(family, out var png) ? png + 1 : 1;
            if (xamls.Contains($"assets/{stem}{theme}.xaml"))
            {
                familyXaml[family] = familyXaml.TryGetValue(family, out var xaml) ? xaml + 1 : 1;
            }
        }

        foreach (var (family, png) in familyPng)
        {
            familyXaml.TryGetValue(family, out var withXaml);
            if (withXaml > 0 && withXaml < png)
            {
                problems.Add($"assets/: векторные .xaml семейства «{family}» несимметричны ({withXaml} из {png} PNG) — нужно у всех или ни у кого");
            }
        }

        if (problems.Count > 0)
        {
            throw new PackException(
                "гейт иконок ленты (STANDARDS §2.7, TECTON-122): " + string.Join("; ", problems));
        }
    }

    /// <summary>(ширина, высота) PNG из IHDR-заголовка; битый/короткий файл — (0, 0).</summary>
    private static (int Width, int Height) PngDimensions(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var head = new byte[24];
            var read = 0;
            while (read < 24)
            {
                var chunk = stream.Read(head, read, 24 - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            if (read < 24 || head[0] != 0x89 || head[1] != (byte)'P' || head[2] != (byte)'N'
                || head[3] != (byte)'G')
            {
                return (0, 0);
            }

            return ((head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19],
                (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23]);
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }

    private static JsonDocument? ReadExtension(ZipArchive zip)
    {
        var entry = zip.GetEntry("extension.json");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var text = Packer.ReadUtf8(buffer.ToArray());
        return JsonDocument.Parse(text);
    }

    private static void CheckExtensionJson(
        Description d, JsonElement ext, List<(string Path, ZipArchiveEntry Entry)> seen)
    {
        static string Str(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            {
                throw new PackException($"extension.json: нет строкового поля «{name}»");
            }

            return v.GetString()!;
        }

        if (ext.ValueKind != JsonValueKind.Object)
        {
            throw new PackException("extension.json: корень должен быть объектом");
        }

        if (Str(ext, "extension_version") != "1.0")
        {
            throw new PackException("extension.json: extension_version ≠ 1.0");
        }

        if (Str(ext, "id") != d.Id)
        {
            throw new PackException($"extension.json: id «{Str(ext, "id")}» ≠ {d.Id}");
        }

        if (Str(ext, "kind") != d.Kind)
        {
            throw new PackException($"extension.json: kind «{Str(ext, "kind")}» ≠ {d.Kind}");
        }

        var install = ext.GetProperty("install");
        if (Str(install, "target") != d.InstallTarget || Str(install, "subpath") != d.InstallSubpath)
        {
            throw new PackException("extension.json: install не совпадает с описанием");
        }

        if (d.Kind == "code")
        {
            var entrypoint = ext.GetProperty("entrypoint");
            if (Str(entrypoint, "assembly") != d.EntrypointAssembly ||
                Str(entrypoint, "type") != d.EntrypointType)
            {
                throw new PackException("extension.json: entrypoint не совпадает с описанием");
            }
        }

        var files = new Dictionary<string, (string Sha256, long Size)>();
        foreach (var f in ext.GetProperty("files").EnumerateArray())
        {
            var path = Str(f, "path");
            if (files.ContainsKey(path))
            {
                throw new PackException($"extension.json: дубль пути «{path}»");
            }

            files[path] = (Str(f, "sha256"), f.GetProperty("size").GetInt64());
        }

        var archiveFiles = seen.Select(s => s.Path).Where(p => p != "extension.json").ToList();
        foreach (var path in archiveFiles)
        {
            if (!files.ContainsKey(path))
            {
                throw new PackException($"файл «{path}» есть в архиве, но нет в files[]");
            }
        }

        foreach (var path in files.Keys)
        {
            if (!archiveFiles.Contains(path))
            {
                throw new PackException($"файл «{path}» описан в files[], но отсутствует в архиве");
            }
        }

        foreach (var (path, meta) in files)
        {
            var entry = seen.First(x => x.Path == path).Entry;
            if (entry.Length != meta.Size)
            {
                throw new PackException($"размер «{path}» {entry.Length} ≠ files[].size {meta.Size}");
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
            if (actual != meta.Sha256.ToLowerInvariant())
            {
                throw new PackException($"sha256 «{path}» не совпадает с files[]");
            }
        }

        var commands = new HashSet<string>();
        foreach (var c in ext.GetProperty("commands").EnumerateArray())
        {
            var id = Str(c, "id");
            if (!commands.Add(id))
            {
                throw new PackException($"extension.json: дубль команды «{id}»");
            }

            var file = Str(c, "file");
            if (!files.ContainsKey(file))
            {
                throw new PackException($"файл команды «{file}» не входит в files[]");
            }
        }

        foreach (var c in d.Commands)
        {
            if (!commands.Contains(c.Id))
            {
                throw new PackException($"команда «{c.Id}» из описания отсутствует в extension.json");
            }
        }

        if (commands.Count != d.Commands.Count)
        {
            throw new PackException("число команд в extension.json не совпадает с описанием");
        }

        if (d.Kind == "code" && !files.ContainsKey(d.EntrypointAssembly))
        {
            throw new PackException("entrypoint.assembly не входит в files[]");
        }

    }
}
