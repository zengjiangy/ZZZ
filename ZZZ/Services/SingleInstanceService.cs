using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace ZZZ.Services;

public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\ZZZ.Browser.SingleInstance";
    private const string PipeName = "ZZZ.Browser.CommandPipe";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _ownsMutex;

    public SingleInstanceService()
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
    }

    public bool IsPrimary => _ownsMutex;

    public void StartListening(Action<string> openUrl)
    {
        if (!IsPrimary) return;
        _ = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cancellation.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) is not null)
                        if (NormalizeTarget(line) is { } target) openUrl(target);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { }
                catch (ObjectDisposedException) { break; }
            }
        });
    }

    public async Task ForwardAsync(IEnumerable<string> arguments)
    {
        var targets = arguments.Select(NormalizeTarget).Where(x => x is not null).Cast<string>().ToArray();
        if (targets.Length == 0) return;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(750);
                using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
                foreach (var target in targets) await writer.WriteLineAsync(target);
                return;
            }
            catch (TimeoutException) { await Task.Delay(150); }
            catch (IOException) { await Task.Delay(150); }
        }
    }

    public static string? NormalizeTarget(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input!.Trim().Trim('"');
        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile)) return uri.AbsoluteUri;
        if (File.Exists(input)) return new Uri(Path.GetFullPath(input)).AbsoluteUri;
        return null;
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _ownsMutex = false;
        }
        _mutex.Dispose();
    }
}
