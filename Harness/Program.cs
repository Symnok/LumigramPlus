using System;

namespace Lumigram.Harness
{
    /// <summary>
    /// Console head over the protocol core.
    ///
    /// Commands are added as the core grows; for now the only one is the
    /// big-integer self-test, which has to pass before anything above it is
    /// worth writing - the handshake cannot be debugged if the arithmetic
    /// underneath it is suspect.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "test";

            switch (cmd)
            {
                case "test":
                    Console.WriteLine("Lumigram core self-test");
                    Console.WriteLine();
                    Console.WriteLine("== bigint ==");
                    bool bigint = BigIntTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== crypto ==");
                    bool crypto = CryptoTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== tl / rsa ==");
                    bool tl = TlTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== links ==");
                    bool links = LinkTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== calls ==");
                    bool calls = CallTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== voip wire format ==");
                    bool voip = VoipTests.RunAll();
                    Console.WriteLine();
                    Console.WriteLine("== jitter buffer ==");
                    bool jitter = JitterTests.RunAll();

                    bool all = bigint && crypto && tl && links && calls && voip && jitter;
                    Console.WriteLine();
                    Console.WriteLine(all ? "ALL PASS" : "FAILURES PRESENT");
                    return all ? 0 : 1;

                case "bigint":
                    return BigIntTests.RunAll() ? 0 : 1;

                case "crypto":
                    return CryptoTests.RunAll() ? 0 : 1;

                case "tl":
                    return TlTests.RunAll() ? 0 : 1;

                case "handshake":
                    return HandshakeCommand.Run(args);

                case "nearestdc":
                    return ApiCommand.RunNearestDc(args);

                case "sendcode":
                    return LoginCommand.RunSendCode(args);

                case "signin":
                    return LoginCommand.RunSignIn(args);

                case "password":
                    return LoginCommand.RunPassword(args);

                case "send":
                    return MessageCommand.RunSend(args);

                case "history":
                    return MessageCommand.RunHistory(args);

                case "dialogs":
                    return MessageCommand.RunDialogs(args);

                case "listen":
                    return ListenCommand.Run(args);

                case "sendphoto":
                    return MediaCommand.RunSend(args);

                case "resolve":
                    return ResolveCommand.Run(args);

                case "media":
                    return MediaCommand.Run(args);

                case "qrlogin":
                    return QrLoginCommand.Run(args);

                case "qrdump":
                    return QrTests.Dump(args);

                case "voicemake":
                    return VoiceCommand.Make(args);

                case "voice":
                    return VoiceCommand.Run(args);

                case "skewtest":
                    return ClockSkewCommand.Run(args);

                case "difftest":
                    return ListenCommand.RunDiffTest(args);

                default:
                    Console.WriteLine("usage: Lumigram.Harness [test|bigint|crypto|tl|handshake|nearestdc|sendcode <phone> [host]|signin <code>|qrlogin|password <pw>|send <text>|history [n]|dialogs [n]|listen [seconds]|voice <file.opus>|voicemake <out.opus>|skewtest|difftest]");
                    return 2;
            }
        }
    }
}
