using System.Net;
using PollEditorBot.Loggers;

namespace PollEditorBot;

/// <summary>
/// A minimal HTTP server that listens on PORT (default 8080).
/// Render pings this endpoint to detect the service is up.
/// An external uptime monitor (e.g. UptimeRobot) should ping GET /
/// every 5 minutes to prevent Render's free-tier from sleeping.
/// </summary>
public class KeepAliveServer
{
    readonly ILogger _logger;
    readonly int _port;

    public KeepAliveServer(ILogger logger)
    {
        _logger = logger;
        _port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8080;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => RunAsync(cancellationToken), cancellationToken);
    }

    async Task RunAsync(CancellationToken cancellationToken)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{_port}/");

        try
        {
            listener.Start();
            _logger.LogInformationLine($"Keep-alive HTTP server listening on port {_port}");

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var response = ctx.Response;
                        response.StatusCode = 200;
                        response.ContentType = "text/plain; charset=utf-8";
                        var body = System.Text.Encoding.UTF8.GetBytes("OK - Bot is alive 🤖");
                        response.ContentLength64 = body.Length;
                        await response.OutputStream.WriteAsync(body, cancellationToken);
                        response.OutputStream.Close();
                    }
                    catch { /* ignore per-request errors */ }
                }, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformationLine($"Keep-alive server error: {ex.Message}");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }
}
