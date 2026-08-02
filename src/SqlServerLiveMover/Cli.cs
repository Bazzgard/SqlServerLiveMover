namespace SqlServerLiveMover;

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var configPath = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                         ?? "mover.json";
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
            Console.WriteLine("停止要求を受け付けました。現在の安全な区切りで終了します...");
        };

        try
        {
            var config = AppConfig.Load(configPath);
            var engine = new MigrationEngine(config);
            switch (command)
            {
                case "preflight":
                    await engine.PreflightAsync(cancellation.Token);
                    break;
                case "copy":
                    await engine.CopyAsync(cancellation.Token);
                    break;
                case "verify":
                    await engine.VerifyAsync(cancellation.Token);
                    break;
                default:
                    Console.Error.WriteLine($"不明なコマンドです: {command}");
                    PrintHelp();
                    return 1;
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.WriteLine("停止しました。処理中だった移行先テーブルへの変更はロールバックされます。");
            return 130;
        }
        catch (ConfigException exception)
        {
            Console.Error.WriteLine($"設定エラー: {exception.Message}");
            return 2;
        }
        catch (VerificationException exception)
        {
            Console.Error.WriteLine($"検証エラー: {exception.Message}");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"エラー: {exception.Message}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SqlServerLiveMover - SQL Server部分テーブル・一回コピーCLI

            Usage:
              SqlServerLiveMover preflight [config.json]
              SqlServerLiveMover copy      [config.json]
              SqlServerLiveMover verify    [config.json]

            Commands:
              preflight  接続、主キー、列、読み取り分離設定を検査
              copy       対象テーブルを一度だけ一括コピー
              verify     移行元と移行先の件数を比較

            Ctrl+Cで安全に停止できます。詳細はREADME.mdを参照してください。
            """);
    }
}
