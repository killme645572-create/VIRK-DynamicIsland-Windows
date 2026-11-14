using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Island
{
    // Крошечный HTTP-приемник на 127.0.0.1: хуки Claude Code шлют сюда `curl -X POST .../claude/<событие>`.
    // Слушаем только локальный адрес, поэтому из сети достучаться нельзя.
    static class ClaudeHookServer
    {
        public static event Action<string> Received;   // "working" | "attention" | "done"

        static TcpListener _listener;
        static volatile bool _run;

        public static bool Start(int port)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
            }
            catch { _listener = null; return false; }   // порт занят — статус Claude Code просто не работает
            _run = true;
            _ = Task.Run(Loop);
            return true;
        }

        public static void Stop()
        {
            _run = false;
            try { _listener?.Stop(); } catch { }
        }

        static async Task Loop()
        {
            while (_run)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(client));
            }
        }

        static async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 2000;
                    client.SendTimeout = 2000;
                    var stream = client.GetStream();
                    var buf = new byte[4096];
                    int n = await stream.ReadAsync(buf, 0, buf.Length);
                    var first = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();   // "POST /claude/done HTTP/1.1"
                    var parts = first.Split(' ');

                    string kind = null;
                    if (parts.Length >= 2 && parts[0] == "POST" && parts[1].StartsWith("/claude/"))
                        kind = parts[1].Substring("/claude/".Length);

                    var resp = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(resp, 0, resp.Length);

                    if (kind == "working" || kind == "attention" || kind == "done")
                        Received?.Invoke(kind);
                }
                catch { }
            }
        }
    }
}
