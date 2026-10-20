using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
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
        public bool Deleted;
    }

    internal sealed class SyncNoteRecord
    {
        public string Id = "";
        public string Text = "";
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public long UpdatedAtTicks;
        public string DeviceId = "";
        public bool Deleted;
    }

    internal sealed class SyncPacket
    {
        public string DeviceId = "";
        public List<SyncWordRecord> Words = new List<SyncWordRecord>();
        public List<SyncNoteRecord> Notes = new List<SyncNoteRecord>();
    }

    internal static class LanSyncClient
    {
        private const int ConnectTimeoutMilliseconds = 15000;
        private const int IoTimeoutMilliseconds = 120000;
        private const int MaxJsonLength = 16 * 1024 * 1024;

        public static SyncPacket Sync(string host, int port, SyncPacket localPacket)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxJsonLength;
            serializer.RecursionLimit = 128;
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMilliseconds)) throw new TimeoutException("连接同步主机超时。");
                client.EndConnect(connect);
                client.ReceiveTimeout = IoTimeoutMilliseconds;
                client.SendTimeout = IoTimeoutMilliseconds;

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
