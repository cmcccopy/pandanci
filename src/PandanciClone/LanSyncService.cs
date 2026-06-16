using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PandanciClone
{
    internal sealed class SyncWordRecord
    {
        public string Word = "";
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public long LastReviewTicks;
        public long NextReviewTicks;
        public int Score;
        public int Level;
        public bool Flag1;
        public bool Flag2;
        public long UpdatedAtTicks;
        public string DeviceId = "";
    }

    internal sealed class SyncPacket
    {
        public string DeviceId = "";
        public List<SyncWordRecord> Words = new List<SyncWordRecord>();
    }

    internal sealed class LanSyncServer : IDisposable
    {
        private readonly int _port;
        private readonly Func<SyncPacket, SyncPacket> _syncHandler;
        private readonly JavaScriptSerializer _serializer = new JavaScriptSerializer();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        public LanSyncServer(int port, Func<SyncPacket, SyncPacket> syncHandler)
        {
            _port = port;
            _syncHandler = syncHandler;
        }

        public int Port
        {
            get { return _port; }
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;
            try
            {
                if (_listener != null) _listener.Stop();
            }
            catch
            {
            }
        }

        private void Run()
        {
            while (_running)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
                catch
                {
                    if (!_running) return;
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    string requestJson = reader.ReadToEnd();
                    SyncPacket request = _serializer.Deserialize<SyncPacket>(requestJson);
                    SyncPacket response = _syncHandler == null ? new SyncPacket() : _syncHandler(request);
                    writer.Write(_serializer.Serialize(response));
                    writer.Flush();
                }
            }
        }
    }

    internal static class LanSyncClient
    {
        public static SyncPacket Sync(string host, int port, SyncPacket localPacket)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(8000)) throw new TimeoutException("连接同步主机超时。");
                client.EndConnect(connect);
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;

                using (NetworkStream stream = client.GetStream())
                {
                    byte[] request = Encoding.UTF8.GetBytes(serializer.Serialize(localPacket));
                    stream.Write(request, 0, request.Length);
                    stream.Flush();
                    try { client.Client.Shutdown(SocketShutdown.Send); }
                    catch { }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string responseJson = reader.ReadToEnd();
                        return serializer.Deserialize<SyncPacket>(responseJson);
                    }
                }
            }
        }
    }
}
