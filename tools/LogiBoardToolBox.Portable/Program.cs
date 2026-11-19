using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LogiBoardToolBox.Portable;

internal static class Program
{
    private const string AppName = "LogiBoardToolBox.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        var verify = args.Contains("--catalog-verify") || args.Contains("--input-verify") || args.Contains("--ui-verify");
        try
        {
            using var payload = typeof(Program).Assembly.GetManifestResourceStream("PortablePayload.zip")
                ?? throw new InvalidDataException("便携版资源缺失。");
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            payload.Position = 0;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LogiBoardToolBox", "Portable");
            var directory = Path.Combine(root, hash);
            PrepareApplication(payload, root, directory, hash);

            var start = new ProcessStartInfo(Path.Combine(directory, AppName))
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                WindowStyle = verify ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var app = Process.Start(start) ?? throw new InvalidOperationException("无法启动应用程序。");
            if (!verify) return 0;
            app.WaitForExit();
            return app.ExitCode;
        }
        catch (Exception error)
        {
            if (verify) Console.Error.WriteLine(error);
            else MessageBoxW(0, error.Message, "罗技鼠标板载工具箱", 0x10);
            return 1;
        }
    }

    private static void PrepareApplication(Stream payload, string root, string directory, string hash)
    {
        Directory.CreateDirectory(root);
        using var unpackLock = new Mutex(false, @"Local\LogiBoardToolBox.Portable." + hash);
        try
        {
            if (!unpackLock.WaitOne(TimeSpan.FromSeconds(30))) throw new TimeoutException("等待程序资源展开超时。");
        }
        catch (AbandonedMutexException) { } // A prior launch ended before it finished extracting.
        try
        {
            if (IsReady(directory)) return;
            var staging = Path.Combine(root, ".unpack-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(payload, staging);
                if (!IsReady(staging)) throw new InvalidDataException("便携版资源不完整。");
                if (Directory.Exists(directory)) Directory.Move(directory, Path.Combine(root, ".incomplete-" + Guid.NewGuid().ToString("N")));
                Directory.Move(staging, directory);
            }
            finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        }
        finally { unpackLock.ReleaseMutex(); }
    }

    private static bool IsReady(string directory) => File.Exists(Path.Combine(directory, AppName))
        && File.Exists(Path.Combine(directory, "resources.pri"))
        && File.Exists(Path.Combine(directory, "Microsoft.UI.Xaml.dll"));

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
