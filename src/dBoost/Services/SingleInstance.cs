using System.IO;
using System.IO.Pipes;
using System.Text;

namespace DBoost.App.Services;

/// <summary>
/// Keeps one dBoost window. Explorer's right-click menu starts one process per selected file; every
/// process after the first sends its file over a named pipe to the first one and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string DefaultId = "dBoost-3f6a1c2e-6b1d-4b8e-9d0b-6a7c5e2f9a11";

    private static string PipeNameFor(string id) => $"{id}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource cts = new();

    /// <summary>Raised on a background thread with the files another dBoost was started with.</summary>
    public event Action<string[]>? FilesReceived;

    private SingleInstance(Mutex m, string id)
    {
        mutex = m;
        pipeName = PipeNameFor(id);
        _ = Task.Run(ServeAsync);
    }

    /// <param name="id">Only tests pass their own; the app always uses the default.</param>
    public static SingleInstance? TryBecomePrimary(string id = DefaultId)
    {
        var m = new Mutex(true, $@"Local\{id}", out bool created);
        if (created) return new SingleInstance(m, id);
        m.Dispose();
        return null;
    }

    /// <summary>Hands files to the running dBoost and asks it to come to the front. False if it did not answer.</summary>
    public static bool SendToPrimary(string[] files, string id = DefaultId)
    {
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            using var client = new NamedPipeClientStream(".", PipeNameFor(id), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(5000);
            using var w = new StreamWriter(client, new UTF8Encoding(false));
            foreach (string f in files) w.WriteLine(Path.GetFullPath(f));
            w.WriteLine(); // empty line ends the message ("just show the window" when there are no files)
            w.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cts.Token).ConfigureAwait(false);
                using var r = new StreamReader(server, Encoding.UTF8);
                var files = new List<string>();
                string? line;
                while ((line = await r.ReadLineAsync(cts.Token).ConfigureAwait(false)) is { Length: > 0 })
                    files.Add(line);
                FilesReceived?.Invoke(files.ToArray());
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(100).ConfigureAwait(false); }
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
    }
}
