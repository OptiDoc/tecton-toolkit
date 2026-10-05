using Pack;

return ProgramEntry.Run(args);

internal static class ProgramEntry
{
    public static int Run(string[] args)
    {
        try
        {
            return Dispatch(args);
        }
        catch (PackException ex)
        {
            Console.Error.WriteLine($"ОШИБКА: {ex.Message}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 2;
        }

        var options = ParseOptions(args.Skip(1).ToArray());
        switch (args[0])
        {
            case "keygen":
            {
                var (privateKey, publicKey) = SignTool.Keygen();
                Console.WriteLine($"private={privateKey}");
                Console.WriteLine($"public={publicKey}");
                return 0;
            }

            case "pack":
            {
                var d = Description.Load(Required(options, "description"));
                var output = Required(options, "output");
                var (sha256, size) = Packer.Build(d, Required(options, "source"), output);
                Console.WriteLine($"path={Path.GetFullPath(output)}");
                Console.WriteLine($"sha256={sha256}");
                Console.WriteLine($"size={size}");
                return 0;
            }

            case "stamp":
            {
                var descriptionPath = Required(options, "description");
                Packer.Stamp(descriptionPath, Required(options, "sha256"),
                    long.Parse(Required(options, "size")), Required(options, "signature"));
                Console.WriteLine($"stamped={Path.GetFullPath(descriptionPath)}");
                return 0;
            }

            case "sign":
            {
                var key = SignTool.ResolveKey(Required(options, "key"));
                var d = Description.Load(Required(options, "description"));
                var archive = Required(options, "archive");
                Console.WriteLine(SignTool.Sign(key, $"{d.Id}|{d.Version}|{Packer.Sha256File(archive)}"));
                return 0;
            }

            case "pub":
            {
                var key = SignTool.ResolveKey(Required(options, "key"));
                Console.WriteLine(SignTool.PublicFromPrivate(key));
                return 0;
            }

            case "verify":
            {
                var d = Description.Load(Required(options, "description"));
                string? publicKey = options.TryGetValue("public-key", out var k)
                    ? SignTool.ResolveKey(k)
                    : null;
                var sha256 = Verify.Check(d, Required(options, "archive"), publicKey);
                Console.WriteLine($"sha256={sha256}");
                Console.WriteLine("VERIFY OK");
                return 0;
            }

            case "selftest":
                SelfTest.Run();
                return 0;

            default:
                Console.Error.WriteLine($"ОШИБКА: неизвестная команда «{args[0]}»");
                Usage();
                return 2;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new PackException($"неожиданный аргумент «{args[i]}»");
            }

            if (i + 1 >= args.Length)
            {
                throw new PackException($"у «{args[i]}» нет значения");
            }

            result[args[i][2..]] = args[i + 1];
            i++;
        }

        return result;
    }

    private static string Required(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out var value) || value.Length == 0)
        {
            throw new PackException($"не задан обязательный аргумент --{name}");
        }

        return value;
    }

    private static void Usage()
    {
        Console.Error.WriteLine("""
            tecton-toolkit — упаковка, подпись и проверка архивов расширений (стандарт Tecton)

            Команды:
              keygen                                      пара ключей Эдвардс-25519 (base64, 32 байта)
              pack    --description m.json --source dir --output a.zip
              stamp   --description m.json --sha256 H --size N --signature S
              sign    --key K|--key @file --description m.json --archive a.zip
              pub     --key K|--key @file            публичный ключ из приватного
              verify  --description m.json --archive a.zip [--public-key K|--public-key @file]
              selftest
            """);
    }
}
