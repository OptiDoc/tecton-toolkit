using System.Text;

namespace Pack;

/// <summary>Самопроверка инструмента: детерминизм, подпись, отказы на нарушениях.</summary>
internal static class SelfTest
{
    private const string Manifest = """
{
  "manifest_version": "1.3",
  "id": "MOD-LAYOUT-001",
  "section": "CORE",
  "name": { "ru": "Диспетчер листов" },
  "version": "1.0.0",
  "core_min": "0.1.0",
  "requires": { "platforms": ["autocad"] },
  "summary": { "ru": "Управление листами в AutoCAD" },
  "category": "layouts",
  "availability": "base",
  "publisher": "OptiDoc",
  "kind": "code",
  "entrypoint": {
    "assembly": "plugin/LayoutManager.dll",
    "type": "Plugin.LayoutManager.LayoutManagerModule"
  },
  "install": { "target": "autocad_extension", "subpath": "mod-layout/1.0.0" },
  "commands": [
    { "id": "open_layout_manager", "title": { "ru": "Открыть диспетчер" },
      "file": "plugin/LayoutManager.dll", "method": "OpenLayoutManager" },
    { "id": "layout_properties", "title": { "ru": "Свойства листа" } }
  ],
  "ui": { "tab": "Tecton", "panel": "Layout Manager", "order": 10 },
  "package": { "url": "", "sha256": "", "size": 0, "signature": "" }
}
""";

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "tecton-pack-" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "content");
            Directory.CreateDirectory(Path.Combine(source, "plugin"));
            Directory.CreateDirectory(Path.Combine(source, "i18n"));
            File.WriteAllBytes(Path.Combine(source, "plugin", "LayoutManager.dll"),
                Encoding.UTF8.GetBytes("fake assembly payload for selftest"));
            File.WriteAllText(Path.Combine(source, "i18n", "ru.json"), "{\"cmd\":\"Открыть\"}", new UTF8Encoding(false));

            var descriptionPath = Path.Combine(root, "manifest.json");
            File.WriteAllText(descriptionPath, Manifest.ReplaceLineEndings("\n"), new UTF8Encoding(false));

            ExpectFailure(() => Description.Parse(
                    Manifest.Replace("  \"name\": { \"ru\": \"Диспетчер листов\" },\n", ""),
                    "no-name"),
                "описание без name отклоняется");
            ExpectFailure(() => Description.Parse(
                    Manifest.Replace("\"publisher\": \"OptiDoc\"", "\"publisherX\": \"OptiDoc\""), "bogus"),
                "неизвестное поле отклоняется");
            ExpectFailure(() => Description.Parse(
                    Manifest.Replace(
                        ", \"title\": { \"ru\": \"Свойства листа\" }", ""), "no-title"),
                "команда без title отклоняется");
            ExpectFailure(() => Description.Parse(
                    Manifest.Replace("\"manifest_version\": \"1.3\"", "\"manifest_version\": \"1.2\""),
                    "v12"),
                "описание 1.2 с полями 1.3 отклоняется");

            var archive1 = Path.Combine(root, "mod-layout-1.0.0.zip");
            var d = Description.Load(descriptionPath);
            Check(d.Commands[1].File == "plugin/LayoutManager.dll",
                "file команды выводится из entrypoint.assembly");
            Check(d.Commands[1].Method == "LayoutProperties",
                "method выводится из id команды");
            var (sha256, size) = Packer.Build(d, source, archive1);
            Check(sha256.Length == 64, "sha256 длиной 64 знака");
            Check(size > 0, "архив не пуст");

            var archive2 = Path.Combine(root, "second", "mod-layout-1.0.0.zip");
            var (sha256Again, sizeAgain) = Packer.Build(d, source, archive2);
            Check(sha256 == sha256Again && size == sizeAgain, "детерминизм: две упаковки дают один sha256");

            var (privateKey, publicKey) = SignTool.Keygen();
            var signature = SignTool.Sign(privateKey, $"{d.Id}|{d.Version}|{sha256}");
            Check(SignTool.Verify(publicKey, $"{d.Id}|{d.Version}|{sha256}", signature), "подпись проходит проверку");
            Check(!SignTool.Verify(publicKey, $"{d.Id}|{d.Version}|deadbeef", signature),
                "подпись не проходит для другого сообщения");

            var stampedPath = Path.Combine(root, "manifest.stamped.json");
            File.Copy(descriptionPath, stampedPath);
            Packer.Stamp(stampedPath, sha256, size, signature);
            var stamped = Description.Load(stampedPath);
            Check(stamped.Package.Sha256 == sha256, "stamp записал sha256");
            Check(stamped.Package.Size == size, "stamp записал размер");
            var reported = Verify.Check(stamped, archive1, publicKey);
            Check(reported == sha256, "verify принял архив с подписью");

            File.Copy(descriptionPath, Path.Combine(root, "bad-sha.json"));
            var badShaPath = Path.Combine(root, "bad-sha.json");
            Packer.Stamp(badShaPath, new string('0', 64), size, signature);
            ExpectFailure(() => Verify.Check(Description.Load(badShaPath), archive1, publicKey),
                "verify отклоняет чужой sha256");

            var foreignKey = SignTool.Keygen().PrivateKey;
            var foreignSignature = SignTool.Sign(foreignKey, $"{d.Id}|{d.Version}|{sha256}");
            File.Copy(descriptionPath, Path.Combine(root, "bad-sig.json"));
            var badSigPath = Path.Combine(root, "bad-sig.json");
            Packer.Stamp(badSigPath, sha256, size, foreignSignature);
            ExpectFailure(() => Verify.Check(Description.Load(badSigPath), archive1, publicKey),
                "verify отклоняет подпись чужого ключа");

            File.WriteAllBytes(Path.Combine(source, "plugin", "setup.exe"), new byte[] { 1 });
            ExpectFailure(() => Packer.Build(d, source, Path.Combine(root, "forbidden.zip")),
                "упаковка отклоняет .exe");

            File.Delete(Path.Combine(source, "plugin", "setup.exe"));
            File.WriteAllText(Path.Combine(source, "run.bat"), "@echo", new UTF8Encoding(false));
            ExpectFailure(() => Packer.Build(d, source, Path.Combine(root, "outside.zip")),
                "упаковка отклоняет файл вне разрешённых корней");

            Console.WriteLine("SELFTEST OK");
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // временный каталог — не мешаем завершению
            }
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new PackException($"selftest провален: {name}");
        }

        Console.WriteLine($"  ok: {name}");
    }

    private static void ExpectFailure(Action action, string name)
    {
        try
        {
            action();
        }
        catch (PackException)
        {
            Console.WriteLine($"  ok: {name}");
            return;
        }

        throw new PackException($"selftest провален: ожидался отказ — {name}");
    }
}
