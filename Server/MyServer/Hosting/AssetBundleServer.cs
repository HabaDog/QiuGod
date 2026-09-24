using System.Net;

namespace MyServer;

/// <summary>
/// Minimal HTTP host for the AssetBundle output directory.
/// </summary>
public sealed class AssetBundleServer : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _rootDirectoryPrefix;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cancellation = new();

    public AssetBundleServer(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _rootDirectoryPrefix = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;
    }

    public void Start(string listenHost, int port)
    {
        if (!Directory.Exists(_rootDirectory))
        {
            throw new DirectoryNotFoundException($"AssetBundle directory was not found: {_rootDirectory}");
        }

        var prefixHost = listenHost is "0.0.0.0" or "*" ? "+" : listenHost;
        _listener.Prefixes.Add($"http://{prefixHost}:{port}/");
        _listener.Start();
        _ = Task.Run(ListenAsync);
        Console.WriteLine($"AssetBundle HTTP server started: http://{listenHost}:{port}/");
        Console.WriteLine($"AssetBundle directory: {_rootDirectory}");
    }

    public void Stop()
    {
        _cancellation.Cancel();
        if (_listener.IsListening)
        {
            _listener.Stop();
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
        _cancellation.Dispose();
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[AssetBundle] Request loop error: {exception.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var relativePath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? string.Empty)
                .TrimStart('/', '\\');
            var filePath = Path.GetFullPath(Path.Combine(_rootDirectory, relativePath));

            if (!filePath.StartsWith(_rootDirectoryPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            var extension = Path.GetExtension(filePath);
            context.Response.ContentType = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                ? "text/plain; charset=utf-8"
                : extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? "application/json; charset=utf-8"
                    : "application/octet-stream";
            if (extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers[HttpResponseHeader.CacheControl] = "no-cache, no-store, must-revalidate";
            }
            context.Response.ContentLength64 = new FileInfo(filePath).Length;

            await using var file = File.OpenRead(filePath);
            await file.CopyToAsync(context.Response.OutputStream, _cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[AssetBundle] Request failed: {exception.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            context.Response.Close();
        }
    }
}
