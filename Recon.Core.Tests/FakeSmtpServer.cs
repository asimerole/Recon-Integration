using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Recon.Core.Tests;

/// <summary>
/// Minimal plain-text SMTP server on 127.0.0.1. Refuses any RCPT containing "bad" with 550,
/// records every accepted message (its RCPTs and raw DATA).
/// </summary>
internal sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public ConcurrentQueue<(List<string> Rcpt, string Data)> Messages { get; } = new();

    public FakeSmtpServer()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 fake ESMTP");
        var rcpt = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            string cmd = line.ToUpperInvariant();
            if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO")) await writer.WriteLineAsync("250 fake");
            else if (cmd.StartsWith("MAIL FROM")) { rcpt = []; await writer.WriteLineAsync("250 OK"); }
            else if (cmd.StartsWith("RCPT TO"))
            {
                if (cmd.Contains("BAD")) await writer.WriteLineAsync("550 5.1.1 No such user");
                else { rcpt.Add(line[(line.IndexOf('<') + 1)..line.IndexOf('>')]); await writer.WriteLineAsync("250 OK"); }
            }
            else if (cmd == "DATA")
            {
                await writer.WriteLineAsync("354 go");
                var data = new StringBuilder();
                while (await reader.ReadLineAsync() is { } d && d != ".") data.AppendLine(d);
                Messages.Enqueue((rcpt, data.ToString()));
                await writer.WriteLineAsync("250 queued");
            }
            else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); return; }
            else if (cmd == "RSET") { rcpt = []; await writer.WriteLineAsync("250 OK"); }
            else await writer.WriteLineAsync("250 OK");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
